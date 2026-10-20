namespace AzurPilot.Core.Capture;

/// <summary>Сцена измерения интервалов.</summary>
public enum ScreenshotIntervalScene
{
    /// <summary>Доказанный главный экран.</summary>
    Normal,

    /// <summary>Бой, открытый пользователем вручную.</summary>
    Combat,
}

/// <summary>Классификация одного кандидата интервала.</summary>
public enum ScreenshotIntervalClass
{
    /// <summary>Порог устойчивости выполнен.</summary>
    Stable,

    /// <summary>Кадры получены, но порог устойчивости не выполнен.</summary>
    Unstable,

    /// <summary>Медианная длительность захвата больше интервала: частота недостижима этим методом.</summary>
    Unachievable,

    /// <summary>Захват завершился отказом.</summary>
    Failed,
}

/// <summary>Сырые моменты одного кандидата интервала (монотонные миллисекунды) и отказ, если был.</summary>
/// <param name="RequestedIntervalMilliseconds">Запрошенный период start→start.</param>
/// <param name="StartsMilliseconds">Моменты начала захватов измерения.</param>
/// <param name="EndsMilliseconds">Моменты завершения тех же захватов.</param>
/// <param name="FreshFrames">Число кадров, доказанно новых относительно предыдущего.</param>
/// <param name="Error">Машинная причина отказа или null.</param>
public sealed record ScreenshotIntervalSamples(
    double RequestedIntervalMilliseconds,
    IReadOnlyList<double> StartsMilliseconds,
    IReadOnlyList<double> EndsMilliseconds,
    int FreshFrames,
    string? Error);

/// <summary>Сводка одного кандидата интервала.</summary>
public sealed record ScreenshotIntervalResult(
    double RequestedIntervalMilliseconds,
    double TargetFps,
    int Frames,
    double WallMilliseconds,
    double AchievedFps,
    double TargetAchievementRatio,
    double FreshFps,
    SampleStatistics StartToStart,
    SampleStatistics CaptureCallLatency,
    double DeadlineMissRatio,
    ScreenshotIntervalClass Class,
    string? Error)
{
    /// <summary>Признак устойчивости.</summary>
    public bool Stable => Class == ScreenshotIntervalClass.Stable;
}

/// <summary>Профиль интервалов normal/combat.</summary>
/// <param name="Name">fast, balanced, low_load или current.</param>
/// <param name="NormalMilliseconds">Интервал обычного режима или null, если не определён.</param>
/// <param name="CombatMilliseconds">Интервал боя или null, если бой не измерен или нет устойчивых.</param>
public sealed record ScreenshotIntervalProfile(string Name, double? NormalMilliseconds, double? CombatMilliseconds);

/// <summary>
/// Анализ интервалов снимков: формулы устойчивости и профилей повторяют исходный benchmark
/// (не менее 5 кадров, ≥90% целевого FPS, ≤10% пропусков, p95 ≤ интервал + max(2 мс, 10%)).
/// </summary>
public static class ScreenshotIntervalAnalysis
{
    /// <summary>Минимум кадров кандидата.</summary>
    public const int MinimumFrames = 5;

    /// <summary>Эталонная сетка обычного режима исходного benchmark, мс; кандидаты ниже латентности транспорта отсекает вызывающая сторона.</summary>
    public static IReadOnlyList<double> DefaultNormalGrid { get; } = [1, 5, 10, 16.7, 25, 33.3, 50, 75, 100, 150, 200, 300];

    /// <summary>Эталонная сетка боя, мс.</summary>
    public static IReadOnlyList<double> DefaultCombatGrid { get; } = [10, 25, 50, 75, 100, 150, 200, 300, 500, 750, 1000];

    /// <summary>Допустимые границы обычного режима, мс.</summary>
    public static (double Minimum, double Maximum) NormalBounds { get; } = (1, 300);

    /// <summary>Допустимые границы боя, мс.</summary>
    public static (double Minimum, double Maximum) CombatBounds { get; } = (1, 1000);

    /// <summary>Допуск дедлайна для интервала.</summary>
    public static double ToleranceMilliseconds(double interval) => Math.Max(2.0, interval * 0.10);

    /// <summary>Нормализует сетку: границы, округление до микросекунды, уникальность и порядок, текущий интервал.</summary>
    /// <exception cref="ArgumentException">Значение вне границ или сетка пуста.</exception>
    public static IReadOnlyList<double> NormalizeGrid(IEnumerable<double> values, ScreenshotIntervalScene scene,
        double? current)
    {
        ArgumentNullException.ThrowIfNull(values);
        (double minimum, double maximum) = scene == ScreenshotIntervalScene.Normal ? NormalBounds : CombatBounds;
        SortedSet<double> grid = [];
        foreach (double value in values)
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum)
            {
                throw new ArgumentException($"Интервал {value} мс вне диапазона {minimum}–{maximum} мс.", nameof(values));
            }

            _ = grid.Add(Math.Round(value, 3));
        }

        if (grid.Count == 0)
        {
            throw new ArgumentException("Сетка интервалов пуста.", nameof(values));
        }

        if (current is double currentValue && currentValue >= minimum && currentValue <= maximum)
        {
            _ = grid.Add(Math.Round(currentValue, 3));
        }

        return [.. grid];
    }

    /// <summary>Сводит сырые моменты кандидата.</summary>
    public static ScreenshotIntervalResult Summarize(ScreenshotIntervalSamples samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double interval = samples.RequestedIntervalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        double targetFps = 1000.0 / interval;
        IReadOnlyList<double> starts = samples.StartsMilliseconds;
        IReadOnlyList<double> ends = samples.EndsMilliseconds;
        if (starts.Count != ends.Count)
        {
            throw new ArgumentException("Число начал и завершений захвата различается.", nameof(samples));
        }

        if (starts.Count == 0)
        {
            return new ScreenshotIntervalResult(interval, targetFps, 0, 0, 0, 0, 0, SampleStatistics.Empty,
                SampleStatistics.Empty, 1, ScreenshotIntervalClass.Failed, samples.Error ?? "no_frames");
        }

        double wall = Math.Max(ends[^1] - starts[0], 1e-6);
        List<double> startToStart = [];
        List<double> latency = [];
        for (int index = 0; index < starts.Count; index++)
        {
            latency.Add(Math.Max(0, ends[index] - starts[index]));
            if (index > 0)
            {
                startToStart.Add(Math.Max(0, starts[index] - starts[index - 1]));
            }
        }

        if (startToStart.Count == 0)
        {
            startToStart.Add(latency[0]);
        }

        SampleStatistics intervals = SampleStatistics.From(startToStart);
        SampleStatistics calls = SampleStatistics.From(latency);
        double achievedFps = starts.Count > 1
            ? (starts.Count - 1) / (Math.Max(starts[^1] - starts[0], 1e-6) / 1000.0)
            : 1000.0 / wall;
        double achievement = achievedFps / targetFps;
        double tolerance = ToleranceMilliseconds(interval);
        double missRatio = (double)startToStart.Count(value => value > interval + tolerance) / startToStart.Count;
        double freshFps = samples.FreshFrames / (wall / 1000.0);

        ScreenshotIntervalClass classification;
        if (samples.Error is not null)
        {
            classification = ScreenshotIntervalClass.Failed;
        }
        else if (ends.Count >= MinimumFrames && achievement >= 0.90 && missRatio <= 0.10
            && intervals.P95 <= interval + tolerance)
        {
            classification = ScreenshotIntervalClass.Stable;
        }
        else if (calls.Median > interval + tolerance)
        {
            classification = ScreenshotIntervalClass.Unachievable;
        }
        else
        {
            classification = ScreenshotIntervalClass.Unstable;
        }

        return new ScreenshotIntervalResult(interval, targetFps, ends.Count, wall, achievedFps, achievement, freshFps,
            intervals, calls, missRatio, classification, samples.Error);
    }

    /// <summary>
    /// Профили fast/balanced/low_load/current по формулам исходного benchmark. Если бой не измерен,
    /// боевые значения профилей не выводятся (null), а не подменяются обычным режимом.
    /// </summary>
    public static IReadOnlyList<ScreenshotIntervalProfile> RecommendProfiles(
        IReadOnlyCollection<ScreenshotIntervalResult> normal,
        IReadOnlyCollection<ScreenshotIntervalResult>? combat,
        double currentNormal,
        double currentCombat)
    {
        ArgumentNullException.ThrowIfNull(normal);
        ScreenshotIntervalProfile current = new("current", currentNormal, currentCombat);
        double? minimumNormal = PickAtLeast(normal, 0);
        if (minimumNormal is not double fastNormal)
        {
            return [current];
        }

        double balancedNormal = PickAtLeast(normal, Math.Max(fastNormal * 2, 50)) ?? fastNormal;
        double lowNormal = PickAtLeast(normal, Math.Max(fastNormal * 4, 100)) ?? fastNormal;
        double? minimumCombat = combat is null ? null : PickAtLeast(combat, 0);
        double? fastCombat = null;
        double? balancedCombat = null;
        double? lowCombat = null;
        if (combat is not null && minimumCombat is double combatMinimum)
        {
            fastCombat = PickAtLeast(combat, Math.Max(combatMinimum, fastNormal * 2));
            balancedCombat = PickAtLeast(combat, Math.Max(Math.Max(combatMinimum * 2, balancedNormal * 3), 150));
            lowCombat = PickAtLeast(combat, Math.Max(Math.Max(combatMinimum * 4, lowNormal * 3), 300));
        }

        return
        [
            new("fast", fastNormal, fastCombat),
            new("balanced", balancedNormal, balancedCombat),
            new("low_load", lowNormal, lowCombat),
            current,
        ];
    }

    /// <summary>Наименьший устойчивый интервал не меньше <paramref name="minimum"/>, иначе наибольший устойчивый.</summary>
    public static double? PickAtLeast(IEnumerable<ScreenshotIntervalResult> results, double minimum)
    {
        ArgumentNullException.ThrowIfNull(results);
        List<double> stable = [.. results.Where(result => result.Stable)
            .Select(result => result.RequestedIntervalMilliseconds).Order()];
        if (stable.Count == 0)
        {
            return null;
        }

        foreach (double value in stable)
        {
            if (value + 1e-6 >= minimum)
            {
                return value;
            }
        }

        return stable[^1];
    }
}
