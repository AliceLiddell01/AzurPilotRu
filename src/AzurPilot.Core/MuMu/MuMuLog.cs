using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Устойчивые project-owned события MuMu lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// События оформлены source-generated <c>[LoggerMessage]</c>: сообщение и его structured properties
/// заданы статически, поэтому runtime-логи остаются machine-readable и не собираются из строк на месте.
/// </para>
/// <para>
/// События bounded: результат перечисления экземпляров и выбор, запрошенная операция, доказанный
/// terminal postcondition, отказ и отмена. Каждый poll не логируется на
/// <see cref="LogLevel.Information"/> — evidence опроса идёт максимум на <see cref="LogLevel.Debug"/>.
/// </para>
/// <para>
/// Отдельно различимы launch без эффекта и единственный повтор launch: они логируются на
/// <see cref="LogLevel.Warning"/>, а не смешиваются с окончательным
/// <c>mumu_lifecycle_timeout</c>, который остаётся отказом уровня <see cref="LogLevel.Error"/>. События
/// едины для всех путей запуска — обычного start и обоих вариантов restart, — поэтому диагностика не
/// зависит от того, какой операцией запрошен launch.
/// </para>
/// <para>
/// Полный stdout/stderr control utility в логи не попадает: сообщается только код выхода и bounded
/// evidence.
/// </para>
/// </remarks>
internal static partial class MuMuLog
{
    /// <summary>Экземпляр выбран.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="selectionMode">Режим выбора: <c>auto</c> или <c>explicit</c>.</param>
    /// <param name="instanceId">Каноническая identity выбранного экземпляра.</param>
    /// <param name="instanceCount">Сколько экземпляров перечислила установка.</param>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "MuMu instance выбран: режим {SelectionMode}, instance {InstanceId}, перечислено "
            + "экземпляров: {InstanceCount}")]
    public static partial void InstanceSelected(
        this ILogger logger,
        string selectionMode,
        string instanceId,
        int instanceCount);

    /// <summary>Выбор экземпляра завершился ожидаемым отказом.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="selectionMode">Режим выбора: <c>auto</c> или <c>explicit</c>.</param>
    /// <param name="failureCode">Стабильный код отказа выбора.</param>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "MuMu instance не выбран: режим {SelectionMode}, код отказа {FailureCode}")]
    public static partial void InstanceSelectionFailed(
        this ILogger logger,
        string selectionMode,
        string failureCode);

    /// <summary>Запрошена lifecycle-операция.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Запрошена MuMu lifecycle-операция: операция {Operation}, instance {InstanceId}")]
    public static partial void LifecycleRequested(this ILogger logger, string operation, string instanceId);

    /// <summary>Lifecycle-операция доказала terminal postcondition.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="initialState">Наблюдённое состояние до mutation.</param>
    /// <param name="finalState">Доказанное состояние после операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence достигнутого postcondition.</param>
    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "MuMu lifecycle доказала postcondition: операция {Operation}, instance {InstanceId}, "
            + "состояние {InitialState} → {FinalState}, elapsed_ms {ElapsedMilliseconds}, evidence "
            + "{Evidence}")]
    public static partial void LifecycleCompleted(
        this ILogger logger,
        string operation,
        string instanceId,
        string initialState,
        string finalState,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Lifecycle-операция завершилась ожидаемым отказом.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="failureCode">Стабильный код отказа операции.</param>
    /// <param name="state">Наблюдённое состояние на момент отказа.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Error,
        Message = "MuMu lifecycle завершилась отказом: операция {Operation}, instance {InstanceId}, код "
            + "отказа {FailureCode}, состояние {State}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void LifecycleFailed(
        this ILogger logger,
        string operation,
        string instanceId,
        string failureCode,
        string state,
        long elapsedMilliseconds);

    /// <summary>Очередное наблюдение состояния при bounded polling.</summary>
    /// <remarks>
    /// Событие диагностическое: каждый poll не является событием уровня
    /// <see cref="LogLevel.Information"/>.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="observedState">Наблюдённое состояние экземпляра.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Debug,
        Message = "MuMu lifecycle poll: операция {Operation}, instance {InstanceId}, наблюдённое "
            + "состояние {ObservedState}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void LifecyclePollObserved(
        this ILogger logger,
        string operation,
        string instanceId,
        string observedState,
        long elapsedMilliseconds);

    /// <summary>Однократная mutation выполнена.</summary>
    /// <remarks>
    /// Событие диагностическое: код выхода не является доказательством успеха, поэтому mutation не
    /// логируется на уровне <see cref="LogLevel.Information"/>.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Операция, которой соответствует mutation.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="exitCode">Код выхода control utility.</param>
    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Debug,
        Message = "MuMu lifecycle mutation выполнена: операция {Operation}, instance {InstanceId}, код "
            + "выхода {ExitCode}")]
    public static partial void LifecycleMutationCompleted(
        this ILogger logger,
        string operation,
        string instanceId,
        int exitCode);

    /// <summary>Launch формально принят, но за окно эффекта признак начала запуска не появился.</summary>
    /// <remarks>
    /// Событие отделяет mutation без эффекта от окончательного отказа: окно эффекта исчерпано, а
    /// состояние экземпляра осталось прежним, поэтому само по себе оно ещё не является отказом.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние, которое не изменилось за окно.</param>
    /// <param name="elapsedMilliseconds">Затраченное время операции в миллисекундах.</param>
    /// <param name="effectWindowMilliseconds">Длительность окна наблюдения эффекта в миллисекундах.</param>
    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Warning,
        Message = "MuMu lifecycle: launch принят, но за окно эффекта признак начала запуска не появился: "
            + "операция {Operation}, instance {InstanceId}, состояние {State}, elapsed_ms "
            + "{ElapsedMilliseconds}, effect_window_ms {EffectWindowMilliseconds}")]
    public static partial void LifecycleLaunchEffectMissing(
        this ILogger logger,
        string operation,
        string instanceId,
        string state,
        long elapsedMilliseconds,
        long effectWindowMilliseconds);

    /// <summary>Выполняется единственный повторный launch после подтверждённого no-op.</summary>
    /// <remarks>
    /// Событие фиксирует факт повтора отдельно от mutation без эффекта и от окончательного отказа: повтор
    /// разрешён ровно один раз и только при подтверждённом прежнем состоянии.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instanceId">Каноническая identity экземпляра.</param>
    /// <param name="elapsedMilliseconds">Затраченное время операции в миллисекундах.</param>
    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Warning,
        Message = "MuMu lifecycle: повторный launch после подтверждённого no-op: операция {Operation}, "
            + "instance {InstanceId}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void LifecycleLaunchRetried(
        this ILogger logger,
        string operation,
        string instanceId,
        long elapsedMilliseconds);
}
