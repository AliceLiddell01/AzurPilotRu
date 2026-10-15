using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает runtime-политику двух источников: канонический YAML — единственный формат записи, legacy
/// JSON допускается только пока YAML отсутствует, одновременное присутствие обоих файлов даёт
/// детерминированный отказ, а отсутствие обоих — встроенные defaults.
/// </summary>
/// <remarks>
/// Проверки идут через <see cref="AzurPilotConfigurationLoader.LoadFromSources"/> с путями внутри
/// временного каталога: пользовательский <c>%LOCALAPPDATA%\AzurPilot</c> машины не читается и не
/// изменяется.
/// </remarks>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationRuntimePolicyTests
{
    private const string CanonicalYamlDocument =
        """
        schemaVersion: 3
        ui:
          mode: diagnostic
        mumu:
          instance: "mumu:1"
        diagnostics:
          minimumLevel: Error
        logging:
          viewer:
            enabled: true
        """;

    private const string LegacyV2Document =
        """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Warning"},"mumu":{"instance":"mumu:2"}}""";

    private const string LegacyV1Document =
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Critical"}}""";

    [Fact(DisplayName = "Только канонический YAML: загружается он, а не defaults и не legacy JSON")]
    public void OnlyCanonicalYamlIsLoaded()
    {
        using TemporaryConfigurationDirectory directory = new();
        string yamlPath = directory.WriteYamlConfiguration(CanonicalYamlDocument);

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.LoadFromSources(yamlPath, directory.LegacyJsonConfigurationFilePath);

        Assert.True(result.IsSuccess, $"Канонический YAML обязан загружаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationFormat.Yaml, snapshot.SourceFormat);
        Assert.Equal(yamlPath, snapshot.FilePath);
        Assert.Equal(OperatorUiMode.Diagnostic, snapshot.Configuration.Ui.Mode);
        Assert.Equal("mumu:1", snapshot.Configuration.MuMu.Instance);
        Assert.True(snapshot.Configuration.Logging.Viewer.Enabled);
        Assert.False(snapshot.IsLegacySchemaNormalized);
    }

    [Theory(DisplayName = "Только legacy JSON: он нормализуется к v3 в памяти и файл не меняется")]
    [InlineData(LegacyV1Document, 1)]
    [InlineData(LegacyV2Document, 2)]
    public void OnlyLegacyJsonIsNormalizedInMemory(string document, int sourceSchemaVersion)
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(document);
        byte[] before = File.ReadAllBytes(jsonPath);

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.LoadFromSources(directory.YamlConfigurationFilePath, jsonPath);

        Assert.True(result.IsSuccess, $"Legacy-документ обязан читаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationFormat.Json, snapshot.SourceFormat);
        Assert.Equal(jsonPath, snapshot.FilePath);
        Assert.Equal(sourceSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.True(snapshot.IsLegacySchemaNormalized);

        // Значения новых секций приходят от владельца defaults, а файл на диске не переписывается.
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(defaults.Ui.Mode, snapshot.Configuration.Ui.Mode);
        Assert.Equal(defaults.Logging.Viewer.Enabled, snapshot.Configuration.Logging.Viewer.Enabled);
        Assert.Equal(before, File.ReadAllBytes(jsonPath));
    }

    [Fact(DisplayName = "Legacy JSON v1 не создаёт канонический YAML на диске")]
    public void LegacyJsonLoadDoesNotCreateCanonicalYaml()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV1Document);

        _ = AzurPilotConfigurationLoader.LoadFromSources(directory.YamlConfigurationFilePath, jsonPath);

        Assert.False(
            File.Exists(directory.YamlConfigurationFilePath),
            "Нормализация legacy-входа существует только в памяти: YAML не создаётся.");
    }

    [Fact(DisplayName = "Оба файла существуют: детерминированный отказ без выбора «победителя»")]
    public void BothSourcesExistIsRejectedDeterministically()
    {
        using TemporaryConfigurationDirectory directory = new();
        string yamlPath = directory.WriteYamlConfiguration(CanonicalYamlDocument);
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "одновременно существуют канонический YAML и legacy JSON");
        Assert.False(failure.IsRetryable);

        // Отказ называет оба источника и требует оставить один.
        IReadOnlyDictionary<string, string> details = failure.Details!;
        Assert.Equal(yamlPath, details["config_path"]);
        Assert.Equal(jsonPath, details["legacy_config_path"]);
        Assert.Contains(yamlPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains(jsonPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Оставьте один источник", failure.Message, StringComparison.Ordinal);
        Assert.Contains("переименуйте или удалите", failure.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Ни одного файла: встроенные defaults схемы v3")]
    public void NeitherSourceProducesBuiltInDefaults()
    {
        using TemporaryConfigurationDirectory directory = new();

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.LoadFromSources(
                directory.MissingYamlConfigurationFilePath,
                directory.MissingConfigurationFilePath);

        Assert.True(result.IsSuccess, $"Отсутствие обоих файлов — валидный сценарий, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.True(snapshot.IsBuiltInDefaults);
        Assert.Equal(AzurPilotConfigurationFormat.None, snapshot.SourceFormat);
        Assert.Equal(AzurPilotConfigurationDefaults.Create(), snapshot.Configuration);
        Assert.Equal(LogLevel.Information, snapshot.Configuration.Diagnostics.MinimumLevel);

        // Загрузка ничего не создаёт: отсутствие конфигурации не приводит к записи файлов.
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
        Assert.False(File.Exists(directory.LegacyJsonConfigurationFilePath));
    }

    [Fact(DisplayName = "Невалидный канонический YAML не подменяется defaults: политика fail-closed")]
    public void InvalidCanonicalYamlIsNotReplacedByDefaults()
    {
        using TemporaryConfigurationDirectory directory = new();
        string yamlPath = directory.WriteYamlConfiguration("schemaVersion: 3\nunknown: true\n");

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.LoadFromSources(
                yamlPath,
                directory.MissingConfigurationFilePath);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "невалидный канонический YAML при отсутствии legacy-дубля");
        Assert.Equal(yamlPath, failure.Details!["config_path"]);
    }

    [Fact(DisplayName = "Пустой и относительный путь в runtime-политике — ошибка программирования")]
    public void NonAbsolutePathsAreProgrammingErrors()
    {
        using TemporaryConfigurationDirectory directory = new();

        _ = Assert.Throws<ArgumentNullException>(
            () => AzurPilotConfigurationLoader.LoadFromSources(null!, directory.LegacyJsonConfigurationFilePath));
        _ = Assert.Throws<ArgumentException>(
            () => AzurPilotConfigurationLoader.LoadFromSources("  ", directory.LegacyJsonConfigurationFilePath));
        _ = Assert.Throws<ArgumentException>(
            () => AzurPilotConfigurationLoader.LoadFromSources(
                directory.YamlConfigurationFilePath,
                Path.Combine("config", "config.json")));
    }
}
