using System.Globalization;
using System.Reflection;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает правила legacy-входа схемы v1: валидный документ v1 допускается и нормализуется к схеме v2
/// только в памяти, строгая валидация v1 сохраняется, а migration/repair/update точки входа не появляются.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationLegacyV1Tests
{
    /// <summary>
    /// Версия legacy-схемы, которую объявляет документ v1. Владелец числа — legacy-контракт внутри Core;
    /// тест пишет документ так, как он лежит на диске у оператора.
    /// </summary>
    private const int LegacySchemaVersion = 1;

    [Fact(DisplayName = "Валидный документ v1 нормализуется к v2 в памяти, а файл на диске не меняется")]
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
        Assert.Equal(path, snapshot.FilePath);

        // Source schema — v1, effective schema — v2: это и есть нормализация в памяти.
        Assert.Equal(LegacySchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.True(snapshot.IsLegacySchemaNormalized);

        // Прочитанные значения сохранены, а новые обязательные значения пришли от владельца defaults.
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal(MuMuInstanceValue.AutoValue, snapshot.Configuration.MuMu.Instance);

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

        // Нормализация v1→v2 выполняется в памяти, поэтому единственная публичная операция загрузчика —
        // Load: команды migration/repair/update (и любая запись файла) не заводятся.
        Assert.NotEmpty(publicMethods);
        Assert.All(publicMethods, method => Assert.Equal("Load", method.Name));
    }

    [Theory(DisplayName = "Строгая валидация legacy-схемы v1 сохраняется")]
    [InlineData("секция mumu в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("секция mumu с допустимым значением в документе v1", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"mumu:1"}}""")]
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
