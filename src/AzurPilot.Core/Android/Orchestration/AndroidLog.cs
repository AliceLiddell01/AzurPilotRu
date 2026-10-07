using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Устойчивые project-owned события Android readiness и lifecycle игры Azur Lane.
/// </summary>
/// <remarks>
/// <para>
/// События оформлены source-generated <c>[LoggerMessage]</c>: сообщение и его structured properties
/// заданы статически, поэтому runtime-логи остаются machine-readable и не собираются из строк на месте.
/// </para>
/// <para>
/// Логируются только существенные фазы: разрешённый endpoint, операция transport и готовности,
/// доказанный postcondition готовности Android, наблюдённое состояние игры, запрошенная и завершённая
/// lifecycle-операция игры, а также отказ, отмена и достигнутый deadline.
/// </para>
/// <para>
/// Каждый poll не является событием уровня <see cref="LogLevel.Information"/>: доказательство опроса
/// идёт максимум на <see cref="LogLevel.Debug"/> и не логируется, когда уровень не включён.
/// </para>
/// <para>
/// В логи попадают только bounded значения: имена состояний, точный endpoint, identity пакета, коды
/// выхода, счётчики и bounded evidence. Полный stdout/stderr ADB, полный <c>dumpsys</c> и полный список
/// процессов устройства в логи не попадают.
/// </para>
/// </remarks>
internal static partial class AndroidLog
{
    /// <summary>Точный ADB endpoint экземпляра разрешён.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="instanceId">Identity Android-экземпляра MuMu.</param>
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "ADB endpoint разрешён: endpoint {Endpoint}, instance {InstanceId}")]
    public static partial void EndpointResolved(this ILogger logger, string endpoint, string instanceId);

    /// <summary>Точный ADB endpoint экземпляра не разрешён.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="instanceId">Identity Android-экземпляра MuMu.</param>
    /// <param name="failureCode">Стабильный код отказа разрешения.</param>
    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "ADB endpoint не разрешён: instance {InstanceId}, код отказа {FailureCode}")]
    public static partial void EndpointResolutionFailed(
        this ILogger logger,
        string instanceId,
        string failureCode);

    /// <summary>Запрошена операция готовности Android на точном endpoint-е.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Имя операции, например <c>ready</c>.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = "Запрошена операция Android: операция {Operation}, endpoint {Endpoint}")]
    public static partial void OperationRequested(this ILogger logger, string operation, string endpoint);

    /// <summary>Достигнута готовность Android на точном endpoint-е.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="transportState">Доказанное состояние ADB transport.</param>
    /// <param name="androidRelease">Версия Android, сообщённая устройством, либо bounded прочерк.</param>
    /// <param name="sdkLevel">Уровень Android SDK, сообщённый устройством, либо bounded прочерк.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence доказанного postcondition.</param>
    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Android Ready postcondition доказан: endpoint {Endpoint}, transport {TransportState}, "
            + "release {AndroidRelease}, sdk {SdkLevel}, elapsed_ms {ElapsedMilliseconds}, evidence "
            + "{Evidence}")]
    public static partial void ReadinessCompleted(
        this ILogger logger,
        string endpoint,
        string transportState,
        string androidRelease,
        string sdkLevel,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Готовность Android не доказана: transport не достиг состояния, готового к командам.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="transportState">Наблюдённое состояние ADB transport.</param>
    /// <param name="phase">Имя фазы операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence последнего наблюдения.</param>
    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Warning,
        Message = "ADB transport не готов: endpoint {Endpoint}, состояние {TransportState}, фаза {Phase}, "
            + "elapsed_ms {ElapsedMilliseconds}, evidence {Evidence}")]
    public static partial void TransportNotReady(
        this ILogger logger,
        string endpoint,
        string transportState,
        string phase,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Готовность Android не доказана: transport готов, а устройство ещё не завершило загрузку.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="phase">Имя фазы операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence последнего наблюдения.</param>
    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Android не готов: endpoint {Endpoint}, фаза {Phase}, elapsed_ms {ElapsedMilliseconds}, "
            + "evidence {Evidence}")]
    public static partial void ReadinessNotProven(
        this ILogger logger,
        string endpoint,
        string phase,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Очередное наблюдение ADB transport при bounded polling.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="transportState">Наблюдённое состояние ADB transport.</param>
    /// <param name="phase">Имя фазы операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Debug,
        Message = "Android poll transport: endpoint {Endpoint}, состояние {TransportState}, фаза {Phase}, "
            + "elapsed_ms {ElapsedMilliseconds}")]
    public static partial void TransportPollObserved(
        this ILogger logger,
        string endpoint,
        string transportState,
        string phase,
        long elapsedMilliseconds);

    /// <summary>Очередное наблюдение готовности Android при bounded polling.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="shellAvailable">Признак доступного shell устройства.</param>
    /// <param name="bootCompleted">Значение <c>sys.boot_completed</c> либо bounded прочерк.</param>
    /// <param name="phase">Имя фазы операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Debug,
        Message = "Android poll readiness: endpoint {Endpoint}, shell {ShellAvailable}, boot_completed "
            + "{BootCompleted}, фаза {Phase}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void ReadinessPollObserved(
        this ILogger logger,
        string endpoint,
        bool shellAvailable,
        string bootCompleted,
        string phase,
        long elapsedMilliseconds);

    /// <summary>Выполнено однократное подключение ADB transport точного endpoint-а.</summary>
    /// <remarks>
    /// Событие диагностическое: код выхода команды ADB не является доказательством готовности, поэтому
    /// подключение не логируется на уровне <see cref="LogLevel.Information"/>.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="exitCode">Код выхода команды ADB.</param>
    [LoggerMessage(
        EventId = 3009,
        Level = LogLevel.Debug,
        Message = "ADB transport connect выполнен: endpoint {Endpoint}, код выхода {ExitCode}")]
    public static partial void TransportConnectCompleted(
        this ILogger logger,
        string endpoint,
        int exitCode);

    /// <summary>Наблюдено состояние игры Azur Lane.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="state">Выведенное состояние игры.</param>
    /// <param name="elapsedMilliseconds">Затраченное время наблюдения в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence наблюдения.</param>
    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "Состояние игры наблюдено: endpoint {Endpoint}, package {Package}, состояние {State}, "
            + "elapsed_ms {ElapsedMilliseconds}, evidence {Evidence}")]
    public static partial void GameStateObserved(
        this ILogger logger,
        string endpoint,
        string package,
        string state,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Состояние игры не доказано наблюдением.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="phase">Имя фазы операции.</param>
    /// <param name="evidence">Bounded evidence последнего наблюдения.</param>
    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Warning,
        Message = "Состояние игры не доказано: endpoint {Endpoint}, package {Package}, фаза {Phase}, "
            + "evidence {Evidence}")]
    public static partial void GameStateUnknown(
        this ILogger logger,
        string endpoint,
        string package,
        string phase,
        string evidence);

    /// <summary>Запрошена lifecycle-операция игры.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция: <c>start</c>, <c>stop</c> или <c>restart</c>.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Information,
        Message = "Запрошена lifecycle-операция игры: операция {Operation}, endpoint {Endpoint}, package "
            + "{Package}")]
    public static partial void GameLifecycleRequested(
        this ILogger logger,
        string operation,
        string endpoint,
        string package);

    /// <summary>Lifecycle-операция игры доказала postcondition.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="initialState">Наблюдённое состояние до mutation.</param>
    /// <param name="finalState">Доказанное состояние после операции.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    /// <param name="evidence">Bounded evidence достигнутого postcondition.</param>
    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Information,
        Message = "Lifecycle-операция игры доказала postcondition: операция {Operation}, endpoint "
            + "{Endpoint}, package {Package}, состояние {InitialState} → {FinalState}, elapsed_ms "
            + "{ElapsedMilliseconds}, evidence {Evidence}")]
    public static partial void GameLifecycleCompleted(
        this ILogger logger,
        string operation,
        string endpoint,
        string package,
        string initialState,
        string finalState,
        long elapsedMilliseconds,
        string evidence);

    /// <summary>Lifecycle-операция игры завершилась ожидаемым отказом.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="failureCode">Стабильный код отказа операции.</param>
    /// <param name="state">Наблюдённое состояние на момент отказа.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3014,
        Level = LogLevel.Error,
        Message = "Lifecycle-операция игры завершилась отказом: операция {Operation}, endpoint {Endpoint}, "
            + "package {Package}, код отказа {FailureCode}, состояние {State}, elapsed_ms "
            + "{ElapsedMilliseconds}")]
    public static partial void GameLifecycleFailed(
        this ILogger logger,
        string operation,
        string endpoint,
        string package,
        string failureCode,
        string state,
        long elapsedMilliseconds);

    /// <summary>Выполнена однократная mutation игры.</summary>
    /// <remarks>
    /// Событие диагностическое: код выхода команды ADB не является доказательством перехода, поэтому
    /// mutation не логируется на уровне <see cref="LogLevel.Information"/>.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Операция, которой соответствует mutation.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="mutation">Имя выполненной mutation.</param>
    /// <param name="exitCode">Код выхода команды ADB.</param>
    [LoggerMessage(
        EventId = 3015,
        Level = LogLevel.Debug,
        Message = "Lifecycle mutation игры выполнена: операция {Operation}, endpoint {Endpoint}, package "
            + "{Package}, mutation {Mutation}, код выхода {ExitCode}")]
    public static partial void GameMutationCompleted(
        this ILogger logger,
        string operation,
        string endpoint,
        string package,
        string mutation,
        int exitCode);

    /// <summary>Очередное наблюдение состояния игры при bounded polling.</summary>
    /// <remarks>
    /// Событие диагностическое: каждый poll не является событием уровня
    /// <see cref="LogLevel.Information"/>.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="state">Наблюдённое состояние игры.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3016,
        Level = LogLevel.Debug,
        Message = "Game lifecycle poll: операция {Operation}, endpoint {Endpoint}, наблюдённое состояние "
            + "{State}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void GameLifecyclePollObserved(
        this ILogger logger,
        string operation,
        string endpoint,
        string state,
        long elapsedMilliseconds);

    /// <summary>Lifecycle-операция игры завершилась отменой.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="phase">Имя фазы операции, в которой пришла отмена.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3017,
        Level = LogLevel.Warning,
        Message = "Lifecycle-операция игры отменена: операция {Operation}, endpoint {Endpoint}, package "
            + "{Package}, фаза {Phase}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void GameLifecycleCancelled(
        this ILogger logger,
        string operation,
        string endpoint,
        string package,
        string phase,
        long elapsedMilliseconds);

    /// <summary>Deadline операции достигнут без доказанного требуемого состояния.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="targetState">Требуемое состояние игры.</param>
    /// <param name="observedState">Наблюдённое состояние на момент достижения deadline.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3018,
        Level = LogLevel.Warning,
        Message = "Deadline операции достигнут без требуемого состояния: операция {Operation}, endpoint "
            + "{Endpoint}, package {Package}, требуемое состояние {TargetState}, наблюдённое "
            + "{ObservedState}, elapsed_ms {ElapsedMilliseconds}")]
    public static partial void GameLifecycleDeadlineReached(
        this ILogger logger,
        string operation,
        string endpoint,
        string package,
        string targetState,
        string observedState,
        long elapsedMilliseconds);

    /// <summary>Разрешён launcher-компонент пакета игры.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="component">Каноническая форма <c>package/activity</c> разрешённого компонента.</param>
    [LoggerMessage(
        EventId = 3019,
        Level = LogLevel.Information,
        Message = "Launcher-компонент игры разрешён: endpoint {Endpoint}, package {Package}, component "
            + "{Component}")]
    public static partial void LauncherResolved(
        this ILogger logger,
        string endpoint,
        string package,
        string component);

    /// <summary>Launcher-компонент пакета игры не разрешён.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="status">Статус разрешения launcher-а.</param>
    /// <param name="matchingComponentCount">Число компонентов, совпавших с запросом.</param>
    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Warning,
        Message = "Launcher-компонент игры не разрешён: endpoint {Endpoint}, package {Package}, статус "
            + "{Status}, совпавших компонентов {MatchingComponentCount}")]
    public static partial void LauncherUnresolved(
        this ILogger logger,
        string endpoint,
        string package,
        string status,
        int matchingComponentCount);

    /// <summary>Попытка подключения ADB transport завершилась ожидаемым отказом.</summary>
    /// <remarks>
    /// Событие диагностическое: отказ подключения не доказывает недостижимость endpoint-а, потому что
    /// готовность transport доказывается наблюдением, а не кодом выхода команды.
    /// </remarks>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="failureCode">Стабильный код отказа подключения.</param>
    [LoggerMessage(
        EventId = 3021,
        Level = LogLevel.Debug,
        Message = "ADB transport connect завершился отказом: endpoint {Endpoint}, код отказа "
            + "{FailureCode}")]
    public static partial void TransportConnectFailed(
        this ILogger logger,
        string endpoint,
        string failureCode);

    /// <summary>Операция Android завершилась отменой.</summary>
    /// <param name="logger">Логгер orchestration.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="endpoint">Каноническая форма точного endpoint-а.</param>
    /// <param name="phase">Имя фазы операции, в которой пришла отмена.</param>
    /// <param name="elapsedMilliseconds">Затраченное время в миллисекундах.</param>
    [LoggerMessage(
        EventId = 3022,
        Level = LogLevel.Warning,
        Message = "Операция Android отменена: операция {Operation}, endpoint {Endpoint}, фаза {Phase}, "
            + "elapsed_ms {ElapsedMilliseconds}")]
    public static partial void OperationCancelled(
        this ILogger logger,
        string operation,
        string endpoint,
        string phase,
        long elapsedMilliseconds);
}
