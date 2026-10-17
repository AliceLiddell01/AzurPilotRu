using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.TuiHarness;

internal sealed class SessionOwner : IAsyncDisposable
{
    private readonly HarnessRequest _start;
    private readonly string _directory;
    private readonly string _exe;
    private readonly string _exeHash;
    private readonly string _assemblyHash;
    private readonly string _assemblyPath;
    private readonly ConPty _pty;
    private readonly TerminalRenderer _renderer;
    private readonly object _identity;
    private int _capture;
    private bool _disposed;

    private SessionOwner(HarnessRequest start, string directory, string exe, string exeHash, string assemblyPath, string assemblyHash,
        ConPty pty, TerminalRenderer renderer, object identity)
    {
        _start = start;
        _directory = directory;
        _exe = exe;
        _exeHash = exeHash;
        _assemblyHash = assemblyHash;
        _assemblyPath = assemblyPath;
        _pty = pty;
        _renderer = renderer;
        renderer.DiagnosticDirectory = directory;
        _identity = identity;
    }

    internal static async Task<SessionOwner> CreateAsync(HarnessRequest start)
    {
        string directory = Path.GetFullPath(start.Output!);
        string exe = Path.GetFullPath(start.Calibration ? Environment.ProcessPath!
            : start.Exe ?? throw new ArgumentException("Требуется --exe с продуктовым AzurPilot.App.exe."));
        if ((!start.Calibration && Path.GetFileName(exe) != "AzurPilot.App.exe") || !File.Exists(exe))
        {
            throw new ArgumentException("Не найден продуктовый AzurPilot.App.exe.");
        }

        string configRoot = Path.Combine(directory, "runtime");
        _ = Directory.CreateDirectory(configRoot);
        // Настоящий строгий YAML. Настройки сохраняются рядом с этим снимком.
        string configPath = Path.Combine(
            configRoot,
            Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()));
        string legacyJsonPath = Path.Combine(
            configRoot,
            Path.GetFileName(AzurPilotConfigurationPath.GetLegacyJsonRuntimePath()));
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        if (start.LiveInstance is string liveTarget)
        {
            defaults = defaults with { MuMu = defaults.MuMu with { Instance = liveTarget } };
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> configuration = AzurPilotConfigurationStore.Save(
            configPath,
            legacyJsonPath,
            AzurPilotConfigurationLoader.Load(configPath).Value!,
            defaults,
            out _);
        if (configuration.IsFailure)
        {
            throw new HarnessBlockedException(configuration.FailureInfo!.Message);
        }

        string profilePath = Path.GetFullPath(start.Profile ?? Path.Combine(AppContext.BaseDirectory, "profiles", "campbell.json"));
        if (new FileInfo(profilePath).Length > 16384)
        {
            throw new ArgumentException("Профиль терминала превышает 16 KiB.");
        }

        using JsonDocument profileDocument = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        JsonElement profile = profileDocument.RootElement.Clone();
        string arguments = $"--mode {start.Mode} --isolated-ui \"{configRoot}\"";
        if (start.LiveInstance is not null)
        {
            if (start.Authorization != "prepare-and-restore")
            {
                throw new ArgumentException("Изменение состояния в live не разрешено.");
            }

            arguments += " --live-environment-target " + start.LiveInstance;
            JsonElement preflight = await LiveEnvironment.ObserveAsync(exe, configPath, start.LiveInstance);
            await File.WriteAllTextAsync(Path.Combine(directory, "preflight.json"), preflight.GetRawText());
            LiveEnvironment.RequireStopped(preflight, start.LiveInstance);
        }

        if (start.Calibration)
        {
            string prefix = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{typeof(Program).Assembly.Location}\" " : "";
            arguments = prefix + "__calibration";
        }

        string exeHash = Hash(exe);
        string assemblyPath = start.Calibration ? typeof(Program).Assembly.Location : Path.ChangeExtension(exe, ".dll");
        string assemblyHash = Hash(assemblyPath);
        bool denyChildProcesses = start.LiveInstance is null && start.Providers == "disabled";
        ConPty pty = new(exe, arguments, directory, start.Columns, start.Rows, safe: denyChildProcesses,
            live: start.LiveInstance is not null, profilePath: profilePath);
        try
        {
            TerminalRenderer renderer = await TerminalRenderer.CreateAsync(pty, profile, start.Columns, start.Rows);
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(assemblyPath);
            object identity = new
            {
                session = start.Session,
                kind = start.Calibration ? "CALIBRATION_ONLY: не production UI" : "PRODUCTION_TUI",
                ownerPid = Environment.ProcessId,
                consoleProfileBootstrapPid = pty.BootstrapPid,
                conpty = new { backend = "Microsoft.Windows.Console.ConPTY", version = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "conpty.dll")).FileVersion },
                childPid = pty.Pid,
                productionPid = start.Calibration ? (int?)null : pty.Pid,
                exe,
                exeSha256 = exeHash,
                assemblySha256 = assemblyHash,
                version = version.ProductVersion,
                arguments,
                startedUtc = DateTimeOffset.UtcNow,
                profilePath,
                profile,
                initialColumns = start.Columns,
                initialRows = start.Rows,
                terminalEnvironment = new
                {
                    term = Environment.GetEnvironmentVariable("TERM"),
                    colorTerm = Environment.GetEnvironmentVariable("COLORTERM"),
                    termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM"),
                    noColor = Environment.GetEnvironmentVariable("NO_COLOR"),
                    windowsTerminalSession = Environment.GetEnvironmentVariable("WT_SESSION"),
                },
                safe = new
                {
                    activeProcessLimit = denyChildProcesses ? 1 : 0,
                    readOnlyProviders = start.Providers,
                    liveInstance = start.LiveInstance,
                    allowedLiveMutations = start.LiveInstance is null ? "none" : "environment-prepare,mumu-stop",
                    assignedWhileSuspended = true,
                    killOnClose = true,
                    isolatedConfiguration = configPath,
                    externalViewerDisabled = true
                },
                source = await SourceIdentityAsync(AppContext.BaseDirectory),
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "identity.json"), JsonSerializer.Serialize(identity, HarnessJson.Options));
            _ = await renderer.WaitAsync(start.Calibration ? "HARNESS COLOR CALIBRATION" : "AzurPilot", 15000);
            return new SessionOwner(start, directory, exe, exeHash, assemblyPath, assemblyHash, pty, renderer, identity);
        }
        catch
        {
            await pty.DisposeAsync();
            throw;
        }
    }

    internal async Task<HarnessResponse> ExecuteAsync(HarnessRequest request)
    {
        request.Validate();
        if (request.Session != _start.Session)
        {
            throw new ArgumentException("session ID не принадлежит этому владельцу.");
        }

        if (request.Command != "close" && (Hash(_exe) != _exeHash || Hash(_assemblyPath) != _assemblyHash))
        {
            throw new HarnessBlockedException("Бинарник приложения изменился после старта. Нужна новая сессия.");
        }

        object? data;
        switch (request.Command)
        {
            case "status":
                if (!_pty.Alive)
                {
                    throw new HarnessBlockedException($"Процесс приложения уже завершился: 0x{_pty.ExitCode:X8}.");
                }

                data = _identity;
                break;
            case "wait" or "wait-idle":
                JsonElement snapshot = await _renderer.WaitAsync(request.Contains, request.TimeoutMs, request.Command == "wait-idle");
                data = new { matched = request.Contains, columns = snapshot.GetProperty("columns"), rows = snapshot.GetProperty("rows") };
                break;
            case "capture":
                if (_capture >= 64)
                {
                    throw new HarnessBlockedException("Бюджет свидетельств: не более 64 снимков на сессию.");
                }

                string name = $"{++_capture:D3}-{request.Name ?? "capture"}";
                data = await _renderer.CaptureAsync(_directory, name, _identity, request.Contains, request.TimeoutMs);
                break;
            case "text" or "key" or "resize" or "mouse":
                await _renderer.InputAsync(request);
                JsonElement settled = await _renderer.WaitAsync(null, request.TimeoutMs);
                if (request.Command == "mouse" && request.Value == "click" && !_renderer.MouseEffectObserved(settled))
                {
                    return new("NOT_TESTED", _start.Session,
                        "Клик доставлен, но наблюдаемого изменения интерфейса нет; поддержка навигации кликом не доказана.",
                        new { delivered = true, effectObserved = false, reason = "click_effect_not_observed" });
                }

                // PASS означает доставку через ввод xterm/ConPTY. Эффект подтверждает отдельная проверка утверждения.
                data = new { delivered = true, effect = "NOT_TESTED: используйте wait/capture/assertions" };
                break;
            case "close":
                await DisposeAsync();
                data = new { closing = true };
                break;
            default: throw new ArgumentException("Команда не относится к открытой сессии.");
        }

        return new("PASS", _start.Session, "Операция выполнена.", data);
    }

    internal static async Task RunAsync(string requestPath)
    {
        HarnessRequest start = JsonSerializer.Deserialize<HarnessRequest>(await File.ReadAllTextAsync(requestPath), HarnessJson.Options)!;
        // Оболочка агента может иметь TERM=dumb/NO_COLOR. Это окружение CLI, а не профиль нового терминала.
        // Меняем только отдельного владельца сессии и его потомков; чужую WT_SESSION не выдаём за собственную.
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "AzurPilot.TuiHarness");
        Environment.SetEnvironmentVariable("NO_COLOR", null);
        Environment.SetEnvironmentVariable("FORCE_COLOR", null);
        Environment.SetEnvironmentVariable("WT_SESSION", null);
        // Дескриптор не наследуется. При аварии или принудительном завершении владельца Windows завершит также драйвер и Chromium.
        using SafeFileHandle job = ConPty.CreateJob(denyChildren: false, allowBreakaway: start.LiveInstance is not null);
        ConPty.AssignOwnerJob(job);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Process startupClient = Process.GetProcessById(start.StartupClientPid);
        // До первого status владелец сессии принадлежит запускающему CLI. Его обрыв завершает только наш Job.
        _ = Task.Run(async () =>
        {
            if (!await HarnessTimeouts.StartupCompletedAsync(ready.Task, startupClient.WaitForExitAsync(), TimeProvider.System))
            {
                Process.GetCurrentProcess().Kill();
            }
        });
        try
        {
            await using SessionOwner owner = await CreateAsync(start);
            using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(start.LifetimeSeconds));
            Task monitor = Task.Run(async () =>
            {
                while (!lifetime.IsCancellationRequested && !owner._disposed)
                {
                    if (!owner._pty.Alive || !owner._renderer.IsAlive)
                    {
                        await lifetime.CancelAsync();
                        break;
                    }

                    await Task.Delay(200);
                }
            });
            bool close = false;
            while (!close && !lifetime.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = new(PipeName(start.Session!), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(120));
                await pipe.WaitForConnectionAsync(idle.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                HarnessResponse response;
                bool startupStatus = false;
                try
                {
                    HarnessRequest request = await ReadAsync<HarnessRequest>(pipe, idle.Token);
                    startupStatus = request.Command == "status";
                    idle.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMs + HarnessTimeouts.ResponseGraceMs));
                    response = await owner.ExecuteAsync(request).WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs + HarnessTimeouts.ResponseGraceMs), lifetime.Token);
                    close = request.Command == "close";
                }
                catch (Exception exception)
                {
                    await File.WriteAllTextAsync(Path.Combine(start.Output!, "operation-error.txt"), exception.ToString());
                    response = Failure(exception, start.Session);
                    // Не продолжать владеть операцией, таймаут которой мог оставить незавершённую работу.
                    close = exception is TimeoutException or OperationCanceledException;
                }

                await WriteResponseAsync(pipe, response);
                if (startupStatus)
                {
                    _ = ready.TrySetResult();
                }
            }

            await lifetime.CancelAsync();
            await monitor;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(start.Output!, "owner-error.txt"), exception.ToString());
            await File.WriteAllTextAsync(Path.Combine(start.Output!, "owner-error.json"),
                JsonSerializer.Serialize(Failure(exception, start.Session), HarnessJson.Options));
        }
        // Закрытие последнего дескриптора Job завершает владельца и все оставшиеся дочерние процессы.
    }

    internal static string PipeName(string session)
    {
        return "AzurPilot.TuiHarness." + session;
    }

    internal static HarnessResponse Failure(Exception exception, string? session)
    {
        return new(
        exception is ArgumentException or JsonException or FormatException or HarnessAssertionException ? "FAIL" : "BLOCKED", session,
        exception.Message.Length > 1500 ? exception.Message[..1500] : exception.Message);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int size = BitConverter.ToInt32(header);
        if (size is < 1 or > 65536)
        {
            throw new ArgumentException("IPC сообщение превышает бюджет 64 KiB.");
        }

        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, HarnessJson.Options) ?? throw new ArgumentException("Пустое IPC сообщение.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, HarnessJson.Options);
        if (bytes.Length > 65536)
        {
            throw new ArgumentException("IPC ответ превышает бюджет 64 KiB.");
        }

        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task WriteResponseAsync(Stream stream, HarnessResponse response)
    {
        using CancellationTokenSource reply = new(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
        await WriteAsync(stream, response, reply.Token);
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static async Task<object> SourceIdentityAsync(string binaryDirectory)
    {
        // Корень привязан к расположению Harness, а не текущего каталога вызывающей оболочки или чужому GIT_DIR.
        DirectoryInfo? root = new(Path.GetFullPath(binaryDirectory));
        while (root is not null && !(File.Exists(Path.Combine(root.FullName, "AzurPilot.slnx"))
            && File.Exists(Path.Combine(root.FullName, "src", "AzurPilot.App", "AzurPilot.App.csproj"))
            && File.Exists(Path.Combine(root.FullName, "tools", "AzurPilot.TuiHarness", "AzurPilot.TuiHarness.csproj"))))
        {
            root = root.Parent;
        }

        object Unavailable()
        {
            return new
            {
                status = "NOT_TESTED",
                commit = (string?)null,
                workingTree = (string?)null,
                buildBinding = "Корень исходного checkout не подтверждён; доступны только SHA-256 бинарников."
            };
        }

        if (root is null)
        {
            return Unavailable();
        }

        async Task<string?> GitAsync(params string[] arguments)
        {
            ProcessStartInfo info = new("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root.FullName,
            };
            foreach (string key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _ = info.Environment.Remove(key);
            }

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                _ = await error;
                string result = (await output).Trim();
                return process.ExitCode == 0 && result.Length <= 8192 ? result : null;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        try
        {
            string? top = await GitAsync("rev-parse", "--show-toplevel");
            if (top is null || !Path.GetFullPath(top).Equals(root.FullName, StringComparison.OrdinalIgnoreCase)
                || await GitAsync("ls-files", "--error-unmatch", "AzurPilot.slnx", "src/AzurPilot.App/AzurPilot.App.csproj",
                    "tools/AzurPilot.TuiHarness/AzurPilot.TuiHarness.csproj") is null)
            {
                return Unavailable();
            }

            string? commit = await GitAsync("rev-parse", "HEAD");
            string? workingTree = await GitAsync("status", "--porcelain");
            return commit is null || workingTree is null ? Unavailable() : new
            {
                status = "PASS",
                root = root.FullName,
                commit,
                workingTree,
                buildBinding = "Git описывает исходный checkout; соответствие сборки этому HEAD не доказано. SHA-256 EXE + DLL — отдельная идентичность бинарников."
            };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            return Unavailable();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return _renderer.DisposeAsync();
    }
}
