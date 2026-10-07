using AzurPilot.Core;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App;

/// <summary>
/// Bounded диагностический snapshot одного запуска: приложение, конфигурация, native boundary, MuMu,
/// Android и состояние игры Azur Lane.
/// </summary>
/// <remarks>
/// <para>
/// Секции Android и Azur Lane собираются только чтением: snapshot не подключает ADB, не запускает и не
/// останавливает ни игру, ни эмулятор. Отказ любой секции — данные snapshot, а не причина отказа запуска.
/// </para>
/// </remarks>
/// <param name="Application">Сведения о сборке, runtime и процессе.</param>
/// <param name="Configuration">Bounded сведения о загруженной конфигурации без её полного дампа.</param>
/// <param name="Native">Сведения о native boundary: доступность, совместимость и фактические evidence.</param>
/// <param name="MuMu">Сведения о MuMu: обнаружение установки, выбор экземпляра и host-side состояние.</param>
/// <param name="Android">
/// Сведения о Android: bundled ADB, точный endpoint и наблюдённая готовность Android.
/// </param>
/// <param name="AzurLane">
/// Сведения о продукте Azur Lane Global/EN: package identity и наблюдённое состояние игры.
/// </param>
public sealed record AzurPilotDiagnosticReport(
    AzurPilotApplicationDiagnostics Application,
    AzurPilotConfigurationDiagnostics Configuration,
    AzurPilotNativeDiagnostics Native,
    AzurPilotMuMuDiagnostics MuMu,
    AzurPilotAndroidDiagnostics Android,
    AzurPilotAzurLaneDiagnostics AzurLane);

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
/// <para>
/// Секция намеренно не содержит ни документа конфигурации, ни её секций: диагностика сообщает только
/// источник, версии схемы, статус валидации и минимальный уровень логирования.
/// </para>
/// <para>
/// Версия схемы источника и версия эффективной схемы различаются для legacy-входа: документ v1
/// нормализуется к v2 в памяти, и на диск при этом ничего не пишется. Обе версии нужны диагностике,
/// чтобы нормализация была видна, а не выглядела молчаливой подменой конфигурации.
/// </para>
/// </remarks>
/// <param name="Source">Источник конфигурации: встроенные defaults или файл.</param>
/// <param name="SchemaVersion">Версия эффективной схемы загруженной конфигурации.</param>
/// <param name="SourceSchemaVersion">Версия схемы, которую объявил источник конфигурации.</param>
/// <param name="ValidationStatus">Статус валидации конфигурации.</param>
/// <param name="MinimumLevel">Минимальный уровень логирования из конфигурации.</param>
/// <param name="FilePath">Путь файла конфигурации; <see langword="null"/> для встроенных defaults.</param>
public sealed record AzurPilotConfigurationDiagnostics(
    AzurPilotConfigurationSource Source,
    int SchemaVersion,
    int SourceSchemaVersion,
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

    /// <summary>Признак того, что источником был legacy-документ, нормализованный к эффективной схеме.</summary>
    /// <value>
    /// <see langword="true"/>, если версии схемы источника и эффективной схемы различаются: диагностика
    /// сообщает о нормализации, не дампя конфигурацию.
    /// </value>
    public bool IsLegacySchemaNormalized => SourceSchemaVersion != SchemaVersion;

    /// <summary>Создаёт секцию из провалидированного snapshot конфигурации.</summary>
    /// <param name="snapshot">Загруженный snapshot конфигурации.</param>
    /// <returns>Bounded сведения о конфигурации.</returns>
    internal static AzurPilotConfigurationDiagnostics FromSnapshot(AzurPilotConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AzurPilotConfigurationDiagnostics(
            snapshot.Source,
            snapshot.EffectiveSchemaVersion,
            snapshot.SourceSchemaVersion,
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
