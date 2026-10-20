using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.App.Agent;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Параметры фазы измерения интервалов.</summary>
/// <param name="Scene">normal или combat.</param>
/// <param name="Session">Сессия, связывающая фазы.</param>
/// <param name="Confirmation">Код подтверждения боя; только для combat.</param>
/// <param name="Grid">Собственная сетка либо null для эталонной.</param>
/// <param name="Method">Явный транспорт либо null для текущего выбора приложения.</param>
/// <param name="Timeout">Общий срок команды.</param>
/// <param name="Settings">Действующая секция screenshot конфигурации.</param>
/// <param name="ConfigurationPath">Файл, в который сохраняются настройки текущего источника.</param>
/// <param name="ConfigurationSha256">SHA-256 источника конфигурации либо null для встроенных defaults.</param>
internal sealed record CaptureIntervalRequest(ScreenshotIntervalScene Scene, string Session, string? Confirmation,
    IReadOnlyList<double>? Grid, ScreenshotMethod? Method, TimeSpan Timeout, ScreenshotConfiguration Settings,
    string ConfigurationPath, string? ConfigurationSha256);

/// <summary>Условия, при которых измерены интервалы; применение профиля требует их совпадения.</summary>
/// <param name="Fingerprint">Отпечаток target маршрутизатора: identity, версии MuMu/Android, ABI, зависимости.</param>
/// <param name="MuMuVersion">Версия MuMu.</param>
/// <param name="AndroidVersion">Версия Android.</param>
/// <param name="Width">Ширина кадров измерения.</param>
/// <param name="Height">Высота кадров измерения.</param>
/// <param name="MethodReason">Причина выбора транспорта.</param>
/// <param name="CalibrationRunId">Прогон калибровки auto, определивший транспорт, либо null.</param>
/// <param name="ConfigurationPath">Файл настроек.</param>
/// <param name="ConfigurationSha256">SHA-256 файла настроек на момент измерения либо null.</param>
/// <param name="Screenshot">Действующая секция screenshot на момент измерения.</param>
internal sealed record CaptureIntervalBinding(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("muMuVersion")] string MuMuVersion,
    [property: JsonPropertyName("androidVersion")] string AndroidVersion,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("methodReason")] string MethodReason,
    [property: JsonPropertyName("calibrationRunId")] string? CalibrationRunId,
    [property: JsonPropertyName("configurationPath")] string ConfigurationPath,
    [property: JsonPropertyName("configurationSha256")] string? ConfigurationSha256,
    [property: JsonPropertyName("screenshot")] ScreenshotConfiguration Screenshot);

/// <summary>Состояние сессии интервалов между фазами normal и combat.</summary>
internal sealed record CaptureIntervalSessionState(
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("confirmationCode")] string ConfirmationCode,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("combatCompleted")] bool CombatCompleted,
    [property: JsonPropertyName("formatVersion")] int FormatVersion = 0,
    [property: JsonPropertyName("binding")] CaptureIntervalBinding? Binding = null,
    [property: JsonPropertyName("normalVerdict")] string? NormalVerdict = null,
    [property: JsonPropertyName("combatVerdict")] string? CombatVerdict = null);

/// <summary>Измерение одного интервала: сводка, возраст кадра потока и разрешение кадров.</summary>
internal sealed record CaptureIntervalPoint(ScreenshotIntervalResult Result, SampleStatistics? SourceFrameAgeMs,
    (int Width, int Height)? Resolution);

/// <summary>
/// Измерение интервалов выбранного транспорта start→start. Фаза normal — только на доказанном главном экране и
/// завершается статусом ожидания подтверждения боя; фаза combat — отдельный вызов после того, как пользователь сам
/// открыл бой и явно подтвердил это. Игровых действий, нажатия Battle и переключения экранов нет.
/// </summary>
internal static class CaptureIntervals
{
    internal const string Topic = "capture-intervals";
    internal const string AwaitingConfirmation = "AWAITING_USER_COMBAT_CONFIRMATION";
    internal const int StateFormatVersion = 2;
    internal const string StateFile = "state.json";
    internal const string NormalFile = "normal.json";
    internal const string CombatFile = "combat.json";
    internal const int Warmup = 2;
    internal static readonly TimeSpan PointDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PostcheckReserve = TimeSpan.FromSeconds(30);
    private const string CalibratedReasonPrefix = "calibrated:";
    internal const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    internal static async Task<CaptureCommandOutcome> RunAsync(AzurPilotOperatorService service,
        CaptureMeasurementTarget target, CaptureIntervalRequest request, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        ScreenshotCaptureRouter? router = service.ScreenshotRouter;
        CaptureReportWriter? writer = CaptureReportWriter.Create(Topic, request.Session, out string? reportFailure);
        if (router is null || writer is null)
        {
            return Rejected(router is null ? "capture_router_unavailable" : reportFailure!, CaptureVerdict.Blocked);
        }

        string statePath = Path.Combine(writer.Directory, StateFile);
        CaptureIntervalSessionState? state = ReadState(statePath);
        bool combat = request.Scene == ScreenshotIntervalScene.Combat;
        ScreenshotMethod method;
        string methodReason;
        if (combat)
        {
            string? rejection = state is null ? "session_not_found"
                : state.CombatCompleted ? "session_completed"
                : DateTimeOffset.UtcNow > state.ExpiresAtUtc ? "confirmation_expired"
                : state.InstanceId != target.InstanceId.ToString() || state.Endpoint != target.Endpoint.ToString() ? "target_mismatch"
                : !string.Equals(state.ConfirmationCode, request.Confirmation, StringComparison.Ordinal) ? "confirmation_mismatch"
                : !ScreenshotMethodNames.TryParse(state.Method, out _) ? "session_invalid"
                : request.Method is ScreenshotMethod explicitMethod && ScreenshotMethodNames.ToName(explicitMethod) != state.Method
                    ? "method_mismatch" : null;
            if (rejection is not null)
            {
                return Rejected(rejection, CaptureVerdict.Blocked);
            }

            _ = ScreenshotMethodNames.TryParse(state!.Method, out method);
            methodReason = "session";
        }
        else
        {
            if (state is not null)
            {
                return Rejected("session_exists", CaptureVerdict.Blocked);
            }

            (method, methodReason) = await ChooseMethodAsync(router, target, request, cancellationToken).ConfigureAwait(false);
        }

        ApplicationResult<string> fingerprint = await router.FingerprintAsync(target.InstanceId, target.Endpoint,
            cancellationToken).ConfigureAwait(false);
        if (fingerprint.IsFailure)
        {
            return Rejected("fingerprint_unavailable", CaptureVerdict.Blocked);
        }

        if (combat && state!.Binding is { } bound && bound.Fingerprint != fingerprint.Value)
        {
            // Эмулятор, Android, ABI или зависимости изменились между фазами: результаты не сопоставимы.
            return Rejected("environment_changed", CaptureVerdict.Blocked);
        }

        SceneCheck before = await SceneCheck.ObserveAsync(service, target, cancellationToken).ConfigureAwait(false);
        if (combat && before.Ready)
        {
            // Главный экран доказан: пользователь ещё не в бою, измерение боя было бы подменой сцены.
            return Rejected("scene_is_main", CaptureVerdict.Blocked);
        }

        if (!combat && !before.Ready)
        {
            writer.WriteJson("normal.json", new { verdict = CaptureVerdictNames.ToName(CaptureVerdict.Blocked), scene = before.Report() });
            return new(new { verdict = CaptureVerdictNames.ToName(CaptureVerdict.Blocked), scene = before.Report() },
                before.Failure ?? GameUiActionRunner.Failure("ui_not_ready"), "none");
        }

        IReadOnlyList<double> grid = ScreenshotIntervalAnalysis.NormalizeGrid(
            request.Grid ?? (combat ? ScreenshotIntervalAnalysis.DefaultCombatGrid : ScreenshotIntervalAnalysis.DefaultNormalGrid),
            request.Scene, combat ? request.Settings.CombatIntervalMilliseconds : request.Settings.NormalIntervalMilliseconds);
        TimeSpan deadline = request.Timeout - PostcheckReserve;
        ApplicationResult<MeasuredGrid> measured = await service.WithCaptureLeaseAsync(target.InstanceId,
            () => MeasureAsync(router, target, method, grid, () => deadline - Stopwatch.GetElapsedTime(started), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (measured.IsFailure)
        {
            return new(new { verdict = CaptureVerdictNames.ToName(CaptureVerdict.Blocked) }, measured.FailureInfo, "none");
        }

        SceneCheck after = await SceneCheck.ObserveAsync(service, target, cancellationToken).ConfigureAwait(false);
        MeasuredGrid points = measured.Value!;
        bool complete = points.NotTested.Count == 0 && points.Failure is null;
        (int Width, int Height)[] resolutions = [.. points.Points.Where(p => p.Resolution is not null)
            .Select(p => p.Resolution!.Value).Distinct()];
        int width = resolutions.Length == 1 ? resolutions[0].Width : 0;
        int height = resolutions.Length == 1 ? resolutions[0].Height : 0;
        bool resolutionChanged = resolutions.Length > 1 || (combat && state!.Binding is { } phaseBinding
            && resolutions.Length == 1 && (width != phaseBinding.Width || height != phaseBinding.Height));
        CaptureVerdict verdict;
        string reason;
        if (!combat)
        {
            verdict = !after.Ready ? CaptureVerdict.Blocked : resolutionChanged ? CaptureVerdict.Failed
                : complete ? CaptureVerdict.Pass : CaptureVerdict.Partial;
            reason = !after.Ready ? "main_not_ready_after" : resolutionChanged ? "resolution_changed"
                : complete ? "measured" : points.Failure ?? "time_budget_exhausted";
        }
        else
        {
            // Распознавателя боя нет: сцена доказана только подтверждением пользователя. Возврат на главный экран
            // означает, что бой закончился во время либо до конца измерения.
            verdict = after.Ready || !complete || resolutionChanged ? CaptureVerdict.Partial : CaptureVerdict.UnverifiedScene;
            reason = resolutionChanged ? "resolution_changed" : after.Ready ? "interrupted_scene_returned_to_main"
                : complete ? "unverified_scene" : points.Failure ?? "time_budget_exhausted";
        }

        object phase = new
        {
            formatVersion = 1,
            session = request.Session,
            scene = combat ? "combat" : "normal",
            verdict = CaptureVerdictNames.ToName(verdict),
            reason,
            method = ScreenshotMethodNames.ToName(method),
            methodReason,
            sceneProof = combat ? "USER_CONFIRMED" : "MACHINE_PROVEN",
            sceneBefore = before.Report(),
            sceneAfter = after.Report(),
            provenance = writer.Provenance(),
            environment = CaptureBenchmark.Environment(target),
            pointDurationMs = PointDuration.TotalMilliseconds,
            warmup = Warmup,
            minimumFrames = ScreenshotIntervalAnalysis.MinimumFrames,
            setupMs = points.SetupMs,
            facts = points.Facts,
            points = points.Points,
            notTestedMs = points.NotTested,
            failure = points.Failure,
            downstreamRecognitionDelay = "not_measured",
            totalMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
        };
        writer.WriteJson(combat ? CombatFile : NormalFile, phase);

        IReadOnlyList<ScreenshotIntervalResult>? normalResults = combat ? ReadResults(Path.Combine(writer.Directory, NormalFile))
            : [.. points.Points.Select(p => p.Result)];
        IReadOnlyList<ScreenshotIntervalResult>? combatResults = combat ? [.. points.Points.Select(p => p.Result)] : null;
        ScreenshotConfiguration measuredSettings = state?.Binding?.Screenshot ?? request.Settings;
        IReadOnlyList<ScreenshotIntervalProfile> profiles = ScreenshotIntervalAnalysis.RecommendProfiles(normalResults ?? [],
            combatResults, measuredSettings.NormalIntervalMilliseconds, measuredSettings.CombatIntervalMilliseconds);
        writer.WriteJson("profiles.json", new { session = request.Session, combat = combat ? "measured" : "NOT_TESTED", profiles });
        writer.WriteText("summary.md", Markdown(request.Session, ScreenshotMethodNames.ToName(method), normalResults,
            combatResults, profiles));

        string? code = null;
        DateTimeOffset? expires = null;
        bool awaiting = !combat && verdict is CaptureVerdict.Pass or CaptureVerdict.Partial;
        if (awaiting)
        {
            code = RandomNumberGenerator.GetString(CodeAlphabet, 8);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            expires = now + ConfirmationLifetime;
            CaptureIntervalBinding binding = new(fingerprint.Value!, target.MuMuVersion, target.AndroidVersion,
                width, height, methodReason,
                methodReason.StartsWith(CalibratedReasonPrefix, StringComparison.Ordinal) ? methodReason[CalibratedReasonPrefix.Length..] : null,
                request.ConfigurationPath, request.ConfigurationSha256, request.Settings);
            WriteState(statePath, new(request.Session, target.InstanceId.ToString(), target.Endpoint.ToString(),
                ScreenshotMethodNames.ToName(method), code, now, expires.Value, false, StateFormatVersion, binding,
                CaptureVerdictNames.ToName(verdict)));
        }
        else if (combat)
        {
            WriteState(statePath, state! with { CombatCompleted = true, CombatVerdict = CaptureVerdictNames.ToName(verdict) });
        }

        object data = new
        {
            session = request.Session,
            report = writer.RelativeDirectory,
            scene = combat ? "combat" : "normal",
            status = awaiting ? AwaitingConfirmation : CaptureVerdictNames.ToName(verdict),
            verdict = CaptureVerdictNames.ToName(verdict),
            reason,
            method = ScreenshotMethodNames.ToName(method),
            methodReason,
            sceneProof = combat ? "USER_CONFIRMED" : "MACHINE_PROVEN",
            combat = combat ? "measured" : "NOT_TESTED",
            points = points.Points.Select(p => Compact(p)),
            notTestedMs = points.NotTested,
            profiles,
            confirmation = code is null ? null : new
            {
                code,
                expiresAtUtc = expires,
                instruction = "Передавайте код только после того, как пользователь сам открыл бой и явно подтвердил это; "
                    + "агент не открывает бой и не считает своё сообщение подтверждением.",
            },
        };
        ApplicationFailure? failure = verdict == CaptureVerdict.Blocked ? after.Failure ?? GameUiActionRunner.Failure("ui_not_ready")
            : verdict == CaptureVerdict.Failed ? Rejected(reason, verdict).Failure : null;
        return new(data, failure, method == ScreenshotMethod.Adb ? "none" : "mayHaveChanged");
    }

    internal static async Task WaitUntilAsync(long timestamp, CancellationToken cancellationToken)
    {
        // Таймер Windows по умолчанию грубый (~15,6 мс), поэтому последние миллисекунды ожидаются уступками потока,
        // без изменения системного разрешения таймера.
        while (true)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), timestamp);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > TimeSpan.FromMilliseconds(20))
            {
                await Task.Delay(remaining - TimeSpan.FromMilliseconds(17), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Thread.Yield();
            }
        }
    }

    private static async Task<(ScreenshotMethod, string)> ChooseMethodAsync(ScreenshotCaptureRouter router,
        CaptureMeasurementTarget target, CaptureIntervalRequest request, CancellationToken cancellationToken)
    {
        if (request.Method is ScreenshotMethod explicitMethod)
        {
            return (explicitMethod, "explicit_argument");
        }

        if (router.Setting.Method is ScreenshotMethod configured)
        {
            return (configured, "explicit_setting");
        }

        ApplicationResult<string> fingerprint = await router.FingerprintAsync(target.InstanceId, target.Endpoint,
            cancellationToken).ConfigureAwait(false);
        ScreenshotCalibration? calibration = fingerprint.IsSuccess ? router.Calibration.Find(fingerprint.Value!) : null;
        return calibration is not null && ScreenshotMethodNames.TryParse(calibration.Method, out ScreenshotMethod calibrated)
            ? (calibrated, CalibratedReasonPrefix + calibration.RunId)
            : (ScreenshotMethod.Adb, "bootstrap_calibration_missing");
    }

    private static async Task<ApplicationResult<MeasuredGrid>> MeasureAsync(ScreenshotCaptureRouter router,
        CaptureMeasurementTarget target, ScreenshotMethod method, IReadOnlyList<double> grid, Func<TimeSpan> remaining,
        CancellationToken cancellationToken)
    {
        await using ScreenshotTransportLease lease = await ScreenshotTransportLease.OpenAsync(router, target.InstanceId,
            target.Endpoint, method, cancellationToken).ConfigureAwait(false);
        if (lease.Failure is ApplicationFailure openFailure)
        {
            return ApplicationResult<MeasuredGrid>.Failure(openFailure);
        }

        List<CaptureIntervalPoint> points = [];
        List<double> notTested = [];
        string? failure = null;
        foreach (double interval in grid)
        {
            if (failure is not null || remaining() < Duration(interval) + TimeSpan.FromSeconds(5) || lease.Broken)
            {
                notTested.Add(interval);
                continue;
            }

            for (int index = 0; index < Warmup; index++)
            {
                using CapturedAndroidScreenshot? warm = await lease.CaptureOrNullAsync(cancellationToken).ConfigureAwait(false);
            }

            CaptureIntervalPoint point = await MeasurePointAsync(lease, interval, cancellationToken).ConfigureAwait(false);
            points.Add(point);
            failure = point.Result.Error;
        }

        return ApplicationResult<MeasuredGrid>.Success(new(points, notTested, failure, Math.Round(lease.Setup.TotalMilliseconds, 1),
            lease.Facts));
    }

    private static async Task<CaptureIntervalPoint> MeasurePointAsync(ScreenshotTransportLease lease, double interval,
        CancellationToken cancellationToken)
    {
        List<double> starts = [], ends = [], ages = [];
        int fresh = 0;
        ulong? lastSequence = null;
        string? error = null;
        (int Width, int Height)? resolution = null;
        long origin = Stopwatch.GetTimestamp();
        long period = (long)(interval * Stopwatch.Frequency / 1000.0);
        long next = origin;
        TimeSpan duration = Duration(interval);
        while (Stopwatch.GetElapsedTime(origin) < duration)
        {
            await WaitUntilAsync(next, cancellationToken).ConfigureAwait(false);
            long start = Stopwatch.GetTimestamp();
            using CapturedAndroidScreenshot? frame = await lease.CaptureOrNullAsync(cancellationToken).ConfigureAwait(false);
            long end = Stopwatch.GetTimestamp();
            if (frame is null)
            {
                error = lease.LastFailure?.Details?.GetValueOrDefault("reason") ?? lease.LastFailure?.Code ?? "capture_failed";
                break;
            }

            (int Width, int Height) size = ((int)frame.Info.FrameInfo.Width, (int)frame.Info.FrameInfo.Height);
            if (resolution is { } first && first != size)
            {
                error = "resolution_changed";
                break;
            }

            resolution = size;
            starts.Add(Stopwatch.GetElapsedTime(origin, start).TotalMilliseconds);
            ends.Add(Stopwatch.GetElapsedTime(origin, end).TotalMilliseconds);
            if (frame.Info.Stream is { } stream)
            {
                ages.Add(Stopwatch.GetElapsedTime(stream.ArrivalTimestamp, end).TotalMilliseconds);
                fresh += stream.Sequence != lastSequence ? 1 : 0;
                lastSequence = stream.Sequence;
            }
            else
            {
                fresh++;
            }

            // Расписание start→start: следующий захват не раньше start + период; опоздание не догоняется пачкой.
            next = start + period;
        }

        ScreenshotIntervalResult result = ScreenshotIntervalAnalysis.Summarize(new(interval, starts, ends, fresh, error));
        return new(result, ages.Count == 0 ? null : SampleStatistics.From(ages), resolution);
    }

    // Длинный интервал получает время хотя бы на минимум кадров: иначе 750–1000 мс за 2 с неизбежно «неустойчивы».
    internal static TimeSpan Duration(double interval)
    {
        TimeSpan minimum = TimeSpan.FromMilliseconds(interval * (ScreenshotIntervalAnalysis.MinimumFrames - 0.5));
        return minimum > PointDuration ? minimum : PointDuration;
    }

    private static object Compact(CaptureIntervalPoint point)
    {
        ScreenshotIntervalResult r = point.Result;
        return new
        {
            requestedMs = r.RequestedIntervalMilliseconds,
            @class = r.Class.ToString(),
            frames = r.Frames,
            achievedFps = Math.Round(r.AchievedFps, 2),
            targetFps = Math.Round(r.TargetFps, 2),
            freshFps = Math.Round(r.FreshFps, 2),
            startToStartP50Ms = Math.Round(r.StartToStart.Median, 2),
            startToStartP95Ms = Math.Round(r.StartToStart.P95, 2),
            captureCallP50Ms = Math.Round(r.CaptureCallLatency.Median, 2),
            missRatio = Math.Round(r.DeadlineMissRatio, 3),
            sourceFrameAgeP50Ms = point.SourceFrameAgeMs is { } age ? Math.Round(age.Median, 2) : (double?)null,
            error = r.Error,
        };
    }

    private static string Markdown(string session, string method, IReadOnlyList<ScreenshotIntervalResult>? normal,
        IReadOnlyList<ScreenshotIntervalResult>? combat, IReadOnlyList<ScreenshotIntervalProfile> profiles)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        _ = text.AppendLine(c, $"# Интервалы снимков, сессия {session}");
        _ = text.AppendLine();
        _ = text.AppendLine(c, $"Транспорт: `{method}`. Порог устойчивости: ≥90% целевого FPS, ≤10% пропусков, p95 start→start ≤ интервал + max(2 мс, 10%).");
        foreach ((string title, IReadOnlyList<ScreenshotIntervalResult>? rows) in new[] { ("Обычный режим (главный экран)", normal), ("Бой (подтверждён пользователем, сцена не распознана машиной)", combat) })
        {
            _ = text.AppendLine();
            _ = text.AppendLine(c, $"## {title}");
            if (rows is null)
            {
                _ = text.AppendLine("NOT_TESTED");
                continue;
            }

            _ = text.AppendLine("| Интервал, мс | Класс | Кадров | FPS | Новых FPS | p95 start→start | p50 вызова | Пропуски |");
            _ = text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (ScreenshotIntervalResult r in rows)
            {
                _ = text.AppendLine(c, $"| {r.RequestedIntervalMilliseconds:0.###} | {r.Class} | {r.Frames} | {r.AchievedFps:0.##} | "
                    + $"{r.FreshFps:0.##} | {r.StartToStart.P95:0.##} | {r.CaptureCallLatency.Median:0.##} | {r.DeadlineMissRatio:P0} |");
            }
        }

        _ = text.AppendLine();
        _ = text.AppendLine("## Профили");
        _ = text.AppendLine("| Профиль | normal, мс | combat, мс |");
        _ = text.AppendLine("| --- | --- | --- |");
        foreach (ScreenshotIntervalProfile profile in profiles)
        {
            _ = text.AppendLine(c, $"| {profile.Name} | {profile.NormalMilliseconds?.ToString("0.###", c) ?? "—"} | "
                + $"{profile.CombatMilliseconds?.ToString("0.###", c) ?? "NOT_TESTED"} |");
        }

        return text.ToString();
    }

    private static CaptureCommandOutcome Rejected(string reason, CaptureVerdict verdict)
        => new(new { verdict = CaptureVerdictNames.ToName(verdict), reason }, new ApplicationFailure
        {
            Code = ApplicationFailure.InvalidArgument,
            Message = "Фаза измерения интервалов отклонена.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                [ApplicationFailure.MutationAttemptedDetailKey] = "false",
            },
        }, "none");

    internal static CaptureIntervalSessionState? ReadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CaptureIntervalSessionState>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteState(string path, CaptureIntervalSessionState state)
        => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state));

    internal static IReadOnlyList<ScreenshotIntervalResult>? ReadResults(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("points", out JsonElement points)
                ? [.. points.EnumerateArray().Select(p => p.GetProperty("result").Deserialize<ScreenshotIntervalResult>(AgentJson.Options)!)]
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record MeasuredGrid(IReadOnlyList<CaptureIntervalPoint> Points, IReadOnlyList<double> NotTested,
        string? Failure, double SetupMs, IReadOnlyDictionary<string, string> Facts);
}
