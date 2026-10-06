using AzurPilot.Core.Configuration;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает, что runtime-путь конфигурации по умолчанию вычисляется из стандартного каталога
/// приложения и не хранится абсолютной строкой конкретной машины.
/// </summary>
/// <remarks>
/// Тест проверяет только форму пути и не читает файл по этому пути: реальный config.json машины и
/// %LOCALAPPDATA% пользователя на результат тестов конфигурации не влияют.
/// </remarks>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationPathTests
{
    [Fact(DisplayName = "Runtime-путь по умолчанию лежит в LocalApplicationData и указывает на AzurPilot/config.json")]
    public void DefaultRuntimePathIsComputedFromLocalApplicationData()
    {
        string path = AzurPilotConfigurationPath.GetDefaultRuntimePath();

        Assert.True(Path.IsPathFullyQualified(path), $"Путь по умолчанию обязан быть абсолютным: «{path}».");
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localApplicationData, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("AzurPilot", "config.json"),
            path,
            StringComparison.OrdinalIgnoreCase);
    }
}
