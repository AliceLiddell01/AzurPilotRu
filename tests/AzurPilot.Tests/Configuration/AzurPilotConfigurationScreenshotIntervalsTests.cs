using System.Text;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>Точечная запись интервалов screenshot: сохранение текста файла, атомарность и отказы.</summary>
public sealed class AzurPilotConfigurationScreenshotIntervalsTests
{
    private const string Head = "schemaVersion: 3\n# пользовательский комментарий\nui:\n  mode: normal\nmumu:\n  instance: \"mumu:1\"\n"
        + "diagnostics:\n  minimumLevel: Information\n";
    private const string Logging = "logging:\n  viewer:\n    enabled: true # отдельное окно\n";

    [Theory(DisplayName = "Правка YAML меняет только значения интервалов и сохраняет остальной текст")]
    [InlineData(Head + Logging, 75, 300,
        Head + Logging + "screenshot:\n  normalIntervalMs: 75\n  combatIntervalMs: 300\n")]
    [InlineData(Head + Logging + "screenshot:\n  normalIntervalMs: 150 # было\n  combatIntervalMs: 1000\n", 75, 300,
        Head + Logging + "screenshot:\n  normalIntervalMs: 75 # было\n  combatIntervalMs: 300\n")]
    [InlineData(Head + "screenshot:\n  method: nemu_ipc # выбран\n  # конец секции\n" + Logging, 75, 300,
        Head + "screenshot:\n  method: nemu_ipc # выбран\n  normalIntervalMs: 75\n  combatIntervalMs: 300\n  # конец секции\n" + Logging)]
    [InlineData(Head + "screenshot: ~ # пусто\n" + Logging, 75, null,
        Head + "screenshot:  # пусто\n  normalIntervalMs: 75\n" + Logging)]
    [InlineData(Head + "screenshot:\n" + Logging, 50, 500,
        Head + "screenshot:\n  normalIntervalMs: 50\n  combatIntervalMs: 500\n" + Logging)]
    [InlineData(Head + Logging + "screenshot:\n  combatIntervalMs: 1000\n", 75, null,
        Head + Logging + "screenshot:\n  combatIntervalMs: 1000\n  normalIntervalMs: 75\n")]
    [InlineData("schemaVersion: 3\nui:\n    mode: normal\nmumu:\n    instance: auto\ndiagnostics:\n    minimumLevel: Information\nlogging:\n    viewer:\n        enabled: false", 75, 300,
        "schemaVersion: 3\nui:\n    mode: normal\nmumu:\n    instance: auto\ndiagnostics:\n    minimumLevel: Information\nlogging:\n    viewer:\n        enabled: false\nscreenshot:\n    normalIntervalMs: 75\n    combatIntervalMs: 300\n")]
    public void EditPreservesDocument(string original, int normal, int? combat, string expected)
    {
        foreach (string newLine in new[] { "\n", "\r\n" })
        {
            string edited = AzurPilotConfigurationYamlEdit.SetScreenshotIntervals(original.Replace("\n", newLine, StringComparison.Ordinal),
                normal, combat, out string? failure)!;
            Assert.Null(failure);
            Assert.Equal(expected.Replace("\n", newLine, StringComparison.Ordinal), edited);

            using TemporaryConfigurationDirectory directory = new();
            ApplicationResult<AzurPilotConfigurationSnapshot> loaded = AzurPilotConfigurationLoader.Load(
                directory.WriteYamlConfiguration(edited));
            Assert.True(loaded.IsSuccess, loaded.IsSuccess ? null : loaded.FailureInfo!.Message);
            Assert.Equal(normal, loaded.Value!.Configuration.Screenshot.NormalIntervalMilliseconds);
        }
    }

    [Fact(DisplayName = "Запись сохраняет BOM, комментарии и CRLF, создаёт backup и перечитывается")]
    public void SavePreservesFileAndCreatesBackup()
    {
        using TemporaryConfigurationDirectory directory = new();
        string text = (Head + Logging + "screenshot:\n  method: auto # выбор бенчмарка\n").Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
        _ = directory.WriteYamlConfigurationBytes(original);
        AzurPilotConfigurationSnapshot before = Load(directory).Value!;

        ApplicationResult<AzurPilotConfigurationSnapshot> saved = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath, before.SourceVersion, 75, 300,
            out string? backupPath);

        Assert.True(saved.IsSuccess, saved.IsSuccess ? null : saved.FailureInfo!.Message);
        // Единственное отличие от исходных байтов — две вставленные строки; BOM, CRLF и комментарии на месте.
        Assert.Equal(TextAfterInsert(original), File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.Equal(original, File.ReadAllBytes(backupPath!));
        AzurPilotConfigurationSnapshot reread = Load(directory).Value!;
        Assert.Equal(before.Configuration with
        {
            Screenshot = before.Configuration.Screenshot with { NormalIntervalMilliseconds = 75, CombatIntervalMilliseconds = 300 },
        }, reread.Configuration);
        Assert.Equal(saved.Value!.SourceVersion, reread.SourceVersion);
        Assert.DoesNotContain(directory.FileNames(), name => name.StartsWith(".config-", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Изменённый после проверки файл не перезаписывается")]
    public void ChangedSourceIsRejected()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration(Head + Logging);
        string version = Load(directory).Value!.SourceVersion!;
        string changed = Head + "# правка пользователя\n" + Logging;
        _ = directory.WriteYamlConfiguration(changed);

        ApplicationResult<AzurPilotConfigurationSnapshot> saved = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath, version, 75, 300, out string? backupPath);

        Assert.True(saved.IsFailure);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, saved.FailureInfo!.Code);
        Assert.Contains("изменилась после проверки", saved.FailureInfo.Message, StringComparison.Ordinal);
        Assert.Null(backupPath);
        Assert.Equal(changed, File.ReadAllText(directory.YamlConfigurationFilePath));
    }

    [Fact(DisplayName = "Ошибка замены файла оставляет исходные байты и не оставляет временных файлов")]
    public void ReplaceFailureKeepsOriginal()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration(Head + Logging);
        byte[] original = File.ReadAllBytes(directory.YamlConfigurationFilePath);
        ConfigurationFileOperations files = new()
        {
            Replace = static (_, _, _) => throw new IOException("диск недоступен"),
        };

        ApplicationResult<AzurPilotConfigurationSnapshot> saved = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath, Load(directory).Value!.SourceVersion,
            75, 300, out _, files);

        Assert.True(saved.IsFailure);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, saved.FailureInfo!.Code);
        Assert.Contains("диск недоступен", saved.FailureInfo!.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(directory.YamlConfigurationFilePath));
        Assert.Equal(["config.yaml"], directory.FileNames());
    }

    [Fact(DisplayName = "Без файла пишется канонический документ; legacy JSON требует миграции")]
    public void MissingAndLegacySources()
    {
        using TemporaryConfigurationDirectory missing = new();
        ApplicationResult<AzurPilotConfigurationSnapshot> created = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            missing.YamlConfigurationFilePath, missing.LegacyJsonConfigurationFilePath, null, 75, null, out _);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.FailureInfo!.Message);
        Assert.Equal(75, created.Value!.Configuration.Screenshot.NormalIntervalMilliseconds);
        Assert.Equal(AzurPilotConfigurationDefaults.Create().Screenshot.CombatIntervalMilliseconds,
            created.Value.Configuration.Screenshot.CombatIntervalMilliseconds);

        using TemporaryConfigurationDirectory legacy = new();
        _ = legacy.WriteConfiguration("""{"schemaVersion":2,"diagnostics":{"minimumLevel":"Warning"},"mumu":{"instance":"mumu:1"}}""");
        ApplicationResult<AzurPilotConfigurationSnapshot> snapshot = Load(legacy);
        Assert.True(snapshot.IsSuccess, snapshot.IsSuccess ? null : snapshot.FailureInfo!.Message);
        ApplicationResult<AzurPilotConfigurationSnapshot> rejected = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            legacy.YamlConfigurationFilePath, legacy.LegacyJsonConfigurationFilePath, snapshot.Value!.SourceVersion, 75, 300, out _);
        Assert.True(rejected.IsFailure);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, rejected.FailureInfo!.Code);
        Assert.Contains("legacy JSON", rejected.FailureInfo.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(legacy.YamlConfigurationFilePath));
    }

    private static byte[] TextAfterInsert(byte[] original)
    {
        string text = Encoding.UTF8.GetString(original);
        const string Anchor = "method: auto # выбор бенчмарка";
        return Encoding.UTF8.GetBytes(text.Replace(Anchor, Anchor + "\r\n  normalIntervalMs: 75\r\n  combatIntervalMs: 300",
            StringComparison.Ordinal));
    }

    private static ApplicationResult<AzurPilotConfigurationSnapshot> Load(TemporaryConfigurationDirectory directory)
        => AzurPilotConfigurationLoader.LoadFromSources(directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath);
}
