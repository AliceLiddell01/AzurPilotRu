using AzurPilot.Core.Capture;
using Xunit;

namespace AzurPilot.Tests.Capture;

/// <summary>Чистая математика измерений: статистика, выбор транспорта, интервалы и имена методов.</summary>
[Trait("Category", "Capture")]
public sealed class CaptureAnalysisTests
{
    [Fact(DisplayName = "Статистика: перцентили с интерполяцией, среднее и отклонение по совокупности")]
    public void StatisticsUseLinearInterpolation()
    {
        SampleStatistics statistics = SampleStatistics.From([4, 1, 3, 2, 5]);

        Assert.Equal(5, statistics.Count);
        Assert.Equal(1, statistics.Minimum);
        Assert.Equal(3, statistics.Median);
        Assert.Equal(4.8, statistics.P95, 9);
        Assert.Equal(4.96, statistics.P99, 9);
        Assert.Equal(5, statistics.Maximum);
        Assert.Equal(3, statistics.Mean, 9);
        Assert.Equal(Math.Sqrt(2), statistics.StandardDeviation, 9);
        Assert.Equal(SampleStatistics.Empty, SampleStatistics.From([]));
    }

    [Theory(DisplayName = "Статистика отвергает отрицательные и нечисловые длительности")]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void StatisticsRejectInvalidValues(double value)
        => _ = Assert.Throws<ArgumentException>(() => SampleStatistics.From([1, value]));

    [Fact(DisplayName = "Имена транспортов: девять точных имён, auto и отказ на неизвестные")]
    public void MethodNamesRoundTrip()
    {
        Assert.Equal(9, ScreenshotMethodNames.All.Count);
        Assert.Equal(["ADB", "ADB_nc", "uiautomator2", "aScreenCap", "aScreenCap_nc", "DroidCast", "DroidCast_raw", "scrcpy", "nemu_ipc"],
            ScreenshotMethodNames.All.Select(ScreenshotMethodNames.ToName));
        foreach (ScreenshotMethod method in ScreenshotMethodNames.All)
        {
            Assert.True(ScreenshotMethodNames.TryParse(ScreenshotMethodNames.ToName(method), out ScreenshotMethod parsed));
            Assert.Equal(method, parsed);
        }

        Assert.False(ScreenshotMethodNames.TryParse("adb", out _));
        Assert.False(ScreenshotMethodNames.TryParse("ldopengl", out _));
        Assert.True(ScreenshotMethodNames.TryParseSetting("auto", out ScreenshotMethodSetting? automatic));
        Assert.True(automatic!.IsAutomatic);
        Assert.True(ScreenshotMethodNames.TryParseSetting("nemu_ipc", out ScreenshotMethodSetting? explicitSetting));
        Assert.Equal(ScreenshotMethod.NemuIpc, explicitSetting!.Method);
        Assert.False(ScreenshotMethodNames.TryParseSetting("Auto", out _));
    }

    [Fact(DisplayName = "Выбор: только PASS с доказанной корректностью, затем меньший p95")]
    public void ChoiceRanksByCorrectnessThenP95()
    {
        ScreenshotMethodChoice choice = ScreenshotMethodSelection.Choose(
        [
            Candidate(ScreenshotMethod.Adb, p95: 400),
            Candidate(ScreenshotMethod.NemuIpc, p95: 12),
            Candidate(ScreenshotMethod.Scrcpy, p95: 9, correct: false),
            Candidate(ScreenshotMethod.DroidCastRaw, p95: 30, verdict: CaptureVerdict.Partial),
            Candidate(ScreenshotMethod.AdbNc, p95: 5, failures: 1),
            Candidate(ScreenshotMethod.AScreenCap, p95: 1, verdict: CaptureVerdict.Unsupported),
        ]);

        Assert.Equal(ScreenshotMethod.NemuIpc, choice.Selected);
        Assert.Equal([ScreenshotMethod.NemuIpc, ScreenshotMethod.Adb], choice.Ranking);
        Assert.Equal("lowest_p95_end_to_end", choice.Reason);
        Assert.Contains(new ScreenshotCandidateRejection(ScreenshotMethod.Scrcpy, "correctness_unproven"), choice.Rejections);
        Assert.Contains(new ScreenshotCandidateRejection(ScreenshotMethod.DroidCastRaw, "verdict_partial"), choice.Rejections);
        Assert.Contains(new ScreenshotCandidateRejection(ScreenshotMethod.AdbNc, "errors_in_series"), choice.Rejections);
        Assert.Contains(new ScreenshotCandidateRejection(ScreenshotMethod.AScreenCap, "verdict_unsupported"), choice.Rejections);
    }

    [Fact(DisplayName = "Выбор: равные в пределах допуска p95 различаются частотой новых кадров")]
    public void ChoiceBreaksP95TieByFreshFrames()
    {
        ScreenshotMethodChoice choice = ScreenshotMethodSelection.Choose(
        [
            Candidate(ScreenshotMethod.NemuIpc, p95: 20, fps: 40),
            Candidate(ScreenshotMethod.Scrcpy, p95: 20.5, fps: 60),
        ]);

        Assert.Equal(ScreenshotMethod.Scrcpy, choice.Selected);
    }

    [Fact(DisplayName = "Выбор: нет допустимого кандидата — NO_VALID_CANDIDATE, разные разрешения — ошибка вызова")]
    public void ChoiceWithoutValidCandidateAndMixedResolution()
    {
        ScreenshotMethodChoice none = ScreenshotMethodSelection.Choose([Candidate(ScreenshotMethod.Adb, p95: 10, samples: 4)]);
        Assert.Null(none.Selected);
        Assert.Equal("no_valid_candidate", none.Reason);
        Assert.Equal("insufficient_samples", Assert.Single(none.Rejections).Reason);

        _ = Assert.Throws<ArgumentException>(() => ScreenshotMethodSelection.Choose(
        [
            Candidate(ScreenshotMethod.Adb, p95: 10),
            Candidate(ScreenshotMethod.NemuIpc, p95: 5, width: 1280, height: 720),
        ]));
    }

    [Fact(DisplayName = "Интервал: ровное расписание устойчиво, медленный захват недостижим")]
    public void IntervalClassification()
    {
        ScreenshotIntervalResult stable = ScreenshotIntervalAnalysis.Summarize(Samples(50, 50, 10, count: 20));
        Assert.Equal(ScreenshotIntervalClass.Stable, stable.Class);
        Assert.Equal(20, stable.TargetFps, 9);
        Assert.Equal(20, stable.AchievedFps, 6);
        Assert.Equal(0, stable.DeadlineMissRatio);

        ScreenshotIntervalResult unachievable = ScreenshotIntervalAnalysis.Summarize(Samples(10, 40, 38, count: 20));
        Assert.Equal(ScreenshotIntervalClass.Unachievable, unachievable.Class);
        Assert.Equal(1, unachievable.DeadlineMissRatio);

        ScreenshotIntervalResult tooFew = ScreenshotIntervalAnalysis.Summarize(Samples(50, 50, 10, count: 4));
        Assert.Equal(ScreenshotIntervalClass.Unstable, tooFew.Class);

        ScreenshotIntervalResult failed = ScreenshotIntervalAnalysis.Summarize(
            new ScreenshotIntervalSamples(50, [0], [10], 1, "capture_timeout"));
        Assert.Equal(ScreenshotIntervalClass.Failed, failed.Class);
        Assert.Equal(ScreenshotIntervalClass.Failed, ScreenshotIntervalAnalysis.Summarize(
            new ScreenshotIntervalSamples(50, [], [], 0, null)).Class);
    }

    [Fact(DisplayName = "Интервал: допуск дедлайна max(2 мс, 10%) и проверка согласованности входа")]
    public void IntervalToleranceAndInputValidation()
    {
        Assert.Equal(2, ScreenshotIntervalAnalysis.ToleranceMilliseconds(10));
        Assert.Equal(15, ScreenshotIntervalAnalysis.ToleranceMilliseconds(150), 9);
        _ = Assert.Throws<ArgumentException>(() => ScreenshotIntervalAnalysis.Summarize(
            new ScreenshotIntervalSamples(50, [0, 50], [10], 2, null)));
    }

    [Fact(DisplayName = "Сетка: границы сцены, уникальность, порядок и текущий интервал")]
    public void GridNormalization()
    {
        Assert.Equal([16.7, 50, 150, 300], ScreenshotIntervalAnalysis.NormalizeGrid([300, 50, 16.7, 50], ScreenshotIntervalScene.Normal, 150));
        _ = Assert.Throws<ArgumentException>(() => ScreenshotIntervalAnalysis.NormalizeGrid([500], ScreenshotIntervalScene.Normal, null));
        Assert.Equal([500, 1000], ScreenshotIntervalAnalysis.NormalizeGrid([500], ScreenshotIntervalScene.Combat, 1000));
        _ = Assert.Throws<ArgumentException>(() => ScreenshotIntervalAnalysis.NormalizeGrid([], ScreenshotIntervalScene.Combat, null));
    }

    [Fact(DisplayName = "Профили: формулы fast/balanced/low_load; без боя боевые значения не выводятся")]
    public void ProfilesFollowOriginalFormulas()
    {
        ScreenshotIntervalResult[] normal = [.. new[] { 25.0, 50, 75, 100, 150, 200, 300 }.Select(Stable)];
        IReadOnlyList<ScreenshotIntervalProfile> withoutCombat = ScreenshotIntervalAnalysis.RecommendProfiles(normal, null, 150, 1000);
        Assert.Equal(new ScreenshotIntervalProfile("fast", 25, null), withoutCombat[0]);
        Assert.Equal(new ScreenshotIntervalProfile("balanced", 50, null), withoutCombat[1]);
        Assert.Equal(new ScreenshotIntervalProfile("low_load", 100, null), withoutCombat[2]);
        Assert.Equal(new ScreenshotIntervalProfile("current", 150, 1000), withoutCombat[3]);

        ScreenshotIntervalResult[] combat = [.. new[] { 50.0, 100, 150, 300, 500 }.Select(Stable)];
        IReadOnlyList<ScreenshotIntervalProfile> withCombat = ScreenshotIntervalAnalysis.RecommendProfiles(normal, combat, 150, 1000);
        Assert.Equal(50, withCombat[0].CombatMilliseconds);
        Assert.Equal(150, withCombat[1].CombatMilliseconds);
        Assert.Equal(300, withCombat[2].CombatMilliseconds);

        Assert.Equal([new ScreenshotIntervalProfile("current", 150, 1000)],
            ScreenshotIntervalAnalysis.RecommendProfiles([], null, 150, 1000));
    }

    private static ScreenshotCandidateMeasurement Candidate(ScreenshotMethod method, double p95, bool correct = true,
        CaptureVerdict verdict = CaptureVerdict.Pass, int failures = 0, int samples = 25, double fps = 10,
        int width = 3840, int height = 2160)
        => new(method, verdict, correct, width, height, samples + failures, failures,
            new SampleStatistics(samples, p95 / 2, p95 / 2, p95, p95, p95, p95 / 2, 1), fps);

    private static ScreenshotIntervalSamples Samples(double interval, double period, double latency, int count)
    {
        double[] starts = [.. Enumerable.Range(0, count).Select(index => index * period)];
        return new(interval, starts, [.. starts.Select(start => start + latency)], count, null);
    }

    private static ScreenshotIntervalResult Stable(double interval)
        => ScreenshotIntervalAnalysis.Summarize(Samples(interval, interval, 1, count: 10));
}
