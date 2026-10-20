using System.Diagnostics;
using System.Globalization;
using System.Text;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Параметры измерения транспортов.</summary>
/// <param name="Methods">Транспорты в порядке измерения.</param>
/// <param name="Profile">short, full или stress.</param>
/// <param name="Samples">Снимков в серии каждого транспорта.</param>
/// <param name="Calibrate">Сохранить выбор auto при доказанном победителе.</param>
/// <param name="DebugOverhead">Повторить серию победителя с Debug Mode для оценки накладных расходов.</param>
/// <param name="Timeout">Общий срок команды.</param>
/// <param name="ConfigurationPath">Явный --config родителя, передаваемый worker-ам.</param>
internal sealed record CaptureBenchmarkRequest(IReadOnlyList<ScreenshotMethod> Methods, string Profile, int Samples,
    bool Calibrate, bool DebugOverhead, TimeSpan Timeout, string? ConfigurationPath);

/// <summary>Итог команды: краткие данные envelope, отказ и эффект.</summary>
internal sealed record CaptureCommandOutcome(object Data, ApplicationFailure? Failure, string Effect);

/// <summary>
/// Измерение транспортов на доказанном главном экране: проверка Main → worker на каждый транспорт → проверка Main →
/// выбор по корректности, стабильности и p95 → калибровка auto → локальный отчёт. Ввода в игру нет.
/// </summary>
internal static class CaptureBenchmark
{
    internal const string Topic = "capture-benchmark";
    internal const string OverheadSession = "capture-benchmark-overhead";
    internal static readonly IReadOnlyDictionary<string, int> ProfileSamples = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["short"] = 25,
        ["full"] = 200,
        ["stress"] = 1000,
    };

    private static readonly TimeSpan PostcheckReserve = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumWorkerBudget = TimeSpan.FromSeconds(20);

    internal static async Task<CaptureCommandOutcome> RunAsync(AzurPilotOperatorService service,
        CaptureMeasurementTarget target, CaptureBenchmarkRequest request, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        ScreenshotCaptureRouter? router = service.ScreenshotRouter;
        string runId = CaptureReportWriter.NewRunId(Topic);
        CaptureReportWriter? writer = CaptureReportWriter.Create(Topic, runId, out string? reportFailure);
        if (router is null || writer is null)
        {
            return new(new { verdict = CaptureVerdictNames.ToName(CaptureVerdict.Blocked) },
                Unavailable(router is null ? "capture_router_unavailable" : reportFailure!), "none");
        }

        SceneCheck before = await SceneCheck.ObserveAsync(service, target, cancellationToken).ConfigureAwait(false);
        List<CaptureWorkerOutcome> outcomes = [];
        Dictionary<string, int> orphansReleased = new(StringComparer.Ordinal);
        SceneCheck? after = null;
        object? overhead = null;
        if (before.Ready)
        {
            TimeSpan deadline = request.Timeout - PostcheckReserve;
            for (int index = 0; index < request.Methods.Count; index++)
            {
                ScreenshotMethod method = request.Methods[index];
                TimeSpan remaining = deadline - Stopwatch.GetElapsedTime(started);
                TimeSpan budget = remaining / (request.Methods.Count - index);
                if (budget < MinimumWorkerBudget)
                {
                    outcomes.Add(new(CaptureWorker.Rejected(ScreenshotMethodNames.ToName(method), CaptureVerdict.NotTested,
                        "time_budget_exhausted"), 0, null, null));
                    continue;
                }

                CaptureWorkerOutcome outcome = await CaptureWorker.RunAsync(target, method, request.Samples, budget,
                    request.ConfigurationPath, null, cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
                if (outcome.ExitCode is null)
                {
                    // Worker остановлен по сроку: его helper/forward/reverse освобождаются по журналу владельца.
                    ApplicationResult<int> released = await router.ReleaseOrphanedResourcesAsync(target.InstanceId,
                        target.Endpoint, cancellationToken).ConfigureAwait(false);
                    orphansReleased[outcome.Result.Method] = released.IsSuccess ? released.Value : -1;
                }
            }

            after = await SceneCheck.ObserveAsync(service, target, cancellationToken).ConfigureAwait(false);
        }

        ScreenshotMethodChoice? choice = null;
        string? choiceFailure = null;
        if (after?.Ready == true)
        {
            try
            {
                choice = ScreenshotMethodSelection.Choose([.. outcomes.Select(o => Measurement(o.Result))]);
            }
            catch (ArgumentException)
            {
                choiceFailure = "resolution_mismatch";
            }
        }

        CaptureProbeResult? winner = choice?.Selected is ScreenshotMethod selected
            ? outcomes.Select(o => o.Result).Single(r => r.Method == ScreenshotMethodNames.ToName(selected)) : null;
        object? calibration = null;
        if (winner is not null && request.Calibrate)
        {
            ApplicationResult<string> fingerprint = await router.FingerprintAsync(target.InstanceId, target.Endpoint,
                cancellationToken).ConfigureAwait(false);
            if (fingerprint.IsSuccess)
            {
                ScreenshotCalibration saved = new(fingerprint.Value!, winner.Method, winner.Width, winner.Height,
                    DateTimeOffset.UtcNow, runId, Math.Round(winner.EndToEnd.P95, 2));
                router.Calibration.Save(saved);
                calibration = new { saved = true, saved.Fingerprint, saved.Method, saved.Width, saved.Height, saved.P95Milliseconds };
            }
            else
            {
                calibration = new { saved = false, reason = fingerprint.FailureInfo!.Code };
            }
        }
        else
        {
            calibration = new { saved = false, reason = winner is null ? "no_winner" : "calibration_disabled" };
        }

        if (winner is not null && request.DebugOverhead)
        {
            overhead = await MeasureOverheadAsync(target, request, winner, started, cancellationToken).ConfigureAwait(false);
        }

        CaptureVerdict verdict = !before.Ready || after is { Ready: false } ? CaptureVerdict.Blocked
            : winner is null ? CaptureVerdict.Failed
            : outcomes.Any(o => o.Result.Verdict == CaptureVerdictNames.ToName(CaptureVerdict.NotTested)) ? CaptureVerdict.Partial
            : CaptureVerdict.Pass;
        string reason = !before.Ready ? "main_not_ready_before"
            : after is { Ready: false } ? "main_not_ready_after"
            : choiceFailure ?? choice?.Reason ?? "no_valid_candidate";
        string effect = outcomes.Any(o => o.Result.Method != ScreenshotMethodNames.ToName(ScreenshotMethod.Adb)
            && o.Result.Verdict != CaptureVerdictNames.ToName(CaptureVerdict.NotTested)) ? "mayHaveChanged" : "none";

        object report = new
        {
            formatVersion = 1,
            runId,
            verdict = CaptureVerdictNames.ToName(verdict),
            reason,
            provenance = writer.Provenance(),
            environment = Environment(target),
            request = new
            {
                methods = request.Methods.Select(ScreenshotMethodNames.ToName),
                request.Profile,
                request.Samples,
                warmup = CaptureProbe.DefaultWarmup,
                timeoutMs = request.Timeout.TotalMilliseconds,
                request.Calibrate,
                request.DebugOverhead,
            },
            scene = new { before = before.Report(), after = after?.Report() },
            selection = choice is null ? null : new
            {
                selected = choice.Selected is ScreenshotMethod m ? ScreenshotMethodNames.ToName(m) : null,
                ranking = choice.Ranking.Select(ScreenshotMethodNames.ToName),
                rejections = choice.Rejections.Select(r => new { method = ScreenshotMethodNames.ToName(r.Method), r.Reason }),
                choice.Reason,
            },
            calibration,
            debugOverhead = overhead,
            orphansReleased,
            dependencies = CaptureDependencies.Describe(),
            methods = outcomes.Select(o => new { o.Result, workerWallMs = o.WallMs, workerExitCode = o.ExitCode }),
            totalMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
        };
        writer.WriteJson("report.json", report);
        writer.WriteText("summary.md", Markdown(runId, verdict, reason, target, before, after, choice, outcomes, calibration));

        object data = new
        {
            runId,
            report = writer.RelativeDirectory,
            verdict = CaptureVerdictNames.ToName(verdict),
            reason,
            selected = winner?.Method,
            ranking = choice?.Ranking.Select(ScreenshotMethodNames.ToName),
            calibration,
            scene = new { before = before.Report(), after = after?.Report() },
            methods = outcomes.Select(o => Compact(o.Result)),
            debugOverhead = overhead,
        };
        ApplicationFailure? failure = verdict switch
        {
            CaptureVerdict.Blocked => before.Failure ?? after?.Failure ?? GameUiActionRunner.Failure("ui_not_ready"),
            CaptureVerdict.Failed => new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
                Message = "Ни один транспорт не доказал корректность и стабильность.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason },
            },
            CaptureVerdict.Pass or CaptureVerdict.Partial or CaptureVerdict.Unsupported or CaptureVerdict.NotTested
                or CaptureVerdict.Insufficient or CaptureVerdict.UnverifiedScene or _ => null,
        };
        return new(data, failure, effect);
    }

    internal static ScreenshotCandidateMeasurement Measurement(CaptureProbeResult result)
    {
        _ = ScreenshotMethodNames.TryParse(result.Method, out ScreenshotMethod method);
        CaptureVerdict verdict = Enum.GetValues<CaptureVerdict>()
            .FirstOrDefault(v => CaptureVerdictNames.ToName(v) == result.Verdict, CaptureVerdict.Failed);
        return new(method, verdict, result.CorrectnessProven, result.Width, result.Height, result.Attempts,
            result.Failures, result.EndToEnd, result.FreshFramesPerSecond);
    }

    internal static object Compact(CaptureProbeResult result) => new
    {
        method = result.Method,
        verdict = result.Verdict,
        reason = result.Reason,
        width = result.Width,
        height = result.Height,
        attempts = result.Attempts,
        successes = result.Successes,
        setupMs = result.SetupMs,
        p50Ms = Math.Round(result.EndToEnd.Median, 1),
        p95Ms = Math.Round(result.EndToEnd.P95, 1),
        p99Ms = Math.Round(result.EndToEnd.P99, 1),
        freshFps = result.FreshFramesPerSecond,
        correctness = result.CorrectnessProven,
        cpuPercent = result.Resources?.ProcessCpuPercent,
        systemCpuPercent = result.Resources?.SystemCpuPercent,
    };

    internal static object Environment(CaptureMeasurementTarget target) => new
    {
        instanceId = target.InstanceId.ToString(),
        endpoint = target.Endpoint.ToString(),
        muMuVersion = target.MuMuVersion,
        androidVersion = target.AndroidVersion,
        abi = Core.NativeBoundaryContract.ExpectedAbiVersion,
    };

    private static async Task<object> MeasureOverheadAsync(CaptureMeasurementTarget target, CaptureBenchmarkRequest request,
        CaptureProbeResult off, long started, CancellationToken cancellationToken)
    {
        TimeSpan remaining = request.Timeout - PostcheckReserve - Stopwatch.GetElapsedTime(started);
        if (remaining < MinimumWorkerBudget || !ScreenshotMethodNames.TryParse(off.Method, out ScreenshotMethod method))
        {
            return new { measured = false, reason = "time_budget_exhausted" };
        }

        CaptureWorkerOutcome on = await CaptureWorker.RunAsync(target, method, request.Samples, remaining,
            request.ConfigurationPath, OverheadSession, cancellationToken).ConfigureAwait(false);
        return new
        {
            measured = on.Result.Verdict == CaptureVerdictNames.ToName(CaptureVerdict.Pass),
            method = off.Method,
            offP50Ms = Math.Round(off.EndToEnd.Median, 1),
            offP95Ms = Math.Round(off.EndToEnd.P95, 1),
            onP50Ms = Math.Round(on.Result.EndToEnd.Median, 1),
            onP95Ms = Math.Round(on.Result.EndToEnd.P95, 1),
            deltaP50Ms = Math.Round(on.Result.EndToEnd.Median - off.EndToEnd.Median, 1),
            deltaP95Ms = Math.Round(on.Result.EndToEnd.P95 - off.EndToEnd.P95, 1),
            offCpuPercent = off.Resources?.ProcessCpuPercent,
            onCpuPercent = on.Result.Resources?.ProcessCpuPercent,
            onVerdict = on.Result.Verdict,
            on.DebugReport,
        };
    }

    private static string Markdown(string runId, CaptureVerdict verdict, string reason, CaptureMeasurementTarget target,
        SceneCheck before, SceneCheck? after, ScreenshotMethodChoice? choice, IReadOnlyList<CaptureWorkerOutcome> outcomes,
        object? calibration)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        _ = text.AppendLine(c, $"# Измерение транспортов снимков {runId}");
        _ = text.AppendLine();
        _ = text.AppendLine(c, $"- Итог: **{CaptureVerdictNames.ToName(verdict)}** ({reason})");
        _ = text.AppendLine(c, $"- Target: `{target.InstanceId}` ({target.Endpoint}), MuMu {target.MuMuVersion}, Android {target.AndroidVersion}");
        _ = text.AppendLine(c, $"- Главный экран до: {before.State}; после: {after?.State ?? "не проверялся"}");
        _ = text.AppendLine(c, $"- Выбор: {(choice?.Selected is ScreenshotMethod m ? ScreenshotMethodNames.ToName(m) : "нет допустимого кандидата")}");
        _ = text.AppendLine(c, $"- Калибровка auto: {System.Text.Json.JsonSerializer.Serialize(calibration)}");
        _ = text.AppendLine();
        _ = text.AppendLine("| Метод | Итог | Причина | Размер | Успешно/попыток | Setup, мс | p50 | p95 | p99 | max | Новых FPS | CPU % | CPU системы % | Корректность |");
        _ = text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (CaptureProbeResult r in outcomes.Select(o => o.Result))
        {
            _ = text.AppendLine(c, $"| {r.Method} | {r.Verdict} | {r.Reason ?? ""} | {r.Width}×{r.Height} | {r.Successes}/{r.Attempts} | "
                + $"{r.SetupMs:0.#} | {r.EndToEnd.Median:0.#} | {r.EndToEnd.P95:0.#} | {r.EndToEnd.P99:0.#} | {r.EndToEnd.Maximum:0.#} | "
                + $"{r.FreshFramesPerSecond:0.##} | {r.Resources?.ProcessCpuPercent:0.#} | {r.Resources?.SystemCpuPercent:0.#} | "
                + $"{(r.CorrectnessProven ? "доказана" : "нет")} |");
        }

        if (choice is not null && choice.Rejections.Count > 0)
        {
            _ = text.AppendLine();
            _ = text.AppendLine("Отклонённые кандидаты:");
            foreach (ScreenshotCandidateRejection rejection in choice.Rejections)
            {
                _ = text.AppendLine(c, $"- {ScreenshotMethodNames.ToName(rejection.Method)}: {rejection.Reason}");
            }
        }

        _ = text.AppendLine();
        _ = text.AppendLine("Кадры не сохраняются; CPU — процесс worker-а, без эмулятора и ADB, рядом указана загрузка системы.");
        return text.ToString();
    }

    private static ApplicationFailure Unavailable(string reason) => new()
    {
        Code = ApplicationFailure.DebugReportUnavailable,
        Message = "Измерение невозможно без локального отчёта и production-маршрутизатора захвата.",
        Details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            [ApplicationFailure.MutationAttemptedDetailKey] = "false",
        },
    };
}

/// <summary>Независимая проверка главного экрана до и после измерения; ввода нет.</summary>
internal sealed class SceneCheck
{
    private SceneCheck(string state, string reason, int stableFrames, ApplicationFailure? failure)
    {
        State = state;
        Reason = reason;
        StableFrames = stableFrames;
        Failure = failure;
    }

    internal string State { get; }

    internal string Reason { get; }

    internal int StableFrames { get; }

    internal ApplicationFailure? Failure { get; }

    internal bool Ready => State == nameof(GameUiReadinessState.Ready) && Failure is null;

    internal static async Task<SceneCheck> ObserveAsync(AzurPilotOperatorService service, CaptureMeasurementTarget target,
        CancellationToken cancellationToken)
    {
        ApplicationResult<GameUiReadinessOutcome> observed = await service.ObserveGameUiReadyAsync(target.InstanceId,
            cancellationToken).ConfigureAwait(false);
        return observed.IsFailure
            ? new(nameof(GameUiReadinessState.TechnicalFailure), observed.FailureInfo!.Code, 0, observed.FailureInfo)
            : new(observed.Value!.State.ToString(), observed.Value.Reason, observed.Value.StableFrames,
                observed.Value.State == GameUiReadinessState.Ready ? observed.Value.Failure
                    : observed.Value.Failure ?? GameUiActionRunner.Failure("ui_not_ready"));
    }

    internal object Report() => new
    {
        state = State,
        reason = Reason,
        stableFrames = StableFrames,
        proof = Ready ? "MACHINE_PROVEN" : "NOT_PROVEN",
    };
}
