namespace AzurPilot.Core.Configuration;

/// <summary>
/// Immutable snapshot загруженной конфигурации вместе с её источником.
/// </summary>
/// <remarks>
/// Snapshot создаётся один раз при загрузке и не обновляется: file watcher и reloadOnChange
/// отсутствуют, поэтому изменение файла на диске не влияет на уже полученный snapshot. Создавать
/// snapshot вручную нельзя: единственный путь его получить — успешная загрузка через
/// <see cref="AzurPilotConfigurationLoader"/>, то есть snapshot всегда провалидирован.
/// </remarks>
public sealed class AzurPilotConfigurationSnapshot
{
    private AzurPilotConfigurationSnapshot(
        AzurPilotConfiguration configuration,
        AzurPilotConfigurationSource source,
        string? filePath)
    {
        Configuration = configuration;
        Source = source;
        FilePath = filePath;
    }

    /// <summary>Провалидированная конфигурация.</summary>
    public AzurPilotConfiguration Configuration { get; }

    /// <summary>Источник конфигурации: встроенные defaults или файл.</summary>
    public AzurPilotConfigurationSource Source { get; }

    /// <summary>Полный путь файла, из которого прочитана конфигурация.</summary>
    /// <value>Путь существующего файла либо <see langword="null"/> для встроенных defaults.</value>
    public string? FilePath { get; }

    /// <summary>Признак того, что используется встроенная конфигурация по умолчанию.</summary>
    public bool IsBuiltInDefaults => Source == AzurPilotConfigurationSource.BuiltInDefaults;

    internal static AzurPilotConfigurationSnapshot FromBuiltInDefaults()
        => new(
            AzurPilotConfigurationDefaults.Create(),
            AzurPilotConfigurationSource.BuiltInDefaults,
            filePath: null);

    internal static AzurPilotConfigurationSnapshot FromFile(string filePath, AzurPilotConfiguration configuration)
        => new(configuration, AzurPilotConfigurationSource.File, filePath);
}
