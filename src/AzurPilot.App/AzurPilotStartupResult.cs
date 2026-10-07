using System.Globalization;
using System.Text;
using AzurPilot.Core.Android;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.App;

/// <summary>
/// Типизированный результат запуска application host: код выхода, correlation identifier операции,
/// ожидаемый отказ и человекочитаемый отчёт.
/// </summary>
/// <remarks>
/// <para>
/// Ожидаемый отказ (невалидная конфигурация, недоступная или несовместимая native boundary) читается
/// свойством <see cref="FailureInfo"/> и приводит к ненулевому <see cref="ExitCode"/>: «нездоровый»
/// запуск не маскируется как успешный.
/// </para>
/// <para>
/// <see cref="ReportLines"/> — человекочитаемый итог для stdout. Structured runtime logs в него не
/// попадают: они идут отдельным потоком в stderr.
/// </para>
/// </remarks>
public sealed record AzurPilotStartupResult
{
    /// <summary>Описание состояния MuMu, которое не доказано наблюдением.</summary>
    private const string UnprovenMuMuState = "не доказано";

    /// <summary>Описание значения Android-секции, которое не доказано наблюдением.</summary>
    private const string UnprovenAndroidValue = "не наблюдалось";

    /// <summary>Описание факта наблюдения, который не доказан.</summary>
    private const string UnprovenFact = "не доказано";

    private AzurPilotStartupResult(
        int exitCode,
        string correlationId,
        ApplicationFailure? failure,
        AzurPilotDiagnosticReport? diagnostics,
        IReadOnlyList<string> reportLines)
    {
        ExitCode = exitCode;
        CorrelationId = correlationId;
        FailureInfo = failure;
        Diagnostics = diagnostics;
        ReportLines = reportLines;
    }

    /// <summary>Код выхода процесса.</summary>
    /// <value><see cref="AzurPilotExitCode.Success"/> для успешного запуска, иначе — код отказа.</value>
    public int ExitCode { get; }

    /// <summary>Correlation identifier выполненной операции запуска.</summary>
    public string CorrelationId { get; }

    /// <summary>Ожидаемый отказ запуска.</summary>
    /// <value>Отказ application boundary либо <see langword="null"/> для успешного запуска.</value>
    public ApplicationFailure? FailureInfo { get; }

    /// <summary>Диагностический snapshot запуска.</summary>
    /// <value>
    /// Snapshot, если диагностика успела собраться; <see langword="null"/>, если запуск завершился до
    /// сбора диагностики (например, конфигурация отклонена).
    /// </value>
    public AzurPilotDiagnosticReport? Diagnostics { get; }

    /// <summary>Человекочитаемые строки итога запуска для stdout.</summary>
    public IReadOnlyList<string> ReportLines { get; }

    /// <summary>Признак успешного запуска.</summary>
    public bool IsSuccess => FailureInfo is null;

    /// <summary>Создаёт успешный результат запуска.</summary>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="diagnostics">Собранный диагностический snapshot.</param>
    /// <returns>Результат с нулевым кодом выхода.</returns>
    internal static AzurPilotStartupResult Success(string correlationId, AzurPilotDiagnosticReport diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new AzurPilotStartupResult(
            AzurPilotExitCode.Success,
            correlationId,
            failure: null,
            diagnostics,
            BuildSuccessReport(correlationId, diagnostics));
    }

    /// <summary>Создаёт результат-отказ запуска.</summary>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="failure">Ожидаемый отказ application boundary.</param>
    /// <param name="diagnostics">Собранный диагностический snapshot, если он есть.</param>
    /// <returns>Результат с ненулевым кодом выхода.</returns>
    internal static AzurPilotStartupResult Failure(
        string correlationId,
        ApplicationFailure failure,
        AzurPilotDiagnosticReport? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AzurPilotStartupResult(
            AzurPilotExitCode.FromFailure(failure),
            correlationId,
            failure,
            diagnostics,
            BuildFailureReport(correlationId, failure, diagnostics));
    }

    /// <summary>Строит человекочитаемый отчёт успешного запуска.</summary>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="diagnostics">Собранный диагностический snapshot.</param>
    /// <returns>Строки отчёта для stdout.</returns>
    private static IReadOnlyList<string> BuildSuccessReport(string correlationId, AzurPilotDiagnosticReport diagnostics)
    {
        AzurPilotApplicationDiagnostics application = diagnostics.Application;
        AzurPilotConfigurationDiagnostics configuration = diagnostics.Configuration;
        AzurPilotNativeDiagnostics native = diagnostics.Native;

        return
        [
            "AzurPilot: application host",
            $"  Операция: {correlationId}",
            $"  Сборка: {application.AssemblyName} {application.InformationalVersion}",
            $"  Runtime: {application.RuntimeFramework} ({application.RuntimeIdentifier}); "
                + $"процесс {application.ProcessArchitecture}, x64: {DescribeFlag(application.Is64BitProcess)}",
            $"  Конфигурация: {DescribeConfigurationSource(configuration)}; {DescribeSchema(configuration)}; "
                + $"статус: {configuration.ValidationStatus}; минимальный уровень логирования: {configuration.MinimumLevel}",
            $"  Native boundary: ABI {DescribeAbiVersion(native)}; OpenCV {DescribeOpencvVersion(native)}; "
                + $"capability: {DescribeCapabilities(native)}; OpenCV исполнялся: {DescribeFlag(native.OpencvExecuted)}",
            $"  Совместимость native boundary: {native.CompatibilityReason}",
            $"  MuMu: {DescribeMuMu(diagnostics.MuMu)}",
            $"  Android: {DescribeAndroid(diagnostics.Android)}",
            $"  Azur Lane: {DescribeAzurLane(diagnostics.AzurLane)}",
            "Итог: запуск успешен",
        ];
    }

    /// <summary>Строит человекочитаемый отчёт отклонённого запуска.</summary>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="failure">Ожидаемый отказ application boundary.</param>
    /// <param name="diagnostics">Диагностический snapshot, если он успел собраться.</param>
    /// <returns>Строки отчёта для stdout.</returns>
    private static List<string> BuildFailureReport(
        string correlationId,
        ApplicationFailure failure,
        AzurPilotDiagnosticReport? diagnostics)
    {
        List<string> lines =
        [
            "AzurPilot: application host",
            $"  Операция: {correlationId}",
        ];

        if (diagnostics is not null)
        {
            AzurPilotConfigurationDiagnostics configuration = diagnostics.Configuration;
            lines.Add(
                $"  Конфигурация: {DescribeConfigurationSource(configuration)}; {DescribeSchema(configuration)}; "
                + $"статус: {configuration.ValidationStatus}; минимальный уровень логирования: {configuration.MinimumLevel}");

            // Отказ native boundary уже описывается итогом ниже, поэтому строка секции добавляется
            // только тогда, когда граница доступна и отказ пришёл из другого места.
            if (diagnostics.Native.Failure is null)
            {
                lines.Add($"  Native boundary: {diagnostics.Native.CompatibilityReason}");
            }

            // MuMu-отказ не является отказом запуска, поэтому секция MuMu описывается данными: она
            // сообщает, на каком шаге диагностика остановилась, а не меняет исход запуска.
            lines.Add($"  MuMu: {DescribeMuMu(diagnostics.MuMu)}");

            // Отказы Android-секций — тоже данные: недоступный ADB, неготовый transport и ненаблюдённое
            // состояние игры не меняют исход запуска.
            lines.Add($"  Android: {DescribeAndroid(diagnostics.Android)}");
            lines.Add($"  Azur Lane: {DescribeAzurLane(diagnostics.AzurLane)}");
        }

        lines.Add(
            $"Итог: запуск отклонён; код: {failure.Code}; повтор имеет смысл: {DescribeFlag(failure.IsRetryable)}");
        lines.Add($"Сообщение: {failure.Message}");
        return lines;
    }

    private static string DescribeConfigurationSource(AzurPilotConfigurationDiagnostics configuration)
        => configuration.Source == AzurPilotConfigurationSource.File
            ? $"файл «{configuration.FilePath}»"
            : "встроенные defaults";

    /// <summary>Описывает версию эффективной схемы и, при различии, версию схемы источника.</summary>
    /// <remarks>
    /// Legacy-документ v1 нормализуется к v2 в памяти, и на диск при этом ничего не пишется. Чтобы
    /// нормализация не выглядела молчаливой подменой конфигурации, различие версий сообщается явно, а
    /// сам документ конфигурации не печатается.
    /// </remarks>
    /// <param name="configuration">Configuration-секция диагностики.</param>
    /// <returns>Описание версии эффективной схемы и, при нормализации, версии схемы источника.</returns>
    private static string DescribeSchema(AzurPilotConfigurationDiagnostics configuration)
        => configuration.IsLegacySchemaNormalized
            ? $"схема v{configuration.SchemaVersion} (нормализована из v{configuration.SourceSchemaVersion})"
            : $"схема v{configuration.SchemaVersion}";

    /// <summary>Описывает bounded MuMu-секцию диагностики для оператора.</summary>
    /// <remarks>
    /// Значения приходят из внешних источников, поэтому они уже bounded и однострочны: отображаемое имя
    /// экземпляра не может добавить строку в человекочитаемый итог. Evidence в итог не печатается — оно
    /// остаётся данными snapshot.
    /// </remarks>
    /// <param name="muMu">MuMu-секция диагностики.</param>
    /// <returns>Строка MuMu-секции человекочитаемого итога.</returns>
    private static string DescribeMuMu(AzurPilotMuMuDiagnostics muMu)
    {
        StringBuilder builder = new();
        _ = builder.Append(muMu.IsInstallationDiscovered
            ? $"установка обнаружена (версия {muMu.Version})"
            : "установка не обнаружена");
        _ = builder.Append($"; control surface: {muMu.ControlSurfaceStatus}");
        _ = builder.Append($"; конфигурация: {muMu.ConfiguredInstance}");

        if (muMu.SelectedInstanceId is string instanceId)
        {
            _ = builder.Append($"; instance: {instanceId}");

            if (!string.IsNullOrEmpty(muMu.SelectedInstanceDisplayName))
            {
                _ = builder.Append($" «{muMu.SelectedInstanceDisplayName}»");
            }
        }

        _ = builder.Append(muMu.LifecycleState is MuMuLifecycleState state
            ? $"; состояние: {DescribeMuMuState(state)}"
            : "; состояние: не наблюдалось");

        if (muMu.Failure is ApplicationFailure failure)
        {
            _ = builder.Append($"; код: {failure.Code}");
        }

        return builder.ToString();
    }

    /// <summary>Описывает доказанное host-side состояние экземпляра MuMu для оператора.</summary>
    /// <param name="state">Доказанное host-side состояние экземпляра.</param>
    /// <returns>Человекочитаемое описание состояния.</returns>
    private static string DescribeMuMuState(MuMuLifecycleState state) => state switch
    {
        MuMuLifecycleState.Unknown => UnprovenMuMuState,
        MuMuLifecycleState.Stopped => "остановлен",
        MuMuLifecycleState.Running => "запущен",
        _ => UnprovenMuMuState,
    };

    /// <summary>Описывает bounded Android-секцию диагностики для оператора.</summary>
    /// <remarks>
    /// Значения приходят из внешних источников, поэтому они уже bounded и однострочны: версия Android не
    /// может добавить строку в человекочитаемый итог. Evidence в итог не печатается — оно остаётся данными
    /// snapshot. Ненаблюдённый факт сообщается как «не наблюдалось», а не как доказанное отсутствие.
    /// </remarks>
    /// <param name="android">Android-секция диагностики.</param>
    /// <returns>Строка Android-секции человекочитаемого итога.</returns>
    private static string DescribeAndroid(AzurPilotAndroidDiagnostics android)
    {
        StringBuilder builder = new();
        _ = builder.Append($"ADB: {DescribeFact(android.IsAdbAvailable)}");

        if (android.Endpoint is string endpoint)
        {
            _ = builder.Append($"; endpoint: {endpoint}");
        }

        _ = builder.Append(android.TransportState is AndroidTransportState transport
            ? $"; transport: {transport}"
            : "; transport: не наблюдался");
        _ = builder.Append(android.IsShellAvailable is bool shell
            ? $"; shell: {DescribeFlag(shell)}"
            : "; shell: не наблюдалась");
        _ = builder.Append(android.BootCompleted is int bootCompleted
            ? $"; boot_completed: {bootCompleted.ToString(CultureInfo.InvariantCulture)}"
            : "; boot_completed: не наблюдался");

        if (android.AndroidRelease is not null || android.SdkLevel is not null)
        {
            string release = android.AndroidRelease ?? UnprovenAndroidValue;
            string sdk = android.SdkLevel?.ToString(CultureInfo.InvariantCulture) ?? UnprovenAndroidValue;
            _ = builder.Append($"; Android: {release} (sdk {sdk})");
        }

        if (android.Failure is ApplicationFailure failure)
        {
            _ = builder.Append($"; шаг: {android.Stage}; код: {failure.Code}");
        }

        return builder.ToString();
    }

    /// <summary>Описывает bounded секцию состояния игры Azur Lane для оператора.</summary>
    /// <remarks>
    /// Product identity печатается всегда: она принадлежит продукту, а не наблюдению. Недоказанный факт
    /// сообщается как «не доказано», а не как доказанное отсутствие: <see langword="null"/> означает
    /// недоказанное значение, а <see langword="false"/> — доказанное отсутствие. Evidence в итог не
    /// печатается — оно остаётся данными snapshot.
    /// </remarks>
    /// <param name="azurLane">Секция состояния игры диагностики.</param>
    /// <returns>Строка секции состояния игры человекочитаемого итога.</returns>
    private static string DescribeAzurLane(AzurPilotAzurLaneDiagnostics azurLane)
    {
        StringBuilder builder = new();
        _ = builder.Append($"{azurLane.Product} ({azurLane.Package})");
        _ = builder.Append($"; установлен: {DescribeFact(azurLane.IsInstalled)}");
        _ = builder.Append($"; процесс: {DescribeFact(azurLane.IsProcessRunning)}");
        _ = builder.Append($"; foreground: {DescribeFact(azurLane.IsForeground)}");
        _ = builder.Append(azurLane.State is AzurLaneGameState state
            ? $"; состояние: {state}"
            : "; состояние: не наблюдалось");

        if (azurLane.Failure is ApplicationFailure failure)
        {
            _ = builder.Append($"; код: {failure.Code}");
        }

        return builder.ToString();
    }

    /// <summary>Описывает недоказанный либо доказанный факт наблюдения для оператора.</summary>
    /// <remarks>
    /// Три состояния факта различаются явно: недоказанное значение — «не доказано», доказанное отсутствие
    /// — «нет», доказанное присутствие — «да». Недоказанный факт поэтому не выглядит доказанным
    /// отсутствием.
    /// </remarks>
    /// <param name="fact">Недоказанный либо доказанный факт наблюдения.</param>
    /// <returns>Человекочитаемое описание факта.</returns>
    private static string DescribeFact(bool? fact)
        => fact is bool value ? DescribeFlag(value) : UnprovenFact;

    private static string DescribeAbiVersion(AzurPilotNativeDiagnostics native)
        => native.AbiVersion?.ToString(CultureInfo.InvariantCulture) ?? "неизвестна";

    private static string DescribeOpencvVersion(AzurPilotNativeDiagnostics native)
        => native.OpencvVersion?.ToString() ?? "неизвестна";

    private static string DescribeCapabilities(AzurPilotNativeDiagnostics native)
        => native.Capabilities.Count == 0 ? "нет" : string.Join(", ", native.Capabilities);

    private static string DescribeFlag(bool value) => value ? "да" : "нет";
}
