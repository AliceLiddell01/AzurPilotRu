using System.Globalization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;

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
            $"  Конфигурация: {DescribeConfigurationSource(configuration)}; схема v{configuration.SchemaVersion}; "
                + $"статус: {configuration.ValidationStatus}; минимальный уровень логирования: {configuration.MinimumLevel}",
            $"  Native boundary: ABI {DescribeAbiVersion(native)}; OpenCV {DescribeOpencvVersion(native)}; "
                + $"capability: {DescribeCapabilities(native)}; OpenCV исполнялся: {DescribeFlag(native.OpencvExecuted)}",
            $"  Совместимость native boundary: {native.CompatibilityReason}",
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
                $"  Конфигурация: {DescribeConfigurationSource(configuration)}; схема v{configuration.SchemaVersion}; "
                + $"статус: {configuration.ValidationStatus}; минимальный уровень логирования: {configuration.MinimumLevel}");

            // Отказ native boundary уже описывается итогом ниже, поэтому строка секции добавляется
            // только тогда, когда граница доступна и отказ пришёл из другого места.
            if (diagnostics.Native.Failure is null)
            {
                lines.Add($"  Native boundary: {diagnostics.Native.CompatibilityReason}");
            }
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

    private static string DescribeAbiVersion(AzurPilotNativeDiagnostics native)
        => native.AbiVersion?.ToString(CultureInfo.InvariantCulture) ?? "неизвестна";

    private static string DescribeOpencvVersion(AzurPilotNativeDiagnostics native)
        => native.OpencvVersion?.ToString() ?? "неизвестна";

    private static string DescribeCapabilities(AzurPilotNativeDiagnostics native)
        => native.Capabilities.Count == 0 ? "нет" : string.Join(", ", native.Capabilities);

    private static string DescribeFlag(bool value) => value ? "да" : "нет";
}
