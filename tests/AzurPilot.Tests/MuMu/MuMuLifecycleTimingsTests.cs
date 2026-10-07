using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства того, что числа времени MuMu lifecycle принадлежат единственному владельцу.
/// </summary>
/// <remarks>
/// Проверки не содержат числовых литералов таймаута: ожидания выводятся из самих значений владельца,
/// поэтому deadline и интервал опроса не могут разъехаться между кодом и тестами.
/// </remarks>
public sealed class MuMuLifecycleTimingsTests
{
    [Fact(DisplayName = "Значения по умолчанию положительны и покрывают все три операции")]
    public void DefaultsArePositiveAndCoverEveryOperation()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        Assert.True(timings.PollInterval > TimeSpan.Zero);
        Assert.True(timings.StartDeadline > TimeSpan.Zero);
        Assert.True(timings.StopDeadline > TimeSpan.Zero);
        Assert.True(timings.RestartDeadline > TimeSpan.Zero);
        Assert.True(timings.LaunchEffectWindow > TimeSpan.Zero);

        Assert.Equal(timings.StartDeadline, timings.DeadlineFor(MuMuLifecycleOperation.Start));
        Assert.Equal(timings.StopDeadline, timings.DeadlineFor(MuMuLifecycleOperation.Stop));
        Assert.Equal(timings.RestartDeadline, timings.DeadlineFor(MuMuLifecycleOperation.Restart));

        // Deadline restart покрывает обе фазы композиции stop → start, поэтому он не меньше deadline
        // каждой из этих фаз.
        Assert.True(timings.RestartDeadline >= timings.StopDeadline);
        Assert.True(timings.RestartDeadline >= timings.StartDeadline);
    }

    [Fact(DisplayName = "Окно эффекта заметно меньше deadline и вмещает несколько наблюдений")]
    public void EffectWindowFitsInsideDeadline()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        // Окно измеряет признак начала запуска и принадлежит единому переходу к Running, поэтому оно
        // меньше deadline запуска: единственный повтор launch обязан уложиться в оставшуюся часть
        // deadline операции.
        Assert.True(
            timings.LaunchEffectWindow <= timings.StartDeadline / 4,
            "Окно эффекта должно быть заметно меньше deadline запуска.");
        Assert.True(
            timings.LaunchEffectWindow < timings.RestartDeadline,
            "Окно эффекта должно укладываться в deadline restart.");
        Assert.True(
            timings.LaunchEffectWindow >= timings.PollInterval * 4,
            "Окно эффекта должно вмещать несколько наблюдений состояния.");
    }

    [Fact(DisplayName = "Конструктор по умолчанию даёт те же значения, что Default")]
    public void ParameterlessConstructorMatchesDefault()
    {
        Assert.Equal(MuMuLifecycleTimings.Default, new MuMuLifecycleTimings());
    }

    [Fact(DisplayName = "Неположительный интервал опроса отклоняется")]
    public void NonPositivePollIntervalIsRejected()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuLifecycleTimings(
            TimeSpan.Zero,
            timings.StartDeadline,
            timings.StopDeadline,
            timings.RestartDeadline,
            timings.LaunchEffectWindow));
    }

    [Fact(DisplayName = "Неположительные deadline отклоняются")]
    public void NonPositiveDeadlinesAreRejected()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuLifecycleTimings(
            timings.PollInterval,
            TimeSpan.Zero,
            timings.StopDeadline,
            timings.RestartDeadline,
            timings.LaunchEffectWindow));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuLifecycleTimings(
            timings.PollInterval,
            timings.StartDeadline,
            TimeSpan.Zero,
            timings.RestartDeadline,
            timings.LaunchEffectWindow));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuLifecycleTimings(
            timings.PollInterval,
            timings.StartDeadline,
            timings.StopDeadline,
            TimeSpan.Zero,
            timings.LaunchEffectWindow));
    }

    [Fact(DisplayName = "Неположительное окно эффекта отклоняется")]
    public void NonPositiveEffectWindowIsRejected()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuLifecycleTimings(
            timings.PollInterval,
            timings.StartDeadline,
            timings.StopDeadline,
            timings.RestartDeadline,
            TimeSpan.Zero));
    }

    [Fact(DisplayName = "Значения времени сравниваются по значению")]
    public void TimingsAreValueEqual()
    {
        MuMuLifecycleTimings timings = MuMuLifecycleTimings.Default;

        Assert.Equal(
            timings,
            new MuMuLifecycleTimings(
                timings.PollInterval,
                timings.StartDeadline,
                timings.StopDeadline,
                timings.RestartDeadline,
                timings.LaunchEffectWindow));

        // Окно эффекта — часть значения: наборы с разным окном не равны.
        Assert.NotEqual(
            timings,
            new MuMuLifecycleTimings(
                timings.PollInterval,
                timings.StartDeadline,
                timings.StopDeadline,
                timings.RestartDeadline,
                timings.LaunchEffectWindow + timings.PollInterval));
    }
}
