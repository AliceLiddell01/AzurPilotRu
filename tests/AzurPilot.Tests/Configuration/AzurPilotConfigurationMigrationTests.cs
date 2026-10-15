using System.Text;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает контролируемую миграцию legacy JSON в канонический YAML: резервная копия, атомарная
/// запись, подтверждение результата повторным чтением, отказ при существующем YAML, отказ без
/// повреждения данных и отсутствие автоматического запуска при загрузке.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationMigrationTests
{
    private const string LegacyV2Document =
        """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Warning"},"mumu":{"instance":"mumu:1"}}""";

    private const string LegacyV1Document =
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Critical"}}""";

    /// <summary>Канонический YAML, которым обязана закончиться миграция legacy-документа v2.</summary>
    private const string ExpectedYamlForLegacyV2 =
        """
        schemaVersion: 3
        ui:
          mode: normal
        mumu:
          instance: "mumu:1"
        diagnostics:
          minimumLevel: Warning
        logging:
          viewer:
            enabled: false

        """;

    [Fact(DisplayName = "Миграция пишет канонический YAML, создаёт резервную копию и подтверждает результат")]
    public void MigrationWritesCanonicalYamlAndBackup()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);
        byte[] documentBefore = File.ReadAllBytes(jsonPath);
        string yamlPath = directory.YamlConfigurationFilePath;

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(jsonPath, yamlPath);

        Assert.True(result.IsSuccess, $"Валидный legacy-документ обязан мигрировать, получено {result}.");
        AzurPilotConfigurationMigrationResult migration = result.Value!;

        // Записанный документ подтверждён повторным чтением загрузчиком.
        AzurPilotConfigurationSnapshot snapshot = migration.Snapshot;
        Assert.Equal(AzurPilotConfigurationFormat.Yaml, snapshot.SourceFormat);
        Assert.Equal(yamlPath, snapshot.FilePath);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.False(snapshot.IsLegacySchemaNormalized);
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal("mumu:1", snapshot.Configuration.MuMu.Instance);

        // Содержимое файла — ровно каноническая форма схемы v3.
        Assert.Equal(ExpectedYamlForLegacyV2, File.ReadAllText(yamlPath), StringComparer.Ordinal);

        // Резервная копия — это перемещённый legacy-файл: по исходному пути его больше нет, а содержимое
        // копии побайтово повторяет исходный документ.
        Assert.StartsWith(jsonPath + ".bak-", migration.BackupFilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(migration.BackupFilePath));
        Assert.Equal(documentBefore, File.ReadAllBytes(migration.BackupFilePath));
        Assert.False(File.Exists(jsonPath), "Legacy-файл перенесён в резервную копию, а не оставлен рядом с YAML.");
        Assert.Equal([migration.BackupFilePath], directory.BackupFilePaths());

        // После миграции действует ровно один источник: runtime-политика больше не отказывает.
        ApplicationResult<AzurPilotConfigurationSnapshot> runtime =
            AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        Assert.True(runtime.IsSuccess, $"После миграции загрузка обязана работать, получено {runtime}.");
        Assert.Equal(migration.Snapshot.Configuration, runtime.Value!.Configuration);

        // Временных файлов записи не осталось: атомарная запись переносит документ на место.
        Assert.DoesNotContain(
            directory.FileNames(),
            name => name.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Миграция legacy JSON v1 дополняет документ значениями defaults новых секций")]
    public void MigrationFromLegacyV1AddsDefaultSections()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV1Document);

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);

        Assert.True(result.IsSuccess, $"Валидный legacy-документ v1 обязан мигрировать, получено {result}.");

        AzurPilotConfiguration configuration = result.Value!.Snapshot.Configuration;
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(LogLevel.Critical, configuration.Diagnostics.MinimumLevel);
        Assert.Equal(defaults.Ui.Mode, configuration.Ui.Mode);
        Assert.Equal(defaults.MuMu.Instance, configuration.MuMu.Instance);
        Assert.Equal(defaults.Logging.Viewer.Enabled, configuration.Logging.Viewer.Enabled);
    }

    [Fact(DisplayName = "Сохранённый mumu:1 переиспользуется после повторной загрузки записанного YAML")]
    public void SavedInstanceIsReusedAfterReload()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);

        ApplicationResult<AzurPilotConfigurationMigrationResult> migration =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);
        Assert.True(migration.IsSuccess, migration.ToString());

        // Повторная загрузка записанного документа даёт то же значение выбора экземпляра.
        ApplicationResult<AzurPilotConfigurationSnapshot> reloaded =
            AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath);

        Assert.True(reloaded.IsSuccess, reloaded.ToString());
        Assert.Equal("mumu:1", reloaded.Value!.Configuration.MuMu.Instance);
        Assert.Equal(migration.Value!.Snapshot.Configuration, reloaded.Value!.Configuration);
    }

    [Fact(DisplayName = "Существующий config.yaml не перезаписывается: миграция отказывает и не трогает данные")]
    public void MigrationRefusesWhenCanonicalYamlExists()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);
        byte[] jsonBefore = File.ReadAllBytes(jsonPath);
        string yamlPath = directory.WriteYamlConfiguration(
            """
            schemaVersion: 3
            ui:
              mode: diagnostic
            mumu:
              instance: "mumu:9"
            diagnostics:
              minimumLevel: Error
            logging:
              viewer:
                enabled: true
            """);
        byte[] yamlBefore = File.ReadAllBytes(yamlPath);

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(jsonPath, yamlPath);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "канонический YAML уже существует");
        Assert.Equal(yamlPath, failure.Details!["config_path"]);

        // Ни действующий YAML, ни legacy-файл не изменены, и резервная копия не создавалась.
        Assert.Equal(yamlBefore, File.ReadAllBytes(yamlPath));
        Assert.Equal(jsonBefore, File.ReadAllBytes(jsonPath));
        Assert.Empty(directory.BackupFilePaths());
    }

    [Fact(DisplayName = "Повторная миграция не ломает данные: legacy-файла уже нет, YAML и копия те же")]
    public void RepeatedMigrationDoesNotBreakData()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);

        ApplicationResult<AzurPilotConfigurationMigrationResult> first =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);
        Assert.True(first.IsSuccess, first.ToString());
        byte[] yamlAfterFirst = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        byte[] backupAfterFirst = File.ReadAllBytes(first.Value!.BackupFilePath);

        ApplicationResult<AzurPilotConfigurationMigrationResult> second =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);

        // Повторный перенос сообщает, что переносить нечего, а не пытается что-то перезаписать.
        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            second,
            ApplicationFailure.ConfigurationInvalid,
            "повторная миграция после успешного переноса");
        Assert.Equal(jsonPath, failure.Details!["config_path"]);

        Assert.False(File.Exists(jsonPath));
        Assert.Equal(yamlAfterFirst, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        _ = Assert.Single(directory.BackupFilePaths());
        Assert.Equal(backupAfterFirst, File.ReadAllBytes(first.Value!.BackupFilePath));
    }

    [Fact(DisplayName = "Отсутствующий legacy JSON отклоняется: мигрировать нечего")]
    public void MigrationRefusesWhenLegacyJsonIsMissing()
    {
        using TemporaryConfigurationDirectory directory = new();

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                directory.MissingConfigurationFilePath,
                directory.YamlConfigurationFilePath);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "legacy JSON отсутствует");
        Assert.Equal(directory.MissingConfigurationFilePath, failure.Details!["config_path"]);
        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
    }

    [Fact(DisplayName = "Невалидный legacy JSON не мигрируется: отказ без записи YAML и без копии")]
    public void MigrationRefusesInvalidLegacyJson()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration("""{"schemaVersion":2,"diagnostics":""");
        byte[] jsonBefore = File.ReadAllBytes(jsonPath);

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "невалидный legacy JSON");

        Assert.False(File.Exists(directory.YamlConfigurationFilePath));
        Assert.Empty(directory.BackupFilePaths());
        Assert.Equal(jsonBefore, File.ReadAllBytes(jsonPath));
    }

    [Fact(DisplayName = "Источник миграции обязан быть legacy JSON, а не каноническим YAML")]
    public void MigrationRefusesNonLegacyJsonSource()
    {
        using TemporaryConfigurationDirectory directory = new();
        string sourcePath = directory.WriteYamlConfiguration(
            """
            schemaVersion: 3
            ui:
              mode: normal
            mumu:
            diagnostics:
            logging:
              viewer:
                enabled: false
            """);
        string targetPath = Path.Combine(directory.Root, "config.target.yaml");

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(sourcePath, targetPath);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "источник миграции — канонический YAML");
        Assert.False(File.Exists(targetPath));
        Assert.Empty(directory.BackupFilePaths());
    }

    [Fact(DisplayName = "Миграция создаёт каталог канонического файла при необходимости")]
    public void MigrationCreatesTargetDirectory()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);
        string targetPath = Path.Combine(directory.Root, "nested", "config.yaml");

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(jsonPath, targetPath);

        Assert.True(result.IsSuccess, $"Каталог канонического файла создаётся при необходимости, получено {result}.");
        Assert.True(File.Exists(targetPath));
        Assert.Equal(targetPath, result.Value!.Snapshot.FilePath);
    }

    [Fact(DisplayName = "Канонический YAML записывается в UTF-8 без BOM")]
    public void MigrationWritesUtf8WithoutByteOrderMark()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);

        ApplicationResult<AzurPilotConfigurationMigrationResult> result =
            AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                jsonPath,
                directory.YamlConfigurationFilePath);
        Assert.True(result.IsSuccess, result.ToString());

        byte[] written = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        byte[] preamble = Encoding.UTF8.GetPreamble();
        Assert.False(
            written.AsSpan().StartsWith(preamble),
            "Канонический YAML записывается без ведущего BOM.");
    }

    [Fact(DisplayName = "Пустой и относительный путь миграции — ошибка программирования")]
    public void NonAbsolutePathsAreProgrammingErrors()
    {
        using TemporaryConfigurationDirectory directory = new();

        _ = Assert.Throws<ArgumentNullException>(
            () => AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(null!, directory.YamlConfigurationFilePath));
        _ = Assert.Throws<ArgumentException>(
            () => AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml("  ", directory.YamlConfigurationFilePath));
        _ = Assert.Throws<ArgumentException>(
            () => AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(
                directory.LegacyJsonConfigurationFilePath,
                Path.Combine("config", "config.yaml")));
    }

    [Fact(DisplayName = "Загрузка конфигурации сама ничего не мигрирует: YAML не появляется")]
    public void LoadingDoesNotMigrateAnything()
    {
        using TemporaryConfigurationDirectory directory = new();
        string jsonPath = directory.WriteConfiguration(LegacyV2Document);

        ApplicationResult<AzurPilotConfigurationSnapshot> loaded =
            AzurPilotConfigurationLoader.LoadFromSources(
                directory.YamlConfigurationFilePath,
                jsonPath);

        Assert.True(loaded.IsSuccess, loaded.ToString());
        Assert.False(
            File.Exists(directory.YamlConfigurationFilePath),
            "Автоматической миграции при загрузке нет: YAML создаёт только явная операция.");
        Assert.Empty(directory.BackupFilePaths());
    }
}
