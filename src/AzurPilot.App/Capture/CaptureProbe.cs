using System.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Сравнение кадра транспорта с эталоном ADB для отчёта.</summary>
internal sealed record CaptureCorrectness(string Phase, bool Matches, string? Reason, int Cells, int MatchedCells,
    double MeanAbsoluteError, double SwappedMeanAbsoluteError, int ChromaticCells);

/// <summary>Итог серии одного транспорта на одном target; кадры не сохраняются.</summary>
internal sealed record CaptureProbeResult
{
    /// <summary>Внешнее имя транспорта.</summary>
    public required string Method { get; init; }
    /// <summary>Итог: PASS, FAILED, UNSUPPORTED, PARTIAL или INSUFFICIENT.</summary>
    public required string Verdict { get; init; }
    /// <summary>Машинная причина итога, кроме PASS.</summary>
    public string? Reason { get; init; }
    /// <summary>Подготовка транспорта (зависимости, helper, соединение), мс.</summary>
    public double SetupMs { get; init; }
    /// <summary>Ограниченные факты транспорта: версии зависимостей, выбранная DLL и подобное.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();
    /// <summary>Ширина кадров серии.</summary>
    public int Width { get; init; }
    /// <summary>Высота кадров серии.</summary>
    public int Height { get; init; }
    /// <summary>Число прогревочных захватов вне статистики.</summary>
    public int Warmup { get; init; }
    /// <summary>Попытки серии.</summary>
    public int Attempts { get; init; }
    /// <summary>Успешные захваты серии.</summary>
    public int Successes { get; init; }
    /// <summary>Отказы серии.</summary>
    public int Failures { get; init; }
    /// <summary>Отказы по тайм-ауту.</summary>
    public int Timeouts { get; init; }
    /// <summary>Причины отказов с числом повторений.</summary>
    public IReadOnlyDictionary<string, int> FailureReasons { get; init; } = new Dictionary<string, int>();
    /// <summary>End-to-end от начала запроса до проверенного RGB8, мс.</summary>
    public SampleStatistics EndToEnd { get; init; } = SampleStatistics.Empty;
    /// <summary>Получение данных транспортом, мс.</summary>
    public SampleStatistics Acquisition { get; init; } = SampleStatistics.Empty;
    /// <summary>Декодирование и преобразование в RGB8, мс.</summary>
    public SampleStatistics Decode { get; init; } = SampleStatistics.Empty;
    /// <summary>Доказанно новые кадры: запрос-ответ — каждый успешный захват, поток — новый номер кадра.</summary>
    public int FreshFrames { get; init; }
    /// <summary>Частота доказанно новых кадров в серии подряд.</summary>
    public double FreshFramesPerSecond { get; init; }
    /// <summary>Длительность серии, мс.</summary>
    public double SeriesWallMs { get; init; }
    /// <summary>Средний объём полезной нагрузки транспорта на кадр, байт.</summary>
    public double MeanPayloadBytes { get; init; }
    /// <summary>Сравнения с эталоном ADB в начале и в конце серии.</summary>
    public IReadOnlyList<CaptureCorrectness> Correctness { get; init; } = [];
    /// <summary>Корректность доказана обоими сравнениями и совпадением геометрии.</summary>
    public bool CorrectnessProven { get; init; }
    /// <summary>CPU и память worker-процесса за серию.</summary>
    public CaptureResourceUsage? Resources { get; init; }
    /// <summary>Освобождение транспорта и ресурсов устройства, мс.</summary>
    public double CleanupMs { get; init; }
    /// <summary>Сырые end-to-end длительности успешных захватов серии, мс.</summary>
    public IReadOnlyList<double> EndToEndSamplesMs { get; init; } = [];
}
/// <summary>
/// Серия одного транспорта: эталон ADB → подготовка → прогрев → серия подряд → эталон ADB → освобождение.
/// Выполняется в отдельном worker-процессе: зависший провайдер ограничен временем жизни процесса.
/// </summary>
internal static class CaptureProbe
{
    internal const int DefaultWarmup = 3;
    private static readonly TimeSpan CleanupReserve = TimeSpan.FromSeconds(20);

    internal static async Task<CaptureProbeResult> RunAsync(ScreenshotCaptureRouter router, MuMuInstanceId instanceId,
        AndroidEndpoint endpoint, ScreenshotMethod method, int samples, int warmup, TimeSpan budget,
        CancellationToken cancellationToken)
    {
        string name = ScreenshotMethodNames.ToName(method);
        long started = Stopwatch.GetTimestamp();
        bool Expired()
        {
            return Stopwatch.GetElapsedTime(started) > budget - CleanupReserve;
        }

        await using ScreenshotTransportLease reference = await ScreenshotTransportLease.OpenAsync(router, instanceId, endpoint,
            ScreenshotMethod.Adb, cancellationToken).ConfigureAwait(false);
        if (reference.Failure is ApplicationFailure referenceFailure)
        {
            return Failed(name, "reference_unavailable:" + referenceFailure.Code);
        }

        using CapturedAndroidScreenshot? first = await reference.CaptureOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return Failed(name, "reference_capture_failed");
        }

        await using ScreenshotTransportLease candidate = method == ScreenshotMethod.Adb ? reference
            : await ScreenshotTransportLease.OpenAsync(router, instanceId, endpoint, method, cancellationToken).ConfigureAwait(false);
        if (candidate.Failure is ApplicationFailure openFailure)
        {
            bool unsupported = openFailure.Code == ApplicationFailure.ScreenshotMethodUnsupported;
            return new CaptureProbeResult
            {
                Method = name,
                Verdict = CaptureVerdictNames.ToName(unsupported ? CaptureVerdict.Unsupported : CaptureVerdict.Failed),
                Reason = openFailure.Details?.GetValueOrDefault("reason") ?? openFailure.Code,
                Facts = Detail(openFailure),
            };
        }

        Dictionary<string, int> reasons = new(StringComparer.Ordinal);
        List<CaptureCorrectness> correctness = [];
        for (int index = 0; index < warmup && !Expired(); index++)
        {
            using CapturedAndroidScreenshot? frame = await candidate.CaptureOrNullAsync(cancellationToken, reasons)
                .ConfigureAwait(false);
            if (frame is not null && index == 0)
            {
                correctness.Add(Compare("start", first.Frame, frame.Frame));
            }
        }

        List<double> endToEnd = [], acquisition = [], decode = [];
        int attempts = 0, failures = 0, timeouts = 0, fresh = 0, width = 0, height = 0;
        double payload = 0;
        ulong? lastSequence = null;
        CapturedAndroidScreenshot? last = null;
        (CaptureResourceUsage Usage, double SeriesMs) meterResult = default;
        CaptureResourceMeter meter = CaptureResourceMeter.Start();
        long seriesStarted = Stopwatch.GetTimestamp();
        bool partial = false;
        try
        {
            while (attempts < samples)
            {
                if (Expired() || candidate.Broken)
                {
                    partial = true;
                    break;
                }

                attempts++;
                CapturedAndroidScreenshot? frame = await candidate.CaptureOrNullAsync(cancellationToken, reasons)
                    .ConfigureAwait(false);
                if (frame is null)
                {
                    failures++;
                    timeouts += candidate.LastFailure?.Details?.GetValueOrDefault("reason")?.Contains("timeout", StringComparison.Ordinal) == true ? 1 : 0;
                    continue;
                }

                AndroidScreenshotCaptureInfo info = frame.Info;
                endToEnd.Add(info.Timing.EndToEnd.TotalMilliseconds);
                acquisition.Add(info.Timing.Acquisition.TotalMilliseconds);
                decode.Add(info.Timing.Decode.TotalMilliseconds);
                payload += info.PayloadByteCount;
                width = (int)info.FrameInfo.Width;
                height = (int)info.FrameInfo.Height;
                if (info.Stream is null || info.Stream.Sequence != lastSequence)
                {
                    fresh++;
                }

                lastSequence = info.Stream?.Sequence;
                last?.Dispose();
                last = frame;
            }
        }
        finally
        {
            // Эталон в конце серии снимается тем же ADB-транспортом, пока последний кадр кандидата ещё жив.
            double seriesMs = Stopwatch.GetElapsedTime(seriesStarted).TotalMilliseconds;
            CaptureResourceUsage usage = meter.Stop();
            if (last is not null)
            {
                using CapturedAndroidScreenshot? end = await reference.CaptureOrNullAsync(cancellationToken).ConfigureAwait(false);
                if (end is not null)
                {
                    correctness.Add(Compare("end", end.Frame, last.Frame));
                }

                last.Dispose();
            }

            meterResult = (usage, seriesMs);
        }

        long cleanupStarted = Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, string> facts = candidate.Facts;
        double setup = candidate.Setup.TotalMilliseconds;
        await candidate.DisposeAsync().ConfigureAwait(false);
        double cleanup = Stopwatch.GetElapsedTime(cleanupStarted).TotalMilliseconds;

        bool proven = correctness.Count == 2 && correctness.All(c => c.Matches)
            && correctness.All(c => c.Cells > 0) && width == first.Info.FrameInfo.Width
            && height == first.Info.FrameInfo.Height;
        int successes = endToEnd.Count;
        CaptureVerdict verdict = successes == 0 ? CaptureVerdict.Failed
            : successes < ScreenshotMethodSelection.MinimumSuccessfulSamples ? CaptureVerdict.Insufficient
            : partial ? CaptureVerdict.Partial
            : failures > 0 || !proven ? CaptureVerdict.Failed
            : CaptureVerdict.Pass;
        string? reason = verdict switch
        {
            CaptureVerdict.Pass => null,
            CaptureVerdict.Partial => candidate.Broken ? "session_broken" : "budget_exhausted",
            CaptureVerdict.Insufficient => "insufficient_samples",
            CaptureVerdict.Failed or CaptureVerdict.Unsupported or CaptureVerdict.Blocked or CaptureVerdict.NotTested
                or CaptureVerdict.UnverifiedScene or _ => failures > 0 ? reasons.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? "capture_failed"
                : correctness.FirstOrDefault(c => !c.Matches)?.Reason ?? "correctness_unproven",
        };
        return new CaptureProbeResult
        {
            Method = name,
            Verdict = CaptureVerdictNames.ToName(verdict),
            Reason = reason,
            SetupMs = Math.Round(setup, 1),
            Facts = facts,
            Width = width,
            Height = height,
            Warmup = warmup,
            Attempts = attempts,
            Successes = successes,
            Failures = failures,
            Timeouts = timeouts,
            FailureReasons = reasons,
            EndToEnd = SampleStatistics.From(endToEnd),
            Acquisition = SampleStatistics.From(acquisition),
            Decode = SampleStatistics.From(decode),
            FreshFrames = fresh,
            FreshFramesPerSecond = meterResult.SeriesMs <= 0 ? 0 : Math.Round(fresh * 1000 / meterResult.SeriesMs, 2),
            SeriesWallMs = Math.Round(meterResult.SeriesMs, 1),
            MeanPayloadBytes = successes == 0 ? 0 : Math.Round(payload / successes),
            Correctness = correctness,
            CorrectnessProven = proven,
            Resources = meterResult.Usage,
            CleanupMs = Math.Round(cleanup, 1),
            EndToEndSamplesMs = [.. endToEnd.Select(value => Math.Round(value, 2))],
        };
    }

    private static CaptureCorrectness Compare(string phase, NativeFrame reference, NativeFrame candidate)
    {
        ScreenshotFrameComparison comparison = ScreenshotFrameComparison.Compare(reference, candidate);
        return new(phase, comparison.Matches, comparison.FailureReason, comparison.Cells, comparison.MatchedCells,
            Math.Round(comparison.MeanAbsoluteError, 2), Math.Round(comparison.SwappedMeanAbsoluteError, 2),
            comparison.ChromaticCells);
    }

    private static CaptureProbeResult Failed(string method, string reason)
        => new() { Method = method, Verdict = CaptureVerdictNames.ToName(CaptureVerdict.Failed), Reason = reason };

    private static Dictionary<string, string> Detail(ApplicationFailure failure)
        => failure.Details?.Where(p => p.Key is "detail" or "native_status_code" or "provider_status")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
}
