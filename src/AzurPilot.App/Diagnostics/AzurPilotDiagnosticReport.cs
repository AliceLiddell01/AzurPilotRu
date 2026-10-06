using AzurPilot.Core;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App;

/// <summary>
/// Bounded диагностический snapshot одного запуска: приложение, конфигурация и native boundary.
/// </summary>
/// <param name="Application">Сведения о сборке, runtime и процессе.</param>
/// <param name="Configuration">Bounded сведения о загруженной конфигурации без её полного дампа.</param>
/// <param name="Native">Сведения о native boundary: доступность, совместимость и фактические evidence.</param>
public sealed record AzurPilotDiagnosticReport(
    AzurPilotApplicationDiagnostics Application,
    AzurPilotConfigurationDiagnostics Configuration,
    AzurPilotNativeDiagnostics Native);

/// <summary>Application-секция диагностического snapshot.</summary>
/// <param name="AssemblyName">Простое имя сборки application host.</param>
/// <param name="AssemblyVersion">Версия сборки application host.</param>
/// <param name="InformationalVersion">Informational version сборки — identity текущего build.</param>
/// <param name="RuntimeFramework">Описание .NET runtime текущего процесса.</param>
/// <param name="RuntimeIdentifier">Runtime identifier текущего процесса.</param>
/// <param name="OperatingSystem">Описание операционной системы.</param>
/// <param name="OperatingSystemArchitecture">Архитектура операционной системы.</param>
/// <param name="ProcessArchitecture">Архитектура текущего процесса.</param>
/// <param name="ProcessId">Идентификатор текущего процесса.</param>
/// <param name="Is64BitProcess">Признак 64-битного процесса.</param>
public sealed record AzurPilotApplicationDiagnostics(
    string AssemblyName,
    string AssemblyVersion,
    string InformationalVersion,
    string RuntimeFramework,
    string RuntimeIdentifier,
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    int ProcessId,
    bool Is64BitProcess);

/// <summary>Configuration-секция диагностического snapshot.</summary>
/// <remarks>
/// Секция намеренно не содержит ни документа конфигурации, ни её секций: диагностика сообщает только
/// источник, версию схемы, статус валидации и минимальный уровень логирования.
/// </remarks>
/// <param name="Source">Источник конфигурации: встроенные defaults или файл.</param>
/// <param name="SchemaVersion">Версия схемы загруженной конфигурации.</param>
/// <param name="ValidationStatus">Статус валидации конфигурации.</param>
/// <param name="MinimumLevel">Минимальный уровень логирования из конфигурации.</param>
/// <param name="FilePath">Путь файла конфигурации; <see langword="null"/> для встроенных defaults.</param>
public sealed record AzurPilotConfigurationDiagnostics(
    AzurPilotConfigurationSource Source,
    int SchemaVersion,
    string ValidationStatus,
    LogLevel MinimumLevel,
    string? FilePath)
{
    /// <summary>Статус валидации успешно загруженной конфигурации.</summary>
    /// <remarks>
    /// Snapshot существует только после успешной строгой валидации, поэтому отдельного состояния
    /// «частично валидна» у конфигурации нет.
    /// </remarks>
    internal const string ValidStatus = "valid";

    /// <summary>Создаёт секцию из провалидированного snapshot конфигурации.</summary>
    /// <param name="snapshot">Загруженный snapshot конфигурации.</param>
    /// <returns>Bounded сведения о конфигурации.</returns>
    internal static AzurPilotConfigurationDiagnostics FromSnapshot(AzurPilotConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AzurPilotConfigurationDiagnostics(
            snapshot.Source,
            snapshot.Configuration.SchemaVersion,
            ValidStatus,
            snapshot.Configuration.Diagnostics.MinimumLevel,
            snapshot.FilePath);
    }
}

/// <summary>Native-секция диагностического snapshot.</summary>
/// <remarks>
/// Недоступная или несовместимая граница — не исключение диагностики, а её результат: секция описывает
/// доступность границы и, если boundary использовать нельзя, несёт application-level отказ
/// <see cref="Failure"/>. Решение о продолжении запуска принимает startup, а не диагностика.
/// </remarks>
/// <param name="IsAvailable">Признак того, что native boundary ответила на запрос.</param>
/// <param name="IsAbiCompatible">Признак совместимости ABI и обязательных capability.</param>
/// <param name="AbiVersion">Фактическая версия ABI native библиотеки; <see langword="null"/>, если boundary недоступна.</param>
/// <param name="OpencvVersion">Фактическая версия OpenCV native библиотеки; <see langword="null"/>, если boundary недоступна.</param>
/// <param name="Capabilities">Capability, подтверждённые native библиотекой.</param>
/// <param name="OpencvExecuted">Признак того, что код OpenCV реально исполнялся при сборе сведений.</param>
/// <param name="BuildInfo">Строка сведений о сборке native библиотеки.</param>
/// <param name="CompatibilityReason">Причина совместимости или несовместимости границы.</param>
/// <param name="Failure">Application-level отказ границы; <see langword="null"/>, если границу можно использовать.</param>
public sealed record AzurPilotNativeDiagnostics(
    bool IsAvailable,
    bool IsAbiCompatible,
    uint? AbiVersion,
    Version? OpencvVersion,
    IReadOnlyList<string> Capabilities,
    bool OpencvExecuted,
    string? BuildInfo,
    string CompatibilityReason,
    ApplicationFailure? Failure)
{
    /// <summary>Создаёт секцию из реально полученных сведений о native boundary.</summary>
    /// <param name="info">Сведения, полученные от native библиотеки.</param>
    /// <param name="compatibility">Результат проверки совместимости границы.</param>
    /// <param name="mapIncompatibility">
    /// Проекция несовместимости границы в application-level отказ: отказ собирает владелец проекции
    /// <see cref="NativeBoundaryFailureMapper"/>, а не эта секция диагностики.
    /// </param>
    /// <returns>Секция с фактическими evidence native boundary.</returns>
    internal static AzurPilotNativeDiagnostics FromBoundary(
        NativeBoundaryInfo info,
        NativeBoundaryCompatibility compatibility,
        Func<string, ApplicationFailure> mapIncompatibility)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(mapIncompatibility);

        return new AzurPilotNativeDiagnostics(
            IsAvailable: true,
            IsAbiCompatible: compatibility.IsCompatible,
            info.AbiVersion,
            info.OpencvVersion,
            [.. info.Capabilities],
            (info.BuildFlags & AzurPilotNativeBridge.BuildFlagOpencvExecuted) != 0,
            info.BuildInfo,
            compatibility.Reason,
            compatibility.IsCompatible ? null : mapIncompatibility(compatibility.Reason));
    }

    /// <summary>Создаёт секцию для недоступной или отвергнутой native boundary.</summary>
    /// <param name="failure">Application-level отказ, полученный из ошибки границы.</param>
    /// <returns>Секция, описывающая недоступную границу.</returns>
    internal static AzurPilotNativeDiagnostics FromFailure(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AzurPilotNativeDiagnostics(
            IsAvailable: false,
            IsAbiCompatible: false,
            AbiVersion: null,
            OpencvVersion: null,
            Capabilities: [],
            OpencvExecuted: false,
            BuildInfo: null,
            CompatibilityReason: failure.Message,
            Failure: failure);
    }
}
