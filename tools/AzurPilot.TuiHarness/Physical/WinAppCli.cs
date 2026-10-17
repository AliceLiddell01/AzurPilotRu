using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AzurPilot.TuiHarness.Physical;

internal sealed record WinAppWindow(long Hwnd, int ProcessId, string ProcessName, string Title, int Width, int Height);

internal sealed record WinAppCapture(string Path, int Width, int Height, string CaptureMethod,
    string Limitation, JsonElement RawMetadata, bool TargetRevalidated, WinAppWindow Target,
    int SourceWindowWidth, int SourceWindowHeight, int CompositeHeaderHeight);

internal sealed record WinAppProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal delegate Task<WinAppProcessResult> WinAppProcessRunner(
    string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

internal sealed class WinAppCli
{
    private const int MaximumOutputCharacters = 1024 * 1024;
    private readonly string executable;
    private readonly TimeSpan timeout;
    private readonly WinAppProcessRunner runner;

    internal WinAppCli(string executable = "winapp.exe", int timeoutMs = 10000, WinAppProcessRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (timeoutMs is < 1 or > 30000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Таймаут WinApp: 1–30000 ms.");
        }

        this.executable = executable;
        timeout = TimeSpan.FromMilliseconds(timeoutMs);
        this.runner = runner ?? RunProcessAsync;
    }

    internal async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        WinAppProcessResult result = await ExecuteAsync(["--version"], cancellationToken);
        string version = result.StandardOutput.Trim();
        if (version.Length is 0 or > 256 || version.Any(char.IsControl))
        {
            throw new HarnessBlockedException("WinApp не сообщил однозначную версию CLI.");
        }

        return version;
    }

    internal async Task<IReadOnlyDictionary<string, string>> ValidateHelpAsync(CancellationToken cancellationToken,
        bool interactive = false)
    {
        List<(string[] Arguments, string[] Required)> commands =
        [
            (["--help"], ["ui"]),
            (["ui", "--help"], ["list-windows", "screenshot", "inspect"]),
            (["ui", "list-windows", "--help"], ["--json"]),
            (["ui", "screenshot", "--help"], ["--output", "--json"]),
            (["ui", "inspect", "--help"], ["--json"]),
        ];
        if (interactive)
        {
            commands.Add((["ui", "send-keys", "--help"], ["--via", "send-input", "--json"]));
        }

        Dictionary<string, string> evidence = new(StringComparer.Ordinal);
        foreach ((string[] arguments, string[] required) in commands)
        {
            WinAppProcessResult result = await ExecuteAsync(arguments, cancellationToken);
            string help = result.StandardOutput;
            if (required.Any(value => !help.Contains(value, StringComparison.Ordinal)))
            {
                throw new HarnessBlockedException("Help установленного WinApp не подтверждает нужный CLI-контракт: "
                    + string.Join(' ', arguments));
            }

            evidence.Add(string.Join(' ', arguments), help);
        }

        return evidence;
    }

    internal async Task<JsonElement> InspectAsync(WinAppWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Hwnd <= 0 || window.ProcessId <= 0
            || !string.Equals(window.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessBlockedException("UIA inspect требует доказанного окна Windows Terminal.");
        }

        WinAppProcessResult result = await ExecuteAsync(
            ["ui", "inspect", "-w", window.Hwnd.ToString(CultureInfo.InvariantCulture), "--json"], cancellationToken);
        using JsonDocument document = ParseJson(result.StandardOutput);
        JsonElement metadata = document.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("windows", out JsonElement windows)
            || windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1
            || windows[0].ValueKind != JsonValueKind.Object || Number(windows[0], "hwnd") != window.Hwnd)
        {
            throw new HarnessBlockedException("UIA inspect не подтвердил единственное выбранное окно.");
        }

        return metadata.Clone();
    }

    internal async Task<JsonElement> SendKeysAsync(WinAppWindow window, string keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(keys);
        if (window.Hwnd <= 0 || window.ProcessId <= 0 || keys.Length > 256
            || !string.Equals(window.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessBlockedException("Ввод требует доказанного окна и ограниченной последовательности клавиш.");
        }

        WinAppProcessResult result = await ExecuteAsync(
            ["ui", "send-keys", keys, "-w", window.Hwnd.ToString(CultureInfo.InvariantCulture),
                "--via", "send-input", "--json"], cancellationToken);
        using JsonDocument document = ParseJson(result.StandardOutput);
        JsonElement metadata = document.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object || Number(metadata, "hwnd") != window.Hwnd
            || Text(metadata, "via") != "send-input" || Number(metadata, "actionCount") <= 0
            || metadata.TryGetProperty("warnings", out JsonElement warnings)
                && warnings.ValueKind != JsonValueKind.Null
                && (warnings.ValueKind != JsonValueKind.Array || warnings.GetArrayLength() > 0))
        {
            throw new HarnessBlockedException("WinApp не подтвердил физический ввод в выбранный HWND без предупреждений.");
        }

        return metadata.Clone();
    }

    internal async Task<WinAppWindow> SelectWindowAsync(string marker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        WinAppProcessResult result = await ExecuteAsync(["ui", "list-windows", "-a", "Terminal", "--json"],
            cancellationToken);
        using JsonDocument document = ParseJson(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new HarnessBlockedException("WinApp list-windows не вернул массив окон.");
        }

        List<WinAppWindow> matches = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new HarnessBlockedException("WinApp list-windows содержит некорректную запись окна.");
            }

            // Полное равенство заголовка защищает от префикса/суффикса чужой сессии.
            if (Text(item, "title") != marker)
            {
                continue;
            }

            string? processName = Text(item, "processName");
            long hwnd = Number(item, "hwnd");
            long processId = Number(item, "processId");
            long width = Number(item, "width");
            long height = Number(item, "height");
            if (!string.Equals(processName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                || hwnd <= 0 || processId is <= 0 or > int.MaxValue
                || width is <= 0 or > 32768 || height is <= 0 or > 32768)
            {
                throw new HarnessBlockedException("Окно с маркером не доказало WindowsTerminal, HWND, PID или геометрию.");
            }

            matches.Add(new WinAppWindow(hwnd, (int)processId, processName!, marker, (int)width, (int)height));
        }

        if (matches.Count != 1)
        {
            throw new HarnessBlockedException($"Ожидалось одно окно с полным маркером; найдено: {matches.Count}.");
        }

        return matches[0];
    }

    internal async Task<WinAppCapture> CaptureAsync(WinAppWindow window, string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (window.Hwnd <= 0 || window.ProcessId <= 0
            || !string.Equals(window.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessBlockedException("Захват требует доказанного HWND/PID Windows Terminal.");
        }

        string fullPath = System.IO.Path.GetFullPath(outputPath);
        if (!string.Equals(System.IO.Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase)
            || File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new HarnessBlockedException("PNG должен иметь новое имя: исходные evidence не перезаписываются.");
        }

        WinAppWindow beforeCapture = await SelectWindowAsync(window.Title, cancellationToken);
        EnsureSameWindow(window, beforeCapture);

        WinAppProcessResult result = await ExecuteAsync(
            ["ui", "screenshot", "-w", window.Hwnd.ToString(CultureInfo.InvariantCulture),
                "--output", fullPath, "--json"], cancellationToken);
        using JsonDocument document = ParseJson(result.StandardOutput);
        JsonElement metadata = document.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new HarnessBlockedException("WinApp screenshot не вернул метаданные одного окна.");
        }

        string? reportedPath = Text(metadata, "filePath");
        reportedPath ??= Text(metadata, "path");
        long width = Number(metadata, "width");
        long height = Number(metadata, "height");
        bool sourceWindowValid = TryGetCapturedWindow(metadata, window, out int sourceWidth, out int sourceHeight);
        int compositeHeaderHeight = sourceWindowValid ? (int)height - sourceHeight : -1;
        if (HasMismatchedOptionalIdentity(metadata, window)
            || reportedPath is null || !System.IO.Path.IsPathFullyQualified(reportedPath)
            || !string.Equals(System.IO.Path.GetFullPath(reportedPath), fullPath, StringComparison.OrdinalIgnoreCase)
            || width is <= 0 or > 32768 || height is <= 0 or > 32768
            || !sourceWindowValid || width != sourceWidth || compositeHeaderHeight is < 0 or > 64
            || metadata.TryGetProperty("executionTarget", out _)
            || metadata.TryGetProperty("elementId", out JsonElement element) && element.ValueKind != JsonValueKind.Null)
        {
            string fields = string.Join(',', metadata.EnumerateObject().Select(property => property.Name));
            string windowsSummary = metadata.TryGetProperty("windows", out JsonElement windowsValue)
                ? windowsValue.ValueKind == JsonValueKind.Array
                    ? $"array[{windowsValue.GetArrayLength()}] " + string.Join(';', windowsValue.EnumerateArray()
                        .Take(4).Select(item => $"hwnd={Number(item, "hwnd")};pid={Number(item, "processId")};title={SafeDiagnostic(Text(item, "title") ?? "<нет>")}"))
                    : windowsValue.ValueKind.ToString()
                : "<нет>";
            throw new HarnessBlockedException($"Метаданные PNG не подтверждают единственное локальное исходное окно "
                + $"(поля: {SafeDiagnostic(fields)}; path: {SafeDiagnostic(reportedPath ?? "<нет>")}; "
                + $"размер: {width}x{height}; окно: {window.Width}x{window.Height}; "
                + $"hwnd={Number(metadata, "hwnd")}; pid={Number(metadata, "processId")}; "
                + $"title={SafeDiagnostic(Text(metadata, "windowTitle") ?? "<нет>")}; mode={SafeDiagnostic(Text(metadata, "mode") ?? "<нет>")}; "
                + $"windows={windowsSummary}; json={SafeDiagnostic(metadata.GetRawText())}).");
        }

        await ValidatePngAsync(fullPath, (int)width, (int)height, cancellationToken);
        WinAppWindow afterCapture = await SelectWindowAsync(window.Title, cancellationToken);
        EnsureSameWindow(window, afterCapture);

        string? mode = Text(metadata, "mode")?.ToLowerInvariant();
        string captureMethod = mode switch
        {
            "wgc" => "WGC",
            "printwindow" => "PrintWindow",
            "screen" => "Screen",
            _ => "Unknown",
        };
        string limitation = captureMethod == "Unknown"
            ? "WinApp screenshot JSON не сообщает фактический mode; WGC и fallback PrintWindow не различаются."
            : captureMethod == "WGC"
                ? "WGC подтверждён метаданными WinApp; --focus и --capture-screen не запрашивались."
                : $"WinApp сообщил mode={mode}; требуемый WGC этим захватом не подтверждён.";
        return new WinAppCapture(fullPath, (int)width, (int)height, captureMethod,
            limitation, metadata.Clone(), TargetRevalidated: true, window,
            sourceWidth, sourceHeight, compositeHeaderHeight);
    }

    private static void EnsureSameWindow(WinAppWindow expected, WinAppWindow actual)
    {
        if (actual.Hwnd != expected.Hwnd || actual.ProcessId != expected.ProcessId
            || !string.Equals(actual.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal))
        {
            throw new HarnessBlockedException("Точный HWND/PID/marker Windows Terminal изменился во время захвата.");
        }
    }

    private static bool HasMismatchedOptionalIdentity(JsonElement metadata, WinAppWindow window)
    {
        return metadata.TryGetProperty("hwnd", out _) && Number(metadata, "hwnd") != window.Hwnd
            || metadata.TryGetProperty("processId", out _) && Number(metadata, "processId") != window.ProcessId
            || metadata.TryGetProperty("windowTitle", out _) && Text(metadata, "windowTitle") != window.Title;
    }

    private static bool TryGetCapturedWindow(JsonElement metadata, WinAppWindow window,
        out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!metadata.TryGetProperty("windows", out JsonElement windows)
            || windows.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement[] captured = [.. windows.EnumerateArray().Where(item =>
            item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("captured", out JsonElement value)
                && value.ValueKind == JsonValueKind.True)];
        if (captured.Length != 1)
        {
            return false;
        }

        JsonElement target = captured[0];
        width = (int)Number(target, "width");
        height = (int)Number(target, "height");
        if (Number(target, "hwnd") != window.Hwnd || Text(target, "title") != window.Title
            || width is <= 0 or > 32768 || height is <= 0 or > 32768)
        {
            return false;
        }

        foreach (JsonElement item in windows.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (item.TryGetProperty("captured", out JsonElement wasCaptured)
                && wasCaptured.ValueKind == JsonValueKind.True
                && Number(item, "hwnd") != window.Hwnd)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<WinAppProcessResult> ExecuteAsync(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            WinAppProcessResult result = await runner(executable, arguments, deadline.Token).WaitAsync(deadline.Token);
            if (result.StandardOutput.Length > MaximumOutputCharacters
                || result.StandardError.Length > MaximumOutputCharacters)
            {
                throw new HarnessBlockedException("Вывод WinApp превысил ограничение 1 MiB.");
            }

            if (result.ExitCode != 0)
            {
                string details = SafeDiagnostic(result.StandardError + " " + result.StandardOutput);
                throw new HarnessBlockedException($"WinApp завершился с кодом {result.ExitCode}: {details}");
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HarnessBlockedException($"WinApp не завершился за {timeout.TotalMilliseconds} ms.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new HarnessBlockedException("WinApp CLI недоступен: " + SafeDiagnostic(exception.Message));
        }
    }

    private static JsonDocument ParseJson(string output)
    {
        try
        {
            return JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new HarnessBlockedException("WinApp вернул некорректный JSON; stdout не является машинным ответом.");
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private static long Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number) ? number : -1;
    }

    private static async Task ValidatePngAsync(string path, int width, int height, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new HarnessBlockedException("WinApp сообщил успех без файла PNG.");
        }

        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[24];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new HarnessBlockedException("WinApp создал неполный файл PNG.");
        }

        if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) != width
            || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) != height)
        {
            throw new HarnessBlockedException("PNG header/геометрия не совпадают с метаданными WinApp.");
        }
    }

    private static async Task<WinAppProcessResult> RunProcessAsync(string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Отдельные вызовы не продолжают случайный desktop workflow родительского shell.
        _ = start.Environment.Remove("WINAPP_UI_WORKFLOW_ID");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new HarnessBlockedException("Не удалось запустить WinApp CLI.");
        }

        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken));
            return new WinAppProcessResult(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Процесс успел завершиться между проверкой и Kill.
                }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder output = new();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumOutputCharacters)
            {
                throw new HarnessBlockedException("Вывод WinApp превысил ограничение 1 MiB.");
            }

            _ = output.Append(buffer, 0, read);
        }

        return output.ToString();
    }

    private static string SafeDiagnostic(string text)
    {
        char[] safeCharacters = [.. text.Where(character => !char.IsControl(character)).Take(1024)];
        return new string(safeCharacters).Trim();
    }
}
