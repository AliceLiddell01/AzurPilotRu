using System.Globalization;
using System.Text;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Временный каталог для тестов конфигурации.
/// </summary>
/// <remarks>
/// Тесты работают только с файлами внутри этого каталога: они не читают реальный
/// <c>%LOCALAPPDATA%\AzurPilot\config.yaml</c> машины и не зависят от пользовательского окружения.
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

    /// <summary>Полный путь каталога конфигурации.</summary>
    internal string Root => _root;

    /// <summary>Полный путь канонического YAML-файла внутри временного каталога.</summary>
    internal string YamlConfigurationFilePath => Path.Combine(_root, "config.yaml");

    /// <summary>Полный путь legacy JSON-файла внутри временного каталога.</summary>
    internal string LegacyJsonConfigurationFilePath => Path.Combine(_root, "config.json");

    /// <summary>Полный путь legacy JSON-файла внутри временного каталога.</summary>
    /// <remarks>
    /// Историческое имя: до появления канонического YAML единственным файлом конфигурации был JSON,
    /// поэтому тесты host-а продолжают обращаться к нему этим свойством.
    /// </remarks>
    internal string ConfigurationFilePath => LegacyJsonConfigurationFilePath;

    /// <summary>Полный путь, по которому файла конфигурации заведомо нет.</summary>
    internal string MissingConfigurationFilePath => Path.Combine(_root, "absent", "config.json");

    /// <summary>Полный путь, по которому канонического YAML заведомо нет.</summary>
    internal string MissingYamlConfigurationFilePath => Path.Combine(_root, "absent", "config.yaml");

    /// <summary>Записывает текстовое содержимое legacy JSON-конфигурации и возвращает её путь.</summary>
    /// <param name="content">Содержимое файла в UTF-8 без BOM.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteConfiguration(string content)
        => WriteConfigurationBytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    /// <summary>Записывает содержимое legacy JSON-конфигурации байтами и возвращает её путь.</summary>
    /// <param name="content">Байты файла конфигурации.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteConfigurationBytes(byte[] content)
    {
        File.WriteAllBytes(LegacyJsonConfigurationFilePath, content);
        return LegacyJsonConfigurationFilePath;
    }

    /// <summary>Записывает текстовое содержимое канонического YAML и возвращает его путь.</summary>
    /// <param name="content">Содержимое файла в UTF-8 без BOM.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteYamlConfiguration(string content)
        => WriteYamlConfigurationBytes(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    /// <summary>Записывает содержимое канонического YAML байтами и возвращает его путь.</summary>
    /// <param name="content">Байты файла конфигурации.</param>
    /// <returns>Полный путь записанного файла.</returns>
    internal string WriteYamlConfigurationBytes(byte[] content)
    {
        File.WriteAllBytes(YamlConfigurationFilePath, content);
        return YamlConfigurationFilePath;
    }

    /// <summary>Создаёт каталог по пути, где ожидается файл конфигурации.</summary>
    /// <returns>Полный путь созданного каталога.</returns>
    internal string CreateDirectoryAtConfigurationPath()
    {
        _ = Directory.CreateDirectory(ConfigurationFilePath);
        return ConfigurationFilePath;
    }

    /// <summary>Возвращает пути резервных копий legacy-файла в порядке имени.</summary>
    /// <returns>Полные пути существующих файлов <c>config.json.bak-*</c>.</returns>
    internal string[] BackupFilePaths()
    {
        string[] backups = [.. Directory
            .EnumerateFiles(_root, "config.json.bak-*")
            .OrderBy(path => path, StringComparer.Ordinal)];
        return backups;
    }

    /// <summary>Возвращает имена всех файлов каталога в порядке имени.</summary>
    /// <returns>Имена файлов без пути.</returns>
    internal string[] FileNames()
    {
        string[] names = [.. Directory
            .EnumerateFiles(_root)
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)];
        return names;
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
