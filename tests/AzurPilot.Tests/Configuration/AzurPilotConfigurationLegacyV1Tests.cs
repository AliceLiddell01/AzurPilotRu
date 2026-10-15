using System.Globalization;
using System.Reflection;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает правила legacy-входа схемы v1: валидный документ v1 допускается и нормализуется к
/// эффективной схеме v3 только в памяти, строгая валидация v1 сохраняется, а migration/repair/update
/// точки входа у загрузчика не появляются.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationLegacyV1Tests
{
    /// <summary>
    /// Версия legacy-схемы, которую объявляет документ v1. Владелец числа — legacy-контракт внутри Core;
    /// тест пишет документ так, как он лежит на диске у оператора.
    /// </summary>
    private const int LegacySchemaVersion = 1;

    /// <summary>Публичная поверхность загрузчика: только чтение, без migration/repair/update.</summary>
    private static readonly string[] LoaderPublicMethodNames = ["Load", "LoadRuntime", "LoadFromSources"];

    [Fact(DisplayName = "Валидный документ v1 нормализуется к v3 в памяти, а файл на диске не меняется")]
    public void ValidLegacyDocumentIsNormalizedInMemoryOnly()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(LegacySchemaDocument("Warning"));
        string directoryPath = Path.GetDirectoryName(path)!;
        byte[] documentBefore = File.ReadAllBytes(path);
        string[] entriesBefore = Directory.GetFileSystemEntries(directoryPath);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Валидный legacy-документ v1 обязан читаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.File, snapshot.Source);
        Assert.Equal(AzurPilotConfigurationFormat.Json, snapshot.SourceFormat);
        Assert.Equal(path, snapshot.FilePath);

        // Source schema — v1, effective schema — v3: это и есть нормализация в памяти.
        Assert.Equal(LegacySchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.True(snapshot.IsLegacySchemaNormalized);

        // Прочитанные значения сохранены, а новые обязательные значения пришли от владельца defaults.
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal(MuMuInstanceValue.AutoValue, snapshot.Configuration.MuMu.Instance);
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(defaults.Ui.Mode, snapshot.Configuration.Ui.Mode);
        Assert.Equal(defaults.Logging.Viewer.Enabled, snapshot.Configuration.Logging.Viewer.Enabled);

        // Нормализация не касается диска: файл побитово тот же, новых файлов не появилось.
        Assert.Equal(documentBefore, File.ReadAllBytes(path));
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(directoryPath));
        Assert.Contains(
            $"\"schemaVersion\": {LegacySchemaVersion.ToString(CultureInfo.InvariantCulture)}",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Повторная загрузка legacy-документа не выполняет repair: результат и файл те же")]
    public void RepeatedLegacyLoadDoesNotRepairTheFile()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(LegacySchemaDocument("Error"));
        byte[] documentBefore = File.ReadAllBytes(path);

        AzurPilotConfigurationSnapshot first = AzurPilotConfigurationLoader.Load(path).Value!;
        AzurPilotConfigurationSnapshot second = AzurPilotConfigurationLoader.Load(path).Value!;

        Assert.Equal(first.Configuration, second.Configuration);
        Assert.Equal(LegacySchemaVersion, second.SourceSchemaVersion);
        Assert.Equal(documentBefore, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Загрузчик не предоставляет migration/repair/update точку входа")]
    public void LoaderExposesNoMigrationOrRepairEntryPoint()
    {
        MethodInfo[] publicMethods = typeof(AzurPilotConfigurationLoader)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        string[] publicNames = [.. publicMethods.Select(method => method.Name)];

        // Публичная поверхность загрузчика — только чтение: Load (явный файл), LoadRuntime (оба
        // runtime-пути) и LoadFromSources (та же политика с явными путями). Ни migration, ни repair, ни
        // запись файла загрузчику не принадлежат.
        Assert.NotEmpty(publicNames);
        Assert.All(
            publicNames,
            name => Assert.Contains(name, LoaderPublicMethodNames));

        // Единственный владелец миграции — отдельный тип, и его операция не вызывается загрузкой.
        MethodInfo[] migrationMethods = typeof(AzurPilotConfigurationMigration)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(migrationMethods);
        Assert.All(
            migrationMethods,
            method => Assert.StartsWith("Migrate", method.Name, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Строгая валидация legacy-схемы v1 сохраняется")]
    [InlineData("секция mumu в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("секция mumu с допустимым значением в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"mumu:1"}}""")]
    [InlineData("секция ui в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"ui":{"mode":"normal"}}""")]
    [InlineData("секция logging в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"logging":{"viewer":{"enabled":false}}}""")]
    [InlineData("секция diagnostics в документе v1 с неизвестным свойством", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information","extra":1}}""")]
    [InlineData("неизвестное property в корне документа v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"unknown":true}""")]
    [InlineData("отсутствует required diagnostics в документе v1", """{"schemaVersion":1}""")]
    [InlineData("null в required diagnostics документа v1", """{"schemaVersion":1,"diagnostics":null}""")]
    [InlineData("отсутствует required minimumLevel в документе v1", """{"schemaVersion":1,"diagnostics":{}}""")]
    [InlineData("duplicate property minimumLevel в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information","minimumLevel":"Warning"}}""")]
    [InlineData("неверный регистр property diagnostics в документе v1", """{"schemaVersion":1,"Diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData("неверный регистр property mumu в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"MuMu":{"instance":"auto"}}""")]
    [InlineData("числовая форма minimumLevel в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":2}}""")]
    [InlineData("неизвестное значение minimumLevel в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Verbose"}}""")]
    public void LegacyStrictnessIsPreserved(string scenario, string document)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            scenario);
        Assert.NotEqual(ApplicationFailure.ConfigurationSchemaUnsupported, failure.Code);
        Assert.Equal(path, failure.Details!["config_path"]);
    }

    [Fact(DisplayName = "Документ v1 без секции mumu читается: секция обязательна только для v2")]
    public void LegacyDocumentWithoutMuMuSectionIsAccepted()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(LegacySchemaDocument("Critical"));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Документ v1 без секции mumu — валидный legacy-вход, получено {result}.");
        Assert.Equal(LogLevel.Critical, result.Value!.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal(LegacySchemaVersion, result.Value!.SourceSchemaVersion);
    }

    /// <summary>Собирает legacy-документ схемы v1: только версия и секция диагностики.</summary>
    /// <param name="minimumLevel">Имя уровня логирования.</param>
    /// <returns>Текст legacy-документа v1.</returns>
    private static string LegacySchemaDocument(string minimumLevel)
        => $$$"""
            {
              "schemaVersion": {{{LegacySchemaVersion.ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "{{{minimumLevel}}}"
              }
            }
            """;
}
