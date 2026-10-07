using System.Globalization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает контракт эффективной схемы v2: владелец номера версии, состав секции <c>mumu</c>, строгое
/// чтение документа v2, различие source/effective schema в snapshot, приоритет неподдерживаемой версии и
/// закрытие edge case с повторяющимся <c>schemaVersion</c>.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationSchemaV2Tests
{
    [Fact(DisplayName = "Эффективная схема — v2, а встроенные defaults описывают валидный документ v2")]
    public void CurrentSchemaVersionIsTwoAndDefaultsAreValidV2()
    {
        // Владелец номера — сам тип конфигурации: требуемое значение проверяется ровно здесь.
        Assert.Equal(2, AzurPilotConfiguration.CurrentSchemaVersion);

        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, defaults.SchemaVersion);
        Assert.Equal(LogLevel.Information, defaults.Diagnostics.MinimumLevel);
        Assert.Equal(MuMuInstanceValue.AutoValue, defaults.MuMu.Instance);
        Assert.True(
            MuMuInstanceValue.IsValid(defaults.MuMu.Instance),
            "Встроенное значение mumu.instance обязано быть частью схемы v2.");

        // Defaults описывают ту же схему, что и файл: документ, собранный из их значений, читается
        // полностью и даёт ровно ту же конфигурацию.
        using TemporaryConfigurationDirectory directory = new();
        string document =
            $$$"""
            {
              "schemaVersion": {{{defaults.SchemaVersion.ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "{{{defaults.Diagnostics.MinimumLevel}}}"
              },
              "mumu": {
                "instance": "{{{defaults.MuMu.Instance}}}"
              }
            }
            """;
        string path = directory.WriteConfiguration(document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Документ из значений defaults обязан читаться, получено {result}.");
        Assert.Equal(defaults, result.Value!.Configuration);
    }

    [Fact(DisplayName = "Валидный документ v2 читается полностью, а snapshot несёт обе версии схемы")]
    public void ValidV2DocumentIsReadAndSnapshotCarriesBothSchemaVersions()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(CurrentSchemaDocument(mumuInstance: "mumu:2", minimumLevel: "Warning"));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Валидный документ v2 обязан читаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.File, snapshot.Source);
        Assert.False(snapshot.IsBuiltInDefaults);
        Assert.Equal(path, snapshot.FilePath);

        // Значения прочитаны полностью, а не заменены defaults.
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal("mumu:2", snapshot.Configuration.MuMu.Instance);

        // Source schema и effective schema различимы без дампа конфигурации.
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.False(snapshot.IsLegacySchemaNormalized);
    }

    [Fact(DisplayName = "Snapshot встроенных defaults несёт effective схему как source schema")]
    public void BuiltInDefaultsSnapshotCarriesEffectiveSchemaAsSourceSchema()
    {
        using TemporaryConfigurationDirectory directory = new();

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);

        Assert.True(result.IsSuccess, $"Отсутствие файла — валидный сценарий, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.BuiltInDefaults, snapshot.Source);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(snapshot.SourceSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.False(snapshot.IsLegacySchemaNormalized);
    }

    [Theory(DisplayName = "Допустимое значение mumu.instance читается из документа v2")]
    [InlineData("auto")]
    [InlineData("mumu:0")]
    [InlineData("mumu:1")]
    [InlineData("mumu:12")]
    [InlineData("mumu:1234567890")]
    public void ValidMuMuInstanceValueIsAccepted(string instance)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(CurrentSchemaDocument(instance));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Значение «{instance}» обязано приниматься, получено {result}.");
        Assert.Equal(instance, result.Value!.Configuration.MuMu.Instance);
    }

    [Theory(DisplayName = "Значение mumu.instance вне синтаксиса схемы v2 даёт configuration_invalid")]
    [InlineData("пустая строка", "")]
    [InlineData("пробел", " ")]
    [InlineData("неверный регистр литерала auto", "Auto")]
    [InlineData("верхний регистр литерала auto", "AUTO")]
    [InlineData("пробел после литерала auto", "auto ")]
    [InlineData("просто номер", "1")]
    [InlineData("номер без префикса", "0")]
    [InlineData("пустой номер", "mumu:")]
    [InlineData("ведущий нуль", "mumu:01")]
    [InlineData("два нуля", "mumu:00")]
    [InlineData("отрицательный номер", "mumu:-1")]
    [InlineData("знак плюс", "mumu:+1")]
    [InlineData("дробный номер", "mumu:1.0")]
    [InlineData("номер с буквой", "mumu:1a")]
    [InlineData("пробел в номере", "mumu: 1")]
    [InlineData("display name", "MuMu Player 6.8.0")]
    public void MuMuInstanceValueOutsideSchemaIsRejected(string scenario, string instance)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(CurrentSchemaDocument(instance));

        _ = ConfigurationTestAssertions.AssertFailure(
            result: AzurPilotConfigurationLoader.Load(path),
            expectedCode: ApplicationFailure.ConfigurationInvalid,
            scenario: $"mumu.instance «{scenario}»");
    }

    [Theory(DisplayName = "Документ v2, нарушающий строгую схему, отклоняется как configuration_invalid")]
    [InlineData("нестроковый mumu.instance", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":1}}""")]
    [InlineData("null mumu.instance", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":null}}""")]
    [InlineData("отсутствует required mumu.instance", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{}}""")]
    [InlineData("null mumu", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":null}""")]
    [InlineData("mumu не объект, а строка", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":"auto"}""")]
    [InlineData("mumu не объект, а массив", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":["auto"]}""")]
    [InlineData("отсутствует required mumu", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData("неизвестное property внутри mumu", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto","extra":1}}""")]
    [InlineData("install-path override внутри mumu", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto","installPath":"any"}}""")]
    [InlineData("секция ADB", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"adb":{"port":5037}}""")]
    [InlineData("секция game", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"game":{"package":"x"}}""")]
    [InlineData("секция vision", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"vision":{}}""")]
    [InlineData("неизвестное property в корне", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"unknown":true}""")]
    [InlineData("неверный регистр property mumu", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"MuMu":{"instance":"auto"}}""")]
    [InlineData("неверный регистр property instance", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"Instance":"auto"}}""")]
    [InlineData("duplicate property instance", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto","instance":"mumu:1"}}""")]
    [InlineData("отсутствует required diagnostics", """{"schemaVersion":2,"mumu":{"instance":"auto"}}""")]
    [InlineData("null в required diagnostics", """{"schemaVersion":2,"diagnostics":null,"mumu":{"instance":"auto"}}""")]
    [InlineData("отсутствует required minimumLevel", """{"schemaVersion":2,"diagnostics":{},"mumu":{"instance":"auto"}}""")]
    [InlineData("неверный регистр значения minimumLevel", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("числовая форма minimumLevel", """{"schemaVersion":2,"diagnostics":{"minimumLevel":2},"mumu":{"instance":"auto"}}""")]
    [InlineData("trailing comma", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},}""")]
    [InlineData("комментарий", "{\"schemaVersion\":2,/* комментарий */\"diagnostics\":{\"minimumLevel\":\"Information\"},\"mumu\":{\"instance\":\"auto\"}}")]
    [InlineData("документ без объекта в корне", """[{"schemaVersion":2,"mumu":{"instance":"auto"}}]""")]
    public void StrictV2ViolationIsRejectedAsConfigurationInvalid(string scenario, string document)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(document);

        _ = ConfigurationTestAssertions.AssertFailure(
            result: AzurPilotConfigurationLoader.Load(path),
            expectedCode: ApplicationFailure.ConfigurationInvalid,
            scenario: scenario);
    }

    [Theory(DisplayName = "Неподдерживаемая версия схемы даёт отдельный стабильный код с details schema_version")]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(42)]
    public void UnsupportedSchemaVersionIsRejectedWithStableCode(int schemaVersion)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(DocumentWithSchemaVersion(schemaVersion));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationSchemaUnsupported,
            $"schemaVersion {schemaVersion}");
        Assert.NotEqual(ApplicationFailure.ConfigurationInvalid, failure.Code);

        IReadOnlyDictionary<string, string> details = failure.Details!;
        Assert.Equal(
            schemaVersion.ToString(CultureInfo.InvariantCulture),
            details["schema_version"]);
        Assert.Equal(path, details["config_path"]);
    }

    [Fact(DisplayName = "Неподдерживаемая версия распознаётся даже при секциях будущей схемы")]
    public void UnsupportedSchemaVersionIsDetectedBeforeStrictValidation()
    {
        // Документ более новой схемы неизбежно содержит новые секции: оператор должен увидеть
        // «версия схемы не поддерживается», а не сообщение о неизвестном свойстве.
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            $$$"""
            {
              "schemaVersion": {{{(AzurPilotConfiguration.CurrentSchemaVersion + 1).ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "Information"
              },
              "mumu": {
                "instance": "auto",
                "futureSetting": true
              },
              "futureSection": {
                "enabled": true
              }
            }
            """);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationSchemaUnsupported,
            "будущая схема с секциями будущих capability");
    }

    [Theory(DisplayName = "Повторяющийся schemaVersion даёт configuration_invalid независимо от порядка")]
    [InlineData("1 и 2", """{"schemaVersion":1,"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("2 и 1", """{"schemaVersion":2,"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("2 и 2", """{"schemaVersion":2,"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("2 и 3", """{"schemaVersion":2,"schemaVersion":3,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("3 и 2", """{"schemaVersion":3,"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"}}""")]
    [InlineData("1 и 1 в legacy-документе", """{"schemaVersion":1,"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"}}""")]
    public void DuplicateSchemaVersionIsRejectedAsConfigurationInvalid(string scenario, string document)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        // Одна из повторяющихся версий может быть неподдерживаемой, но документ с повторяющимся
        // свойством невалиден сам по себе: выбор контракта не зависит от того, какое значение увидел
        // предварительный разбор.
        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            $"duplicate schemaVersion ({scenario})");
        Assert.NotEqual(ApplicationFailure.ConfigurationSchemaUnsupported, failure.Code);
        Assert.Equal(path, failure.Details!["config_path"]);
    }

    [Fact(DisplayName = "Повторяющееся свойство секции в корне документа тоже даёт configuration_invalid")]
    public void DuplicateSectionPropertyIsRejectedAsConfigurationInvalid()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"mumu":{"instance":"mumu:1"}}""");

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "duplicate property mumu в корне");
        Assert.NotEqual(ApplicationFailure.ConfigurationSchemaUnsupported, failure.Code);
    }

    /// <summary>Собирает документ эффективной схемы v2 с указанным значением <c>mumu.instance</c>.</summary>
    private static string CurrentSchemaDocument(string mumuInstance, string minimumLevel = "Information")
        => CurrentSchemaDocumentWithRawInstance("\"" + mumuInstance + "\"", minimumLevel);

    /// <summary>Собирает документ схемы v2 с значением <c>mumu.instance</c>, записанным как есть.</summary>
    private static string CurrentSchemaDocumentWithRawInstance(string rawInstanceJson, string minimumLevel)
        => $$$"""
            {
              "schemaVersion": {{{AzurPilotConfiguration.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "{{{minimumLevel}}}"
              },
              "mumu": {
                "instance": {{{rawInstanceJson}}}
              }
            }
            """;

    /// <summary>Собирает документ с указанной версией схемы и секциями эффективной схемы v2.</summary>
    private static string DocumentWithSchemaVersion(int schemaVersion)
        => $$$"""
            {
              "schemaVersion": {{{schemaVersion.ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "Information"
              },
              "mumu": {
                "instance": "auto"
              }
            }
            """;
}
