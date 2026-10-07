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
}
