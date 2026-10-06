using System.Globalization;
using System.Text;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Временный каталог для тестов конфигурации.
/// </summary>
/// <remarks>
/// Тесты работают только с файлами внутри этого каталога: они не читают реальный config.json машины и
/// не зависят от %LOCALAPPDATA% пользователя.
/// </remarks>
internal sealed class TemporaryConfigurationDirectory : IDisposable
{
    private readonly string _root;

    internal TemporaryConfigurationDirectory()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "AzurPilot.Tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(_root);
    }

    /// <summary>Полный путь файла конфигурации внутри временного каталога.</summary>
    internal string ConfigurationFilePath => Path.Combine(_root, "config.json");

    /// <summary>Полный путь, по которому файла конфигурации заведомо нет.</summary>
    internal string MissingConfigurationFilePath => Path.Combine(_root, "absent", "config.json");

    /// <summary>Записывает текстовое содержимое конфигурации и возвращает её путь.</summary>
    /// <param name="content">Содержимое файла в UTF-8 без BOM.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteConfiguration(string content)
        => WriteConfigurationBytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    /// <summary>Записывает содержимое конфигурации байтами и возвращает её путь.</summary>
    /// <param name="content">Байты файла конфигурации.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteConfigurationBytes(byte[] content)
    {
        File.WriteAllBytes(ConfigurationFilePath, content);
        return ConfigurationFilePath;
    }

    /// <summary>Создаёт каталог по пути, где ожидается файл конфигурации.</summary>
    /// <returns>Полный путь созданного каталога.</returns>
    internal string CreateDirectoryAtConfigurationPath()
    {
        _ = Directory.CreateDirectory(ConfigurationFilePath);
        return ConfigurationFilePath;
    }

    /// <summary>Удаляет временный каталог вместе с содержимым.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Каталог мог остаться занятым: это не влияет на результат теста.
        }
        catch (UnauthorizedAccessException)
        {
            // Каталог мог остаться занятым: это не влияет на результат теста.
        }

        GC.SuppressFinalize(this);
    }
}
