using Microsoft.Extensions.Logging;

namespace AzurPilot.App;

/// <summary>
/// Устойчивые project-owned события application host.
/// </summary>
/// <remarks>
/// <para>
/// События оформлены source-generated <c>[LoggerMessage]</c>: сообщение и его structured properties
/// заданы статически, поэтому runtime-логи остаются machine-readable и не собираются из строк на месте.
/// </para>
/// <para>
/// Каждое событие несёт correlation identifier операции: он связывает startup, загрузку конфигурации и
/// native-диагностику одного запуска. Полный документ конфигурации и произвольные payload в события не
/// попадают: только bounded сведения, нужные для диагностики.
/// </para>
/// </remarks>
internal static partial class AzurPilotLog
{
    /// <summary>Категория логирования application host.</summary>
    internal const string Category = "AzurPilot.App";

    /// <summary>Начало операции запуска.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Application host выполняет операцию {CorrelationId}")]
    public static partial void StartupStarted(this ILogger logger, string correlationId);

    /// <summary>Конфигурация успешно загружена как snapshot.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="configurationSource">Источник конфигурации: встроенные defaults или файл.</param>
    /// <param name="schemaVersion">Версия эффективной схемы конфигурации.</param>
    /// <param name="sourceSchemaVersion">
    /// Версия схемы, объявленная источником: отличается от эффективной, когда legacy-документ
    /// нормализован к текущей схеме в памяти.
    /// </param>
    /// <param name="minimumLevel">Минимальный уровень логирования из конфигурации.</param>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Конфигурация загружена: источник {ConfigurationSource}, схема v{SchemaVersion} "
            + "(источник v{SourceSchemaVersion}), минимальный уровень {MinimumLevel}; "
            + "операция {CorrelationId}")]
    public static partial void ConfigurationLoaded(
        this ILogger logger,
        string correlationId,
        string configurationSource,
        int schemaVersion,
        int sourceSchemaVersion,
        string minimumLevel);

    /// <summary>Существующая конфигурация отклонена схемой.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="failureCode">Стабильный application-level код отказа.</param>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Конфигурация отклонена: код {FailureCode}; операция {CorrelationId}")]
    public static partial void ConfigurationRejected(this ILogger logger, string correlationId, string failureCode);

    /// <summary>Native boundary подтвердила совместимость.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="abiVersion">Фактическая версия ABI native библиотеки.</param>
    /// <param name="opencvVersion">Фактическая версия OpenCV native библиотеки.</param>
    /// <param name="capabilities">Capability, подтверждённые native библиотекой.</param>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Native boundary доступна: ABI {AbiVersion}, OpenCV {OpencvVersion}, "
            + "capability {Capabilities}; операция {CorrelationId}")]
    public static partial void NativeBoundaryVerified(
        this ILogger logger,
        string correlationId,
        uint abiVersion,
        string opencvVersion,
        string capabilities);

    /// <summary>Операция запуска завершена успешно.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Application host готов; операция {CorrelationId}")]
    public static partial void StartupCompleted(this ILogger logger, string correlationId);

    /// <summary>Операция запуска завершена отказом.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="failureCode">Стабильный application-level код отказа.</param>
    /// <param name="isRetryable">Признак того, что повтор операции имеет смысл.</param>
    /// <param name="failureMessage">Человекочитаемое сообщение отказа.</param>
    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "Startup завершён отказом: код {FailureCode}, повтор имеет смысл: {IsRetryable}, "
            + "сообщение: {FailureMessage}; операция {CorrelationId}")]
    public static partial void StartupRejected(
        this ILogger logger,
        string correlationId,
        string failureCode,
        bool isRetryable,
        string failureMessage);

    /// <summary>MuMu-диагностика собрана без отказов.</summary>
    /// <remarks>
    /// Событие описывает результат чтения host-side поверхности MuMu: обнаружение установки, статус
    /// control surface и выбранный экземпляр. Полный вывод control utility, список процессов и пути
    /// установки в событие не попадают.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="isInstallationDiscovered">Признак того, что установка MuMuPlayer обнаружена.</param>
    /// <param name="version">Версия обнаруженной установки или пустая строка.</param>
    /// <param name="controlSurfaceStatus">Bounded статус control surface.</param>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <param name="selectedInstanceId">Каноническая identity выбранного экземпляра или пустая строка.</param>
    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "MuMu-диагностика: установка обнаружена: {IsInstallationDiscovered}, версия {Version}, "
            + "control surface {ControlSurfaceStatus}, разрешённый instance {ConfiguredInstance}, "
            + "выбранный instance {SelectedInstanceId}; операция {CorrelationId}")]
    public static partial void MuMuDiagnosticsCaptured(
        this ILogger logger,
        string correlationId,
        bool isInstallationDiscovered,
        string version,
        string controlSurfaceStatus,
        string configuredInstance,
        string selectedInstanceId);

    /// <summary>MuMu-диагностика сообщила ожидаемый отказ.</summary>
    /// <remarks>
    /// Отказ MuMu — диагностический результат, а не отказ запуска: событие сообщает код отказа, но
    /// исход startup от него не зависит.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="failureCode">Стабильный application-level код отказа MuMu.</param>
    /// <param name="controlSurfaceStatus">Bounded статус control surface.</param>
    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "MuMu-диагностика сообщила отказ: код {FailureCode}, control surface "
            + "{ControlSurfaceStatus}; операция {CorrelationId}")]
    public static partial void MuMuDiagnosticsFailed(
        this ILogger logger,
        string correlationId,
        string failureCode,
        string controlSurfaceStatus);

    /// <summary>Android-диагностика собрана без отказов.</summary>
    /// <remarks>
    /// Событие описывает read-only результат: доступность bundled ADB, разрешённый точный endpoint и
    /// наблюдённое состояние ADB transport. Полного списка устройств, вывода команд ADB и
    /// machine-specific путей в событии нет.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="isAdbAvailable">Признак того, что bundled ADB установки обнаружен.</param>
    /// <param name="endpoint">Точный endpoint в форме <c>host:port</c> или пустая строка.</param>
    /// <param name="transportState">Наблюдённое состояние ADB transport или пустая строка.</param>
    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Information,
        Message = "Android-диагностика: ADB доступен: {IsAdbAvailable}, endpoint {Endpoint}, "
            + "transport {TransportState}; операция {CorrelationId}")]
    public static partial void AndroidDiagnosticsCaptured(
        this ILogger logger,
        string correlationId,
        bool isAdbAvailable,
        string endpoint,
        string transportState);

    /// <summary>Android-диагностика сообщила ожидаемый отказ.</summary>
    /// <remarks>
    /// Отказ Android — диагностический результат, а не отказ запуска: событие сообщает bounded имя шага,
    /// на котором диагностика остановилась, и код отказа, но исход startup от него не зависит.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="androidStage">Bounded machine-stable имя шага, на котором диагностика остановилась.</param>
    /// <param name="failureCode">Стабильный application-level код отказа Android.</param>
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "Android-диагностика сообщила отказ: шаг {AndroidStage}, код {FailureCode}; "
            + "операция {CorrelationId}")]
    public static partial void AndroidDiagnosticsFailed(
        this ILogger logger,
        string correlationId,
        string androidStage,
        string failureCode);

    /// <summary>Состояние игры Azur Lane наблюдалось.</summary>
    /// <remarks>
    /// Событие описывает read-only результат: product identity и три независимых факта. Пустое состояние
    /// и пустые факты означают «не наблюдалось», а не «доказано отсутствующим»: наблюдение выполняется
    /// только на готовом transport. Полного списка процессов, вывода команд ADB и machine-specific путей в
    /// событии нет.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="product">Отображаемое имя продукта.</param>
    /// <param name="package">Package identity продукта.</param>
    /// <param name="state">Bounded имя производного состояния игры или пустая строка.</param>
    /// <param name="isInstalled">Признак установленного пакета; <see langword="null"/>, если не наблюдалось.</param>
    /// <param name="isProcessRunning">Признак запущенного процесса; <see langword="null"/>, если не наблюдалось.</param>
    /// <param name="isForeground">Признак переднего плана; <see langword="null"/>, если не наблюдалось.</param>
    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Azur Lane: продукт {Product}, пакет {Package}, состояние {State}, установлен "
            + "{IsInstalled}, процесс {IsProcessRunning}, foreground {IsForeground}; "
            + "операция {CorrelationId}")]
    public static partial void AzurLaneDiagnosticsCaptured(
        this ILogger logger,
        string correlationId,
        string product,
        string package,
        string state,
        bool? isInstalled,
        bool? isProcessRunning,
        bool? isForeground);

    /// <summary>Наблюдение состояния игры Azur Lane завершилось отказом.</summary>
    /// <remarks>
    /// Отказ наблюдения — диагностический результат, а не отказ запуска: событие сообщает product identity
    /// и код отказа, но исход startup от него не зависит.
    /// </remarks>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="product">Отображаемое имя продукта.</param>
    /// <param name="package">Package identity продукта.</param>
    /// <param name="failureCode">Стабильный application-level код отказа наблюдения.</param>
    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "Azur Lane: продукт {Product}, пакет {Package}, отказ: код {FailureCode}; "
            + "операция {CorrelationId}")]
    public static partial void AzurLaneDiagnosticsFailed(
        this ILogger logger,
        string correlationId,
        string product,
        string package,
        string failureCode);
}
