using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace AzurPilot.TuiHarness;

internal static class Program
{
    // Бюджет последовательного создания сессии принадлежит HarnessTimeouts.
    internal const int StartStatusConnectionTimeoutMs = HarnessTimeouts.StartConnectionMs;

    private static async Task<int> Main(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")))
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "AzurPilot.slnx")))
            {
                root = root.Parent;
            }

            if (root is not null)
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH",
                    Path.Combine(root.FullName, "artifacts", "tui-harness", "browsers"));
            }
        }

        if (args is ["__calibration"])
        {
            return Calibration.EmitPattern();
        }

        if (args is ["__console-profile", string profilePath])
        {
            try
            {
                return ConsoleProfile.Apply(profilePath);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync("console-profile-error.json",
                    JsonSerializer.Serialize(SessionOwner.Failure(exception, null), HarnessJson.Options));
                return 2;
            }
        }

        if (args is ["__owner", string requestPath])
        {
            await SessionOwner.RunAsync(requestPath);
            return 0;
        }

        HarnessResponse result;
        HarnessRequest? request = null;
        try
        {
            request = HarnessRequest.Parse(args);
            result = request.Command switch
            {
                "help" => Help(),
                "install-browser" => InstallBrowser(),
                "start" => await StartAsync(request),
                "run" => await Scenarios.RunAsync(request),
                "fidelity" => await Calibration.RunAsync(request),
                "live-environment" => await LiveEnvironment.RunAsync(request),
                _ => await SendAsync(request),
            };
        }
        catch (Exception exception)
        {
            result = SessionOwner.Failure(exception, request?.Session);
        }

        Console.WriteLine(JsonSerializer.Serialize(result, HarnessJson.Options));
        return result.ExitCode;
    }

    internal static async Task<HarnessResponse> StartAsync(HarnessRequest request)
    {
        string id = request.Session ?? Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(request.Output ?? Path.Combine("artifacts", "tui-harness", "sessions"));
        string directory = Path.Combine(root, id);
        _ = Directory.CreateDirectory(directory);
        HarnessRequest start = request with
        {
            Command = "start",
            Session = id,
            Output = directory,
            StartupClientPid = Environment.ProcessId
        };
        string file = Path.Combine(directory, "start.json");
        await using (FileStream exclusive = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(exclusive, start, HarnessJson.Options);
        }

        string? assembly = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? typeof(Program).Assembly.Location : null;
        using Process owner = ConPty.StartOwner(Environment.ProcessPath!, assembly, file);
        try
        {
            HarnessResponse response = await SendAsync(
                new HarnessRequest { Command = "status", Session = id, TimeoutMs = HarnessTimeouts.StartupResponseMs },
                connectionTimeoutMs: StartStatusConnectionTimeoutMs);
            if (response.Status != "PASS")
            {
                _ = await SendAsync(new HarnessRequest { Command = "close", Session = id, TimeoutMs = 1000 });
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
            }

            return response.Status != "PASS"
                ? throw new HarnessBlockedException(response.Message)
                : new("PASS", id, "Изолированная ConPTY-сессия открыта.", new { directory, ownerPid = owner.Id, identity = response.Data });
        }
        catch
        {
            if (!owner.HasExited)
            {
                owner.Kill(entireProcessTree: true);
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
            }

            string error = Path.Combine(directory, "owner-error.json");
            if (File.Exists(error))
            {
                return JsonSerializer.Deserialize<HarnessResponse>(await File.ReadAllTextAsync(error), HarnessJson.Options)!;
            }

            throw;
        }
    }

    private static HarnessResponse InstallBrowser()
    {
        TextWriter original = Console.Out;
        try
        {
            Console.SetOut(Console.Error);
            int code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            return new(code == 0 ? "PASS" : "BLOCKED", null, "Установка Chromium завершена.");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    internal static async Task<HarnessResponse> SendAsync(HarnessRequest request, int? connectionTimeoutMs = null)
    {
        if (request.Session is null)
        {
            throw new ArgumentException("Требуется --session ID открытой сессии.");
        }

        request.Validate();
        int connectTimeoutMs = connectionTimeoutMs ?? request.TimeoutMs;
        if (connectTimeoutMs is < 100 or > StartStatusConnectionTimeoutMs
            || (connectionTimeoutMs is not null && request.Command != "status"))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeoutMs));
        }

        await using NamedPipeClientStream pipe = new(".", SessionOwner.PipeName(request.Session), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(connectTimeoutMs));
        bool connected = false;
        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, timeout.Token);
            connected = true;
            using CancellationTokenSource responseTimeout = new(TimeSpan.FromMilliseconds(request.TimeoutMs + HarnessTimeouts.ResponseGraceMs));
            await SessionOwner.WriteAsync(pipe, request, responseTimeout.Token);
            return await SessionOwner.ReadAsync<HarnessResponse>(pipe, responseTimeout.Token);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            throw new HarnessBlockedException(connected
                ? "Владелец сессии не ответил в пределах срока IPC."
                : "Владелец сессии недоступен: неизвестная, закрытая либо истёкшая сессия.");
        }
    }

    private static readonly string[] Commands = ["start --exe PATH [--profile JSON] [--mode normal|manual|diagnostic] [--providers disabled|read-only] [--output DIR]",
            "run --exe PATH --scenario JSON", "status|close --session ID",
            "text|key --session ID --value TEXT_OR_PLAYWRIGHT_KEY",
            "mouse --session ID --value click|move|wheel-up|wheel-down --x COLUMN --y ROW",
            "resize --session ID --columns N --rows N", "wait --session ID --contains TEXT",
            "capture --session ID [--name NAME] [--contains TEXT]", "fidelity [--profile JSON]",
            "live-environment --exe PATH --instance mumu:<index> --authorize prepare-and-restore", "install-browser"];

    private static HarnessResponse Help()
    {
        return new("PASS", null, "AzurPilot.TuiHarness — Windows ConPTY + xterm.js + Chromium", new
        {
            commands = Commands,
            exitCodes = new { PASS = 0, FAIL = 1, BLOCKED = 2, NOT_TESTED = 3 },
            commandTimeouts = new
            {
                start = HarnessTimeouts.StartProcessMs,
                scenario = HarnessTimeouts.ScenarioProcessMs,
                other = HarnessTimeouts.CommandProcessMs,
            },
            configurationPaths = new
            {
                yaml = Core.Configuration.AzurPilotConfigurationPath.GetDefaultRuntimePath(),
                legacyJson = Core.Configuration.AzurPilotConfigurationPath.GetLegacyJsonRuntimePath(),
            },
            safety = "По умолчанию UI изолирован. Произвольный ввод не разрешает изменение состояния в live.",
        });
    }
}
