using System.Globalization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает правила legacy-входа схемы v2: документ с <c>schemaVersion = 2</c> читается строгим
/// контрактом своей версии и нормализуется к эффективной схеме v3 только в памяти, строгая валидация v2
/// сохраняется, а неподдерживаемый номер версии получает отдельный стабильный код.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationSchemaV2Tests
{
    /// <summary>Версия legacy-схемы v2: владелец числа — legacy-контракт внутри Core.</summary>
    private const int LegacySchemaVersion = 2;

    [Fact(DisplayName = "Эффективная схема — v3, а встроенные defaults описывают валидный документ v3")]
    public void CurrentSchemaVersionIsThreeAndDefaultsAreValidV3()
    {
        // Владелец номера — сам тип конфигурации: требуемое значение проверяется ровно здесь.
        Assert.Equal(3, AzurPilotConfiguration.CurrentSchemaVersion);

        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, defaults.SchemaVersion);
        Assert.Equal(OperatorUiMode.Normal, defaults.Ui.Mode);
        Assert.Equal(LogLevel.Information, defaults.Diagnostics.MinimumLevel);
        Assert.Equal(MuMuInstanceValue.AutoValue, defaults.MuMu.Instance);
        Assert.False(defaults.Logging.Viewer.Enabled);
        Assert.True(
            MuMuInstanceValue.IsValid(defaults.MuMu.Instance),
            "Встроенное значение mumu.instance обязано быть частью схемы v3.");

        // Defaults описывают ту же схему, что и канонический файл: документ, собранный из их значений,
        // читается полностью и даёт ровно ту же конфигурацию.
        using TemporaryConfigurationDirectory directory = new();
        string document =
            $"""
            schemaVersion: {defaults.SchemaVersion}
            ui:
              mode: normal
            mumu:
              instance: {defaults.MuMu.Instance}
            diagnostics:
              minimumLevel: {defaults.Diagnostics.MinimumLevel}
            logging:
              viewer:
                enabled: false
            """;
        string path = directory.WriteYamlConfiguration(document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Документ из значений defaults обязан читаться, получено {result}.");
        Assert.Equal(defaults, result.Value!.Configuration);
    }

    [Fact(DisplayName = "Валидный legacy-документ v2 читается и нормализуется к v3 в памяти")]
    public void ValidV2DocumentIsReadAsLegacyInput()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            CurrentSchemaDocument(mumuInstance: "mumu:2", minimumLevel: "Warning"));
        byte[] documentBefore = File.ReadAllBytes(path);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Валидный legacy-документ v2 обязан читаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.File, snapshot.Source);
        Assert.Equal(AzurPilotConfigurationFormat.Json, snapshot.SourceFormat);
        Assert.False(snapshot.IsBuiltInDefaults);
        Assert.Equal(path, snapshot.FilePath);

        // Значения прочитаны полностью, а не заменены defaults.
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal("mumu:2", snapshot.Configuration.MuMu.Instance);

        // Source schema — v2, effective schema — v3: это и есть нормализация в памяти.
        Assert.Equal(LegacySchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.True(snapshot.IsLegacySchemaNormalized);

        // Секции, которых в схеме v2 не было, приходят от владельца defaults.
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(defaults.Ui.Mode, snapshot.Configuration.Ui.Mode);
        Assert.Equal(defaults.Logging.Viewer.Enabled, snapshot.Configuration.Logging.Viewer.Enabled);

        // Нормализация не касается диска: файл побитово тот же.
        Assert.Equal(documentBefore, File.ReadAllBytes(path));
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
        Assert.Equal(AzurPilotConfigurationFormat.None, snapshot.SourceFormat);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.Equal(snapshot.SourceSchemaVersion, snapshot.EffectiveSchemaVersion);
        Assert.False(snapshot.IsLegacySchemaNormalized);
    }

    [Theory(DisplayName = "Допустимое значение mumu.instance читается из legacy-документа v2")]
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

    [Theory(DisplayName = "Значение mumu.instance вне синтаксиса схемы даёт configuration_invalid")]
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

    [Theory(DisplayName = "Legacy-документ v2, нарушающий строгую схему, отклоняется как configuration_invalid")]
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
    [InlineData("секция ui", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"ui":{"mode":"normal"}}""")]
    [InlineData("секция logging", """{"schemaVersion":2,"diagnostics":{"minimumLevel":"Information"},"mumu":{"instance":"auto"},"logging":{"viewer":{"enabled":false}}}""")]
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

    [Theory(DisplayName = "Неподдерживаемая версия схемы в JSON даёт отдельный код с details schema_version")]
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

    [Fact(DisplayName = "JSON-документ схемы v3 не существует: текущая версия в JSON неподдерживаема")]
    public void CurrentSchemaVersionInJsonIsUnsupported()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            """{"schemaVersion":3,"ui":{"mode":"normal"},"mumu":{},"diagnostics":{},"logging":{"viewer":{"enabled":false}}}""");

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationSchemaUnsupported,
            "текущая версия схемы в JSON-файле");
        Assert.Equal("3", failure.Details!["schema_version"]);
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

    /// <summary>Собирает legacy-документ схемы v2 с указанным значением <c>mumu.instance</c>.</summary>
    /// <param name="mumuInstance">Значение выбора экземпляра.</param>
    /// <param name="minimumLevel">Имя уровня логирования.</param>
    /// <returns>Текст legacy-документа v2.</returns>
    private static string CurrentSchemaDocument(string mumuInstance, string minimumLevel = "Information")
        => $$$"""
            {
              "schemaVersion": {{{LegacySchemaVersion.ToString(CultureInfo.InvariantCulture)}}},
              "diagnostics": {
                "minimumLevel": "{{{minimumLevel}}}"
              },
              "mumu": {
                "instance": "{{{mumuInstance}}}"
              }
            }
            """;

    /// <summary>Собирает документ с указанной версией схемы и секциями legacy-схемы v2.</summary>
    /// <param name="schemaVersion">Номер версии схемы.</param>
    /// <returns>Текст документа с указанной версией.</returns>
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
