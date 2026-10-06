namespace AzurPilot.Core.Configuration;

/// <summary>
/// Владелец runtime-пути пользовательской конфигурации.
/// </summary>
/// <remarks>
/// Путь вычисляется через BCL API, а не хранится абсолютной строкой: абсолютный путь конкретной
/// машины в репозитории запрещён. Каталог приложения не создаётся: если файла нет, загрузчик
/// возвращает встроенные defaults.
/// </remarks>
public static class AzurPilotConfigurationPath
{
    private const string ApplicationFolderName = "AzurPilot";

    private const string ConfigurationFileName = "config.json";

    /// <summary>Возвращает runtime-путь конфигурации по умолчанию.</summary>
    /// <returns>Абсолютный путь <c>%LOCALAPPDATA%\AzurPilot\config.json</c>.</returns>
    public static string GetDefaultRuntimePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName,
            ConfigurationFileName);
}
