using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.App.Capture;

/// <summary>Точный target измерения и факты окружения для отчёта.</summary>
/// <param name="InstanceId">Exact MuMu identity.</param>
/// <param name="Endpoint">ADB endpoint, разрешённый по identity.</param>
/// <param name="MuMuVersion">Версия установки MuMu.</param>
/// <param name="AndroidVersion">Версия Android экземпляра по данным MuMu.</param>
internal sealed record CaptureMeasurementTarget(MuMuInstanceId InstanceId, AndroidEndpoint Endpoint, string MuMuVersion,
    string AndroidVersion);

/// <summary>Итог worker-процесса одного транспорта.</summary>
/// <param name="Result">Итог серии либо отказ worker-а, выраженный как FAILED/BLOCKED.</param>
/// <param name="WallMs">Время жизни worker-процесса, включая старт хоста.</param>
/// <param name="ExitCode">Код выхода; null, если процесс остановлен по сроку или не запустился.</param>
/// <param name="DebugReport">Каталог Debug Mode worker-а, если он был включён.</param>
internal sealed record CaptureWorkerOutcome(CaptureProbeResult Result, double WallMs, int? ExitCode, string? DebugReport);

/// <summary>
/// Запускает <c>agent capture-probe</c> текущего исполняемого файла отдельным процессом с ограниченным временем
/// жизни: зависший провайдер (DLL, видеодекодер, helper) не удерживает родительскую команду дольше бюджета.
/// Stdout worker-а — единственный JSON envelope того же контракта.
/// </summary>
internal static class CaptureWorker
{
    internal const string ProbeCommand = "capture-probe";
    private static readonly TimeSpan HostGrace = TimeSpan.FromSeconds(15);

    internal static async Task<CaptureWorkerOutcome> RunAsync(CaptureMeasurementTarget target, ScreenshotMethod method,
        int samples, TimeSpan budget, string? configurationPath, string? debugSession, CancellationToken cancellationToken)
    {
        string name = ScreenshotMethodNames.ToName(method);
        string? executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).StartsWith("AzurPilot", StringComparison.OrdinalIgnoreCase))
        {
            return new(Rejected(name, CaptureVerdict.Blocked, "worker_executable_unavailable"), 0, null, null);
        }

        int timeout = (int)Math.Clamp(budget.TotalMilliseconds, 1, AgentRequest.MaximumTimeoutMilliseconds);
        List<string> arguments =
        [
            "agent", ProbeCommand, "--target", target.InstanceId.ToString(), "--method", name,
            "--samples", samples.ToString(CultureInfo.InvariantCulture),
            "--timeout-ms", timeout.ToString(CultureInfo.InvariantCulture),
        ];
        if (configurationPath is not null)
        {
            arguments.AddRange(["--config", configurationPath]);
        }

        if (debugSession is not null)
        {
            arguments.AddRange([AgentDebugRun.ArgumentName, debugSession]);
        }

        WindowsProcessRunner runner = new(new WorkerFailureProjection());
        long started = Stopwatch.GetTimestamp();
        ApplicationResult<WindowsProcessOutcome> run = await runner.RunAsync(new WindowsProcessRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromMilliseconds(timeout) + HostGrace,
            StandardOutputMode = WindowsProcessStandardOutputMode.Binary,
            MaximumStandardOutputBytes = AgentJson.MaximumOutputBytes + 1,
        }, cancellationToken).ConfigureAwait(false);
        double wall = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        if (run.IsFailure)
        {
            string reason = run.FailureInfo!.Details?.GetValueOrDefault("reason") ?? run.FailureInfo.Code;
            return new(Rejected(name, CaptureVerdict.Failed, reason), wall, null, null);
        }

        WindowsProcessOutcome outcome = run.Value!;
        string? report = DebugReport(outcome.StandardError);
        if (!outcome.StandardOutputBinaryComplete)
        {
            return new(Rejected(name, CaptureVerdict.Failed, "worker_output_incomplete"), wall, outcome.ExitCode, report);
        }

        return new(Parse(name, outcome.StandardOutputBytes), wall, outcome.ExitCode, report);
    }

    internal static CaptureProbeResult Parse(string method, byte[] output)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("verdict", out _))
            {
                CaptureProbeResult? parsed = data.Deserialize<CaptureProbeResult>(AgentJson.Options);
                if (parsed is not null && parsed.Method == method)
                {
                    return parsed;
                }
            }

            string? code = root.TryGetProperty("failure", out JsonElement failure) && failure.ValueKind == JsonValueKind.Object
                && failure.TryGetProperty("code", out JsonElement value) ? value.GetString() : null;
            return code == ApplicationFailure.TargetBusy
                ? Rejected(method, CaptureVerdict.Blocked, "target_busy")
                : Rejected(method, CaptureVerdict.Failed, "worker_failed:" + (code ?? "unknown"));
        }
        catch (JsonException)
        {
            return Rejected(method, CaptureVerdict.Failed, "worker_output_invalid");
        }
    }

    internal static CaptureProbeResult Rejected(string method, CaptureVerdict verdict, string reason)
        => new() { Method = method, Verdict = CaptureVerdictNames.ToName(verdict), Reason = reason };

    // Путь отчёта Debug Mode worker-а сообщается одной структурированной строкой stderr.
    private static string? DebugReport(string standardError)
    {
        foreach (string line in standardError.Split('\n'))
        {
            if (!line.Contains("\"debugReport\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("debugReport", out JsonElement value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private sealed class WorkerFailureProjection : IProcessFailureProjection
    {
        public ApplicationFailure StartFailed(string executablePath, Exception? exception) => Failure("worker_start_failed");

        public ApplicationFailure TimedOut(string executablePath, TimeSpan elapsed, int standardOutputCharacters,
            int standardErrorCharacters) => Failure("worker_timeout");

        public ApplicationFailure Cancelled() => new()
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = "Измерение отменено.",
        };

        private static ApplicationFailure Failure(string reason) => new()
        {
            Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
            Message = "Worker измерения не завершился штатно.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason },
        };
    }
}
