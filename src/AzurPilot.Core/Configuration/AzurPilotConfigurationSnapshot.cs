namespace AzurPilot.Core.Configuration;

/// <summary>
/// Immutable snapshot загруженной конфигурации вместе с её источником, форматом и версиями схемы.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot создаётся один раз при загрузке и не обновляется: file watcher и reloadOnChange
/// отсутствуют, поэтому изменение файла на диске не влияет на уже полученный snapshot. Создавать
/// snapshot вручную нельзя: единственный путь его получить — успешная загрузка через
/// <see cref="AzurPilotConfigurationLoader"/>, то есть snapshot всегда провалидирован.
/// </para>
/// <para>
/// Версия схемы, объявленная в источнике, и версия эффективной схемы различаются: legacy-документ JSON
/// нормализуется к v3 в памяти, поэтому snapshot несёт обе версии и позволяет отличить нормализованный
/// legacy-вход от канонического YAML, не дампя саму конфигурацию.
/// </para>
/// </remarks>
public sealed class AzurPilotConfigurationSnapshot
{
    private AzurPilotConfigurationSnapshot(
        AzurPilotConfiguration configuration,
        AzurPilotConfigurationSource source,
        AzurPilotConfigurationFormat sourceFormat,
        string? filePath,
        int sourceSchemaVersion)
    {
        Configuration = configuration;
        Source = source;
        SourceFormat = sourceFormat;
        FilePath = filePath;
        SourceSchemaVersion = sourceSchemaVersion;
    }

    /// <summary>Провалидированная конфигурация эффективной схемы.</summary>
    public AzurPilotConfiguration Configuration { get; }

    /// <summary>Источник конфигурации: встроенные defaults или файл.</summary>
    public AzurPilotConfigurationSource Source { get; }

    /// <summary>Формат файла, из которого прочитана конфигурация.</summary>
    /// <value>
    /// <see cref="AzurPilotConfigurationFormat.None"/> для встроенных defaults,
    /// <see cref="AzurPilotConfigurationFormat.Yaml"/> для канонического YAML схемы v3 и
    /// <see cref="AzurPilotConfigurationFormat.Json"/> для нормализованного legacy JSON.
    /// </value>
    public AzurPilotConfigurationFormat SourceFormat { get; }

    /// <summary>Полный путь файла, из которого прочитана конфигурация.</summary>
    /// <value>Путь существующего файла либо <see langword="null"/> для встроенных defaults.</value>
    public string? FilePath { get; }

    /// <summary>Версия схемы, которую объявил источник конфигурации.</summary>
    /// <value>
    /// Версия из документа (для legacy JSON — <see cref="AzurPilotConfigurationV1.LegacySchemaVersion"/>
    /// или <see cref="AzurPilotConfigurationV2.LegacySchemaVersion"/>) либо
    /// <see cref="AzurPilotConfiguration.CurrentSchemaVersion"/> для встроенных defaults и канонического
    /// YAML.
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
            AzurPilotConfigurationFormat.None,
            filePath: null,
            sourceSchemaVersion: AzurPilotConfiguration.CurrentSchemaVersion);

    internal static AzurPilotConfigurationSnapshot FromFile(
        string filePath,
        AzurPilotConfigurationFormat sourceFormat,
        AzurPilotConfiguration configuration,
        int sourceSchemaVersion)
        => new(configuration, AzurPilotConfigurationSource.File, sourceFormat, filePath, sourceSchemaVersion);
}
