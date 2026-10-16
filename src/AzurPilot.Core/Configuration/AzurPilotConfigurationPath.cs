namespace AzurPilot.Core.Configuration;

/// <summary>
/// Владелец runtime-путей пользовательской конфигурации: канонического YAML и legacy JSON.
/// </summary>
/// <remarks>
/// <para>
/// Оба пути вычисляются через BCL API, а не хранятся абсолютными строками: абсолютный путь конкретной
/// машины в репозитории запрещён. Каталог приложения не создаётся: если файла нет, загрузчик возвращает
/// встроенные defaults.
/// </para>
/// <para>
/// Пути не равнозначны: канонический формат — YAML схемы v3, а JSON остаётся только legacy-входом и
/// поддерживается для чтения и явной миграции.
/// </para>
/// </remarks>
public static class AzurPilotConfigurationPath
{
    /// <summary>Имя пользовательского каталога приложения внутри LocalApplicationData.</summary>
    public const string ApplicationFolderName = "AzurPilot";

    private const string CanonicalConfigurationFileName = "config.yaml";

    private const string LegacyJsonConfigurationFileName = "config.json";

    /// <summary>Возвращает runtime-путь канонической конфигурации по умолчанию.</summary>
    /// <returns>Абсолютный путь <c>%LOCALAPPDATA%\AzurPilot\config.yaml</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Каталог <c>LocalApplicationData</c> не определён в окружении процесса: абсолютный путь вычислить
    /// нельзя, а относительный путь нарушил бы контракт владельца пути.
    /// </exception>
    public static string GetDefaultRuntimePath()
        => CombineWithApplicationFolder(CanonicalConfigurationFileName);

    /// <summary>Возвращает runtime-путь legacy JSON-конфигурации.</summary>
    /// <returns>Абсолютный путь <c>%LOCALAPPDATA%\AzurPilot\config.json</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Каталог <c>LocalApplicationData</c> не определён в окружении процесса: абсолютный путь вычислить
    /// нельзя, а относительный путь нарушил бы контракт владельца пути.
    /// </exception>
    public static string GetLegacyJsonRuntimePath()
        => CombineWithApplicationFolder(LegacyJsonConfigurationFileName);

    private static string CombineWithApplicationFolder(string fileName)
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

        return Path.Combine(localApplicationData, ApplicationFolderName, fileName);
    }
}
