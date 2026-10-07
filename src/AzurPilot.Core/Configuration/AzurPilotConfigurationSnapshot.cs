namespace AzurPilot.Core.Configuration;

/// <summary>
/// Immutable snapshot загруженной конфигурации вместе с её источником и версиями схемы.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot создаётся один раз при загрузке и не обновляется: file watcher и reloadOnChange
/// отсутствуют, поэтому изменение файла на диске не влияет на уже полученный snapshot. Создавать
/// snapshot вручную нельзя: единственный путь его получить — успешная загрузка через
/// <see cref="AzurPilotConfigurationLoader"/>, то есть snapshot всегда провалидирован.
/// </para>
/// <para>
/// Версия схемы, объявленная в источнике, и версия эффективной схемы различаются: legacy-документ v1
/// нормализуется к v2 в памяти, поэтому snapshot несёт обе версии и позволяет отличить нормализованный
/// legacy-вход от документа v2, не дампя саму конфигурацию.
/// </para>
/// </remarks>
public sealed class AzurPilotConfigurationSnapshot
{
    private AzurPilotConfigurationSnapshot(
        AzurPilotConfiguration configuration,
        AzurPilotConfigurationSource source,
        string? filePath,
        int sourceSchemaVersion)
    {
        Configuration = configuration;
        Source = source;
        FilePath = filePath;
        SourceSchemaVersion = sourceSchemaVersion;
    }

    /// <summary>Провалидированная конфигурация эффективной схемы.</summary>
    public AzurPilotConfiguration Configuration { get; }

    /// <summary>Источник конфигурации: встроенные defaults или файл.</summary>
    public AzurPilotConfigurationSource Source { get; }

    /// <summary>Полный путь файла, из которого прочитана конфигурация.</summary>
    /// <value>Путь существующего файла либо <see langword="null"/> для встроенных defaults.</value>
    public string? FilePath { get; }

    /// <summary>Версия схемы, которую объявил источник конфигурации.</summary>
    /// <value>
    /// Версия из документа (для legacy-входа — <see cref="AzurPilotConfigurationV1.LegacySchemaVersion"/>)
    /// либо <see cref="AzurPilotConfiguration.CurrentSchemaVersion"/> для встроенных defaults.
    /// </value>
    public int SourceSchemaVersion { get; }

    /// <summary>Версия эффективной схемы, которой соответствует <see cref="Configuration"/>.</summary>
    /// <value>
    /// Всегда <see cref="AzurPilotConfiguration.CurrentSchemaVersion"/>: legacy-вход нормализуется к
    /// текущей схеме в памяти, и на диск ничего не пишется.
    /// </value>
    public int EffectiveSchemaVersion => Configuration.SchemaVersion;

    /// <summary>Признак того, что используется встроенная конфигурация по умолчанию.</summary>
    public bool IsBuiltInDefaults => Source == AzurPilotConfigurationSource.BuiltInDefaults;

    /// <summary>Признак того, что источником был legacy-документ, нормализованный к текущей схеме.</summary>
    public bool IsLegacySchemaNormalized => SourceSchemaVersion != EffectiveSchemaVersion;

    internal static AzurPilotConfigurationSnapshot FromBuiltInDefaults()
        => new(
            AzurPilotConfigurationDefaults.Create(),
            AzurPilotConfigurationSource.BuiltInDefaults,
            filePath: null,
            sourceSchemaVersion: AzurPilotConfiguration.CurrentSchemaVersion);

    internal static AzurPilotConfigurationSnapshot FromFile(
        string filePath,
        AzurPilotConfiguration configuration,
        int sourceSchemaVersion)
        => new(configuration, AzurPilotConfigurationSource.File, filePath, sourceSchemaVersion);
}
