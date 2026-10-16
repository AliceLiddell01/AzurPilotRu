using AzurPilot.Core.Configuration;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает, что оба runtime-пути конфигурации вычисляются из стандартного каталога приложения и не
/// хранятся абсолютными строками конкретной машины.
/// </summary>
/// <remarks>
/// Тест проверяет только форму путей и не читает файлы по ним: реальные
/// <c>%LOCALAPPDATA%\AzurPilot\config.yaml</c> и <c>config.json</c> машины на результат тестов
/// конфигурации не влияют.
/// </remarks>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationPathTests
{
    [Fact(DisplayName = "Канонический runtime-путь лежит в LocalApplicationData и указывает на config.yaml")]
    public void DefaultRuntimePathPointsToCanonicalYaml()
    {
        string path = AzurPilotConfigurationPath.GetDefaultRuntimePath();

        Assert.True(Path.IsPathFullyQualified(path), $"Путь по умолчанию обязан быть абсолютным: «{path}».");
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localApplicationData, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("AzurPilot", "config.yaml"),
            path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Legacy runtime-путь указывает на config.json в том же каталоге приложения")]
    public void LegacyJsonRuntimePathPointsToLegacyJson()
    {
        string path = AzurPilotConfigurationPath.GetLegacyJsonRuntimePath();

        Assert.True(Path.IsPathFullyQualified(path), $"Legacy-путь обязан быть абсолютным: «{path}».");
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localApplicationData, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("AzurPilot", "config.json"),
            path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Оба пути различаются именами файлов, но лежат в одном каталоге приложения")]
    public void BothRuntimePathsShareApplicationDirectory()
    {
        string canonical = AzurPilotConfigurationPath.GetDefaultRuntimePath();
        string legacy = AzurPilotConfigurationPath.GetLegacyJsonRuntimePath();

        Assert.NotEqual(canonical, legacy, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetDirectoryName(canonical),
            Path.GetDirectoryName(legacy),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("config.yaml", Path.GetFileName(canonical), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("config.json", Path.GetFileName(legacy), StringComparer.OrdinalIgnoreCase);
    }
}
