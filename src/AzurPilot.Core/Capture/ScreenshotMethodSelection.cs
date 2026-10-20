namespace AzurPilot.Core.Capture;

/// <summary>Измерение одного кандидата для выбора метода на точном target и одном разрешении.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Verdict">Итог probe и серии; к выбору допускаются <see cref="CaptureVerdict.Pass"/> и серия, урезанная бюджетом (<see cref="CaptureVerdict.Partial"/>).</param>
/// <param name="CorrectnessProven">Кадр совпал с эталоном ADB по геометрии и контрольной сигнатуре.</param>
/// <param name="Width">Ширина кадров серии.</param>
/// <param name="Height">Высота кадров серии.</param>
/// <param name="Attempts">Число попыток серии.</param>
/// <param name="Failures">Число отказов серии (включая тайм-ауты).</param>
/// <param name="EndToEnd">Сводка end-to-end длительности успешных захватов, мс.</param>
/// <param name="FreshFramesPerSecond">Частота доказанно новых кадров в серии подряд.</param>
public sealed record ScreenshotCandidateMeasurement(
    ScreenshotMethod Method,
    CaptureVerdict Verdict,
    bool CorrectnessProven,
    int Width,
    int Height,
    int Attempts,
    int Failures,
    SampleStatistics EndToEnd,
    double FreshFramesPerSecond)
{
    /// <summary>Доля отказов серии.</summary>
    public double ErrorRatio => Attempts == 0 ? 1 : (double)Failures / Attempts;
}

/// <summary>Отклонённый кандидат и машинная причина.</summary>
public sealed record ScreenshotCandidateRejection(ScreenshotMethod Method, string Reason);

/// <summary>Итог выбора: метод либо отсутствие допустимого кандидата.</summary>
/// <param name="Selected">Выбранный метод; null — <c>NO_VALID_CANDIDATE</c>.</param>
/// <param name="Ranking">Допущенные кандидаты в порядке предпочтения.</param>
/// <param name="Rejections">Отклонённые кандидаты с причинами.</param>
/// <param name="Reason">Машинная причина выбора.</param>
public sealed record ScreenshotMethodChoice(
    ScreenshotMethod? Selected,
    IReadOnlyList<ScreenshotMethod> Ranking,
    IReadOnlyList<ScreenshotCandidateRejection> Rejections,
    string Reason);

/// <summary>
/// Политика выбора метода: сначала корректность и стабильность, затем p95 end-to-end, затем частота
/// новых кадров. Гипотеза о самом быстром методе в политику не встроена: выбор следует только из измерений.
/// </summary>
public static class ScreenshotMethodSelection
{
    /// <summary>Минимум успешных измерений, при котором кандидат сравним.</summary>
    public const int MinimumSuccessfulSamples = 5;

    /// <summary>Допустимая доля отказов серии.</summary>
    public const double MaximumErrorRatio = 0.0;

    /// <summary>Относительный допуск, внутри которого p95 считаются равными.</summary>
    public const double P95TieRatio = 0.05;

    /// <summary>Абсолютный допуск равенства p95, мс.</summary>
    public const double P95TieMilliseconds = 1.0;

    /// <summary>Выбирает метод по измерениям одного target и одного разрешения.</summary>
    /// <exception cref="ArgumentException">Кандидаты сняты на разных разрешениях.</exception>
    public static ScreenshotMethodChoice Choose(IReadOnlyCollection<ScreenshotCandidateMeasurement> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        List<ScreenshotCandidateRejection> rejections = [];
        List<ScreenshotCandidateMeasurement> eligible = [];
        foreach (ScreenshotCandidateMeasurement candidate in candidates)
        {
            string? reason = RejectionReason(candidate);
            if (reason is null)
            {
                eligible.Add(candidate);
            }
            else
            {
                rejections.Add(new ScreenshotCandidateRejection(candidate.Method, reason));
            }
        }

        if (eligible.Select(candidate => (candidate.Width, candidate.Height)).Distinct().Count() > 1)
        {
            // Сравнение разных разрешений не определено: такой вход — ошибка вызывающей стороны.
            throw new ArgumentException("Кандидаты выбора сняты на разных разрешениях.", nameof(candidates));
        }

        if (eligible.Count == 0)
        {
            return new ScreenshotMethodChoice(null, [], rejections, "no_valid_candidate");
        }

        List<ScreenshotCandidateMeasurement> ranking = [.. eligible];
        ranking.Sort(Compare);
        return new ScreenshotMethodChoice(ranking[0].Method, [.. ranking.Select(candidate => candidate.Method)],
            rejections, "lowest_p95_end_to_end");
    }

    private static string? RejectionReason(ScreenshotCandidateMeasurement candidate)
    {
        // PARTIAL означает серию, урезанную только бюджетом времени: без отказов и с доказанной корректностью.
        if (candidate.Verdict is not (CaptureVerdict.Pass or CaptureVerdict.Partial))
        {
            return "verdict_" + CaptureVerdictNames.ToName(candidate.Verdict).ToLowerInvariant();
        }

        if (!candidate.CorrectnessProven)
        {
            return "correctness_unproven";
        }

        if (candidate.EndToEnd.Count < MinimumSuccessfulSamples)
        {
            return "insufficient_samples";
        }

        return candidate.ErrorRatio > MaximumErrorRatio ? "errors_in_series" : null;
    }

    private static int Compare(ScreenshotCandidateMeasurement left, ScreenshotCandidateMeasurement right)
    {
        double tolerance = Math.Max(P95TieMilliseconds, Math.Min(left.EndToEnd.P95, right.EndToEnd.P95) * P95TieRatio);
        if (Math.Abs(left.EndToEnd.P95 - right.EndToEnd.P95) > tolerance)
        {
            return left.EndToEnd.P95.CompareTo(right.EndToEnd.P95);
        }

        int fps = right.FreshFramesPerSecond.CompareTo(left.FreshFramesPerSecond);
        if (fps != 0)
        {
            return fps;
        }

        int median = left.EndToEnd.Median.CompareTo(right.EndToEnd.Median);
        return median != 0 ? median : left.Method.CompareTo(right.Method);
    }
}
