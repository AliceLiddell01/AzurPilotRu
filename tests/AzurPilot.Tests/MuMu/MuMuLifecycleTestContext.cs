using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Собранный orchestration с управляемым временем, координацией и host-double.
/// </summary>
/// <remarks>
/// Все зависимости передаются явно: production orchestration не имеет скрытых значений по умолчанию,
/// поэтому тест никогда не идёт по реальным часам и не делит координацию с другими проверками.
/// </remarks>
internal sealed class MuMuLifecycleTestContext
{
    /// <summary>EventId события «launch без эффекта» в <c>MuMuLog</c>.</summary>
    /// <remarks>
    /// <c>MuMuLog</c> внутренний для <c>AzurPilot.Core</c> и проверкам не виден, поэтому число события
    /// живёт здесь одним экземпляром: иначе каждый тест-класс заводил бы свою копию одного и того же
    /// значения, и они разъехались бы при изменении события.
    /// </remarks>
    internal const int LaunchEffectMissingEventId = 2008;

    /// <summary>EventId события «повторный launch» в <c>MuMuLog</c>.</summary>
    internal const int LaunchRetriedEventId = 2009;

    internal MuMuLifecycleTestContext()
    {
        Host = new TestMuMuHost();
        Gate = new MuMuInstanceMutationGate();
        TimeProvider = new MuMuTestTimeProvider();
        Timings = MuMuLifecycleTimings.Default;
        Logger = new MuMuTestLogger();
        Service = new MuMuLifecycleService(Host, Gate, TimeProvider, Timings, Logger);
    }

    /// <summary>Host-double.</summary>
    internal TestMuMuHost Host { get; }

    /// <summary>Координация mutation, изолированная для этой проверки.</summary>
    internal MuMuInstanceMutationGate Gate { get; }

    /// <summary>Управляемое время.</summary>
    internal MuMuTestTimeProvider TimeProvider { get; }

    /// <summary>Числа времени; литералов таймаута в тестах нет.</summary>
    internal MuMuLifecycleTimings Timings { get; }

    /// <summary>Записывающий логгер.</summary>
    internal MuMuTestLogger Logger { get; }

    /// <summary>Проверяемый orchestration.</summary>
    internal MuMuLifecycleService Service { get; }

    /// <summary>Установка-фикстура без machine-specific путей.</summary>
    internal static MuMuInstallation Installation { get; } =
        new("test-version", "test-install-root", "test-control-utility");

    internal static MuMuInstance Instance(
        string index,
        string displayName = "test-instance",
        string androidVersion = "test-android")
        => new(MuMuInstanceId.FromIndex(index), displayName, androidVersion);

    internal static ApplicationResult<MuMuInstanceState> Observed(
        MuMuLifecycleState state,
        string evidence = "test-observation")
        => ApplicationResult<MuMuInstanceState>.Success(new MuMuInstanceState(state, evidence));

    internal static ApplicationResult<MuMuLifecycleCommandOutcome> Command(
        int exitCode,
        string output = "test-control-output")
        => ApplicationResult<MuMuLifecycleCommandOutcome>.Success(
            new MuMuLifecycleCommandOutcome(exitCode, output));

    /// <summary>Сколько наблюдений вмещает окно эффекта при интервале опроса владельца чисел времени.</summary>
    /// <remarks>
    /// Ожидание выводится из чисел времени, а не из литерала: число наблюдений окна одинаково для всех
    /// проверок единого перехода, поэтому владелец вывода один.
    /// </remarks>
    /// <param name="context">Собранный orchestration с числами времени.</param>
    /// <returns>Число наблюдений окна эффекта.</returns>
    internal static int EffectWindowObservations(MuMuLifecycleTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (int)(context.Timings.LaunchEffectWindow / context.Timings.PollInterval) + 1;
    }
}
