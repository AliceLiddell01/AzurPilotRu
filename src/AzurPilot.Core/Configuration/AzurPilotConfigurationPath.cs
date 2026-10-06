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
    /// <exception cref="InvalidOperationException">
    /// Каталог <c>LocalApplicationData</c> не определён в окружении процесса: абсолютный путь вычислить
    /// нельзя, а относительный путь нарушил бы контракт владельца пути.
    /// </exception>
    public static string GetDefaultRuntimePath()
    {
        // Пустой LocalApplicationData дал бы относительный путь, который загрузчик обязан отклонить как
        // ошибку программирования. Владелец пути сообщает причину сам, а не отдаёт наружу невалидный путь.
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "Каталог LocalApplicationData не определён в окружении процесса: runtime-путь конфигурации "
                + "нельзя вычислить как абсолютный путь.");
        }

        return Path.Combine(
            localApplicationData,
            ApplicationFolderName,
            ConfigurationFileName);
    }
}
