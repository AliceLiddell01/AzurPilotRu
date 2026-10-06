namespace AzurPilot.Core.Configuration;

/// <summary>Источник загруженной конфигурации.</summary>
public enum AzurPilotConfigurationSource
{
    /// <summary>Файла нет: используется встроенная конфигурация по умолчанию.</summary>
    BuiltInDefaults,

    /// <summary>Конфигурация полностью прочитана и провалидирована из существующего файла.</summary>
    File,
}
