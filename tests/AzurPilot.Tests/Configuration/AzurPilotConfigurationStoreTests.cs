using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Configuration;

public sealed class AzurPilotConfigurationStoreTests
{
    [Fact(DisplayName = "Опубликованный полный пример YAML читается настоящим loader без изменений")]
    public void DocumentedYamlExampleIsValid()
    {
        string documentation = File.ReadAllText(Path.Combine(PinnedVersions.RepositoryRoot,
            "docs", "reference", "application-configuration.md")).ReplaceLineEndings("\n");
        string yaml = documentation.Split("```yaml\n", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration(yaml);
        ApplicationResult<AzurPilotConfigurationSnapshot> loaded = AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath);
        Assert.True(loaded.IsSuccess);
        Assert.Equal("mumu:1", loaded.Value!.Configuration.MuMu.Instance);
        Assert.Equal(OperatorUiMode.Normal, loaded.Value.Configuration.Ui.Mode);
        Assert.False(loaded.Value.Configuration.Logging.Viewer.Enabled);
    }
    [Fact(DisplayName = "Каталог вместо legacy-файла не превращается во встроенные defaults")]
    public void DirectoryInPlaceOfSourceFailsClosed()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = Directory.CreateDirectory(directory.LegacyJsonConfigurationFilePath);
        ApplicationResult<AzurPilotConfigurationSnapshot> loaded = AzurPilotConfigurationLoader.LoadFromSources(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath);
        Assert.True(loaded.IsFailure);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, loaded.FailureInfo!.Code);
    }

    [Fact(DisplayName = "Чрезмерный документ ограничен до materialization YAML дерева")]
    public void OversizedInputFailsClosed()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration(new string(' ', 65537));
        ApplicationResult<AzurPilotConfigurationSnapshot> loaded = AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath);
        Assert.True(loaded.IsFailure);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, loaded.FailureInfo!.Code);
    }
    [Fact(DisplayName = "Сохранённый exact target восстанавливается после новой загрузки без изменения snapshot")]
    public void SaveTargetSurvivesReloadAndKeepsOriginalSnapshot()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot original = Load(directory);
        AzurPilotConfiguration changed = WithTarget(original.Configuration, "mumu:7");

        ApplicationResult<AzurPilotConfigurationSnapshot> saved = Save(directory, original, changed);

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.FailureInfo!.Message : null);
        Assert.Equal(changed, Load(directory).Configuration);
        Assert.Equal("auto", original.Configuration.MuMu.Instance);
        Assert.False(File.Exists(directory.LegacyJsonConfigurationFilePath));
    }

    [Fact(DisplayName = "Изменение YAML оставляет точную резервную копию предыдущего файла")]
    public void SavingExistingYamlBacksUpOriginal()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot original = Load(directory);
        AzurPilotConfigurationSnapshot first = Save(directory, original, original.Configuration).Value!;
        byte[] before = File.ReadAllBytes(directory.YamlConfigurationFilePath);

        Assert.True(Save(directory, first, WithTarget(first.Configuration, "mumu:8")).IsSuccess);

        string backup = Assert.Single(Directory.GetFiles(directory.Root, "config.yaml.bak-*"));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    [Fact(DisplayName = "Явное сохранение legacy JSON переносит значения и оставляет backup")]
    public void SavingLegacyMigratesWithBackup()
    {
        using TemporaryConfigurationDirectory directory = new();
        string json = """{"schemaVersion":2,"mumu":{"instance":"mumu:7"},"diagnostics":{"minimumLevel":"Warning"}}""";
        _ = directory.WriteConfiguration(json);
        AzurPilotConfigurationSnapshot original = Load(directory);

        Assert.True(Save(directory, original, WithTarget(original.Configuration, "mumu:8")).IsSuccess);

        Assert.Equal(json, File.ReadAllText(Assert.Single(directory.BackupFilePaths())));
        Assert.False(File.Exists(directory.LegacyJsonConfigurationFilePath));
        Assert.Equal("mumu:8", Load(directory).Configuration.MuMu.Instance);
        Assert.Equal(original.Configuration.Diagnostics, Load(directory).Configuration.Diagnostics);
    }

    [Fact(DisplayName = "Внешнее изменение источника не перезаписывается устаревшим snapshot")]
    public void StaleSaveFailsClosed()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot original = Load(directory);
        Assert.True(Save(directory, original, WithTarget(original.Configuration, "mumu:8")).IsSuccess);
        byte[] expected = File.ReadAllBytes(directory.YamlConfigurationFilePath);

        ApplicationResult<AzurPilotConfigurationSnapshot> stale = Save(directory, original, original.Configuration);

        Assert.True(stale.IsFailure);
        Assert.Equal(expected, File.ReadAllBytes(directory.YamlConfigurationFilePath));
    }

    [Fact(DisplayName = "Невалидный target не создаёт YAML и не меняет legacy JSON")]
    public void InvalidCandidateDoesNotTouchOriginal()
    {
        using TemporaryConfigurationDirectory directory = new();
        string json = """{"schemaVersion":2,"mumu":{"instance":"auto"},"diagnostics":{"minimumLevel":"Information"}}""";
        _ = directory.WriteConfiguration(json);
        AzurPilotConfigurationSnapshot original = Load(directory);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = Save(directory, original, WithTarget(original.Configuration, "mumu:-1"));

        Assert.True(result.IsFailure);
        Assert.Equal(json, File.ReadAllText(directory.LegacyJsonConfigurationFilePath));
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
        Assert.Empty(directory.BackupFilePaths());
        Assert.DoesNotContain(
            directory.FileNames(),
            name => name.Contains(".tmp.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Ошибка переноса legacy JSON отменяет создание YAML и сохраняет исходник")]
    public void FailedLegacyBackupMoveLeavesOriginalJsonAsTheOnlySource()
    {
        using TemporaryConfigurationDirectory directory = new();
        string json = """{"schemaVersion":2,"mumu":{"instance":"mumu:7"},"diagnostics":{"minimumLevel":"Warning"}}""";
        _ = directory.WriteConfiguration(json);
        AzurPilotConfigurationSnapshot original = Load(directory);
        using FileStream sourceLock = new(
            directory.LegacyJsonConfigurationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            Save(directory, original, WithTarget(original.Configuration, "mumu:8"));

        Assert.True(result.IsFailure);
        Assert.Equal(json, File.ReadAllText(directory.LegacyJsonConfigurationFilePath));
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
        Assert.Empty(directory.BackupFilePaths());
    }

    private static AzurPilotConfiguration WithTarget(AzurPilotConfiguration configuration, string target)
        => configuration with { MuMu = new MuMuConfiguration { Instance = target } };

    private static AzurPilotConfigurationSnapshot Load(TemporaryConfigurationDirectory directory)
        => AzurPilotConfigurationLoader.LoadFromSources(directory.YamlConfigurationFilePath,
            directory.LegacyJsonConfigurationFilePath).Value!;

    private static ApplicationResult<AzurPilotConfigurationSnapshot> Save(TemporaryConfigurationDirectory directory,
        AzurPilotConfigurationSnapshot expected, AzurPilotConfiguration configuration)
        => AzurPilotConfigurationStore.Save(directory.YamlConfigurationFilePath,
            directory.LegacyJsonConfigurationFilePath, expected, configuration);
}
