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
        string yaml = ExtractCanonicalYaml(documentation);
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
    [Fact(DisplayName = "Сохранённый точного целевого экземпляра восстанавливается после новой загрузки без изменения snapshot")]
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

    [Theory(DisplayName = "Семантически равные внешние правки сохраняются побайтно при отказе")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EquivalentExternalEditFailsClosed(int representation)
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot defaults = Load(directory);
        AzurPilotConfigurationSnapshot original = Save(directory, defaults, defaults.Configuration).Value!;
        string yaml = File.ReadAllText(directory.YamlConfigurationFilePath);
        string edited = representation switch
        {
            0 => "# пользовательский комментарий\n" + yaml,
            1 => yaml.ReplaceLineEndings("\r\n"),
            _ => "\uFEFF" + yaml,
        };
        File.WriteAllText(directory.YamlConfigurationFilePath, edited);
        byte[] before = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        Assert.Equal(original.Configuration, Load(directory).Configuration);

        Assert.True(Save(directory, original, WithTarget(original.Configuration, "mumu:8")).IsFailure);

        Assert.Equal(before, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.bak-*"));
    }

    [Fact(DisplayName = "Удаление прочитанного YAML не разрешает сохранение устаревшего снимка")]
    public void DeletedSourceFailsClosed()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot defaults = Load(directory);
        AzurPilotConfigurationSnapshot original = Save(directory, defaults, defaults.Configuration).Value!;
        File.Delete(directory.YamlConfigurationFilePath);
        Assert.True(Save(directory, original, original.Configuration).IsFailure);
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
    }

    [Theory(DisplayName = "Конкурирующие сохранения одного снимка дают одного победителя и точный backup")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSavesKeepOneWinnerAndOriginalBackup(bool existing)
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot defaults = Load(directory);
        AzurPilotConfigurationSnapshot original = existing ? Save(directory, defaults, defaults.Configuration).Value! : defaults;
        byte[] before = existing ? File.ReadAllBytes(directory.YamlConfigurationFilePath) : [];
        Task<ApplicationResult<AzurPilotConfigurationSnapshot>>[] writes = [.. Enumerable.Range(1, 8)
            .Select(index => Task.Run(() => Save(directory, original, WithTarget(original.Configuration, $"mumu:{index}"))))];
        ApplicationResult<AzurPilotConfigurationSnapshot>[] results = await Task.WhenAll(writes);
        ApplicationResult<AzurPilotConfigurationSnapshot> winner = Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(winner.Value!.Configuration, Load(directory).Configuration);
        string[] backups = Directory.GetFiles(directory.Root, "*.bak-*");
        if (existing)
        {
            Assert.Equal(before, File.ReadAllBytes(Assert.Single(backups)));
        }
        else
        {
            Assert.Empty(backups);
        }
    }

    [Fact(DisplayName = "Замена файла между проверкой и публикацией обнаруживается по реальному backup")]
    public void LateReplacementRestoresExternalBytesAndReportsConflict()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot defaults = Load(directory);
        AzurPilotConfigurationSnapshot original = Save(directory, defaults, defaults.Configuration).Value!;
        byte[] external = System.Text.Encoding.UTF8.GetBytes("# поздняя внешняя версия\n"
            + File.ReadAllText(directory.YamlConfigurationFilePath));
        ConfigurationFileOperations files = new()
        {
            Replace = (candidate, target, backup) =>
            {
                string replacement = target + ".external";
                File.WriteAllBytes(replacement, external);
                File.Replace(replacement, target, target + ".external-backup");
                File.Replace(candidate, target, backup);
            },
        };

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationStore.Save(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath,
            original, WithTarget(original.Configuration, "mumu:9"), out _, files);

        Assert.True(result.IsFailure);
        Assert.Contains("Конфликт", result.FailureInfo!.Message, StringComparison.Ordinal);
        Assert.Equal(external, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.Equal("mumu:9", AzurPilotConfigurationLoader.Load(
            CopyCandidateToYaml(directory, Assert.Single(Directory.GetFiles(directory.Root, "*.conflict-*")))).Value!.Configuration.MuMu.Instance);
    }

    private static string CopyCandidateToYaml(TemporaryConfigurationDirectory directory, string source)
    {
        string path = Path.Combine(directory.Root, "candidate.yaml");
        File.Copy(source, path);
        return path;
    }

    [Fact(DisplayName = "Отказ атомарной замены сохраняет исходные байты и удаляет временный файл")]
    public void FailedReplacePreservesSourceAndCleansCandidate()
    {
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfigurationSnapshot defaults = Load(directory);
        AzurPilotConfigurationSnapshot original = Save(directory, defaults, defaults.Configuration).Value!;
        byte[] before = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        ConfigurationFileOperations files = new() { Replace = static (_, _, _) => throw new IOException("управляемый отказ замены") };
        Assert.True(AzurPilotConfigurationStore.Save(directory.YamlConfigurationFilePath,
            directory.LegacyJsonConfigurationFilePath, original, WithTarget(original.Configuration, "mumu:9"), out _, files).IsFailure);
        Assert.Equal(before, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.Empty(Directory.GetFiles(directory.Root, ".config-*.yaml"));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.bak-*"));
    }

    [Fact(DisplayName = "Посторонний YAML-блок перед полным примером не меняет проверяемый документ")]
    public void EarlierYamlFenceDoesNotChangeCanonicalExample()
    {
        string document = File.ReadAllText(Path.Combine(PinnedVersions.RepositoryRoot,
            "docs", "reference", "application-configuration.md")).ReplaceLineEndings("\n");
        string yaml = ExtractCanonicalYaml("```yaml\nschemaVersion: 999\n```\n" + document);
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration(yaml);
        Assert.True(AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath).IsSuccess);
    }

    private static string ExtractCanonicalYaml(string documentation)
    {
        const string begin = "<!-- canonical-configuration:start -->";
        const string end = "<!-- canonical-configuration:end -->";
        string[] sections = Assert.Single(documentation.Split(begin, StringSplitOptions.None).Skip(1))
            .Split(end, StringSplitOptions.None);
        Assert.Equal(2, sections.Length);
        string section = sections[0];
        return section.Split("```yaml\n", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
    }

    [Fact(DisplayName = "Внешнее форматирование JSON не перезаписывается сохранением в YAML")]
    public void EquivalentLegacyJsonEditPreservesExternalBytes()
    {
        using TemporaryConfigurationDirectory directory = new();
        const string json = """{"schemaVersion":2,"mumu":{"instance":"auto"},"diagnostics":{"minimumLevel":"Information"}}""";
        _ = directory.WriteConfiguration(json);
        AzurPilotConfigurationSnapshot original = Load(directory);
        File.AppendAllText(directory.LegacyJsonConfigurationFilePath, "\n ");
        byte[] external = File.ReadAllBytes(directory.LegacyJsonConfigurationFilePath);
        Assert.Equal(original.Configuration, Load(directory).Configuration);
        Assert.True(Save(directory, original, original.Configuration).IsFailure);
        Assert.Equal(external, File.ReadAllBytes(directory.LegacyJsonConfigurationFilePath));
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
        Assert.Empty(directory.BackupFilePaths());
    }

    [Fact(DisplayName = "Смена JSON на равный по значениям YAML отклоняет устаревшее сохранение")]
    public void SourceFormatSwitchPreservesNewSource()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteConfiguration("""{"schemaVersion":2,"mumu":{"instance":"auto"},"diagnostics":{"minimumLevel":"Information"}}""");
        AzurPilotConfigurationSnapshot original = Load(directory);
        File.Delete(directory.LegacyJsonConfigurationFilePath);
        _ = directory.WriteYamlConfiguration(AzurPilotConfigurationYamlWriter.Write(original.Configuration));
        byte[] external = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        Assert.Equal(original.Configuration, Load(directory).Configuration);
        Assert.True(Save(directory, original, original.Configuration).IsFailure);
        Assert.Equal(external, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.False(File.Exists(directory.LegacyJsonConfigurationFilePath));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.bak-*"));
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
