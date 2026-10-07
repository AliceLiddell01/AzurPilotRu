using System.Text;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает строгую семантику JSON-контракта: запрет unmapped members, duplicate properties, неверного
/// регистра, нарушения required/nullable contract и числовой формы уровня логирования. Документы этого
/// класса объявляют legacy-схему v1, поэтому проверки одновременно доказывают, что допуск v1 как
/// legacy-входа не ослабил строгость его собственного контракта.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationJsonContractTests
{
    [Theory(DisplayName = "Документ, не соответствующий строгой legacy-схеме v1, отклоняется как configuration_invalid")]
    [InlineData("синтаксически невалидный JSON", """{"schemaVersion":1,"diagnostics":""")]
    [InlineData("пустой документ", "")]
    [InlineData("документ из пробелов", "   ")]
    [InlineData("корневой JSON null", "null")]
    [InlineData("корневой JSON массив", "[]")]
    [InlineData("корневой JSON строка", "\"config\"")]
    [InlineData("пустой JSON-объект", "{}")]
    [InlineData(
        "неизвестное property в корне",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"unknown":true}""")]
    [InlineData(
        "неизвестное property внутри diagnostics",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information","extra":1}}""")]
    [InlineData(
        "секция MuMu",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"mumu":{}}""")]
    [InlineData(
        "секция ADB",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"adb":{"port":5037}}""")]
    [InlineData(
        "секция game",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"game":{"package":"x"}}""")]
    [InlineData(
        "секция vision",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"vision":{}}""")]
    [InlineData(
        "секция input",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"input":{}}""")]
    [InlineData(
        "секция OCR",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"ocr":{}}""")]
    [InlineData(
        "duplicate property в корне",
        """{"schemaVersion":1,"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData(
        "duplicate property внутри diagnostics",
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information","minimumLevel":"Warning"}}""")]
    [InlineData(
        "неверный регистр property schemaVersion",
        """{"SchemaVersion":1,"diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData(
        "неверный регистр property diagnostics",
        """{"schemaVersion":1,"Diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData(
        "неверный регистр property minimumLevel",
        """{"schemaVersion":1,"diagnostics":{"MinimumLevel":"Information"}}""")]
    [InlineData("отсутствует required schemaVersion", """{"diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData("отсутствует required diagnostics", """{"schemaVersion":1}""")]
    [InlineData("null в required diagnostics", """{"schemaVersion":1,"diagnostics":null}""")]
    [InlineData("null в required minimumLevel", """{"schemaVersion":1,"diagnostics":{"minimumLevel":null}}""")]
    [InlineData("неизвестное значение minimumLevel", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Verbose"}}""")]
    [InlineData("неверный регистр значения minimumLevel", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"information"}}""")]
    [InlineData("числовое значение minimumLevel", """{"schemaVersion":1,"diagnostics":{"minimumLevel":2}}""")]
    [InlineData("числовая строка вместо имени minimumLevel", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"2"}}""")]
    [InlineData("неверный тип schemaVersion", """{"schemaVersion":"1","diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData("дробный schemaVersion", """{"schemaVersion":1.5,"diagnostics":{"minimumLevel":"Information"}}""")]
    [InlineData("trailing comma", """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},}""")]
    [InlineData("комментарий", "{\"schemaVersion\":1,/* комментарий */\"diagnostics\":{\"minimumLevel\":\"Information\"}}")]
    public void StrictSchemaViolationIsRejectedAsConfigurationInvalid(string scenario, string document)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(document);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(result, ApplicationFailure.ConfigurationInvalid, scenario);
    }

    [Fact(DisplayName = "Неподдерживаемый schemaVersion отклоняется отдельным стабильным кодом")]
    public void UnsupportedSchemaVersionIsRejectedWithStableCode()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            """{"schemaVersion":3,"diagnostics":{"minimumLevel":"Information"}}""");

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationSchemaUnsupported,
            "schemaVersion 3");
        Assert.NotEqual(ApplicationFailure.ConfigurationInvalid, failure.Code);

        IReadOnlyDictionary<string, string> details = failure.Details!;
        Assert.Equal("3", details["schema_version"]);
        Assert.Equal(path, details["config_path"]);
    }

    [Fact(DisplayName = "Неподдерживаемый schemaVersion распознаётся даже при секциях будущей схемы")]
    public void UnsupportedSchemaVersionIsDetectedBeforeStrictValidation()
    {
        // Файл более новой схемы неизбежно содержит новые секции: оператор должен увидеть
        // «версия схемы не поддерживается», а не сообщение о неизвестном свойстве.
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(
            """{"schemaVersion":3,"diagnostics":{"minimumLevel":"Information"},"futureSection":{"enabled":true}}""");

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationSchemaUnsupported,
            "schemaVersion 3 с секцией будущей схемы");
    }

    [Fact(DisplayName = "Отсутствующий schemaVersion даёт configuration_invalid, а не неподдерживаемую версию")]
    public void MissingSchemaVersionIsNotReportedAsUnsupportedSchema()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration("""{"diagnostics":{"minimumLevel":"Information"}}""");

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "отсутствующий schemaVersion");
        Assert.NotEqual(ApplicationFailure.ConfigurationSchemaUnsupported, failure.Code);
    }

    [Fact(DisplayName = "Все имена LogLevel принимаются как minimumLevel: перечень принадлежит logging stack")]
    public void AllLogLevelNamesAreAccepted()
    {
        using TemporaryConfigurationDirectory directory = new();

        foreach (string name in Enum.GetNames<LogLevel>())
        {
            string path = directory.WriteConfiguration(
                $$$"""{"schemaVersion":1,"diagnostics":{"minimumLevel":"{{{name}}}"}}""");

            ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

            Assert.True(result.IsSuccess, $"Уровень «{name}» обязан приниматься, получено {result}.");
            Assert.Equal(Enum.Parse<LogLevel>(name), result.Value!.Configuration.Diagnostics.MinimumLevel);
        }
    }

    [Fact(DisplayName = "BOM не ослабляет строгость: невалидный документ с BOM всё равно отклоняется")]
    public void ByteOrderMarkDoesNotWeakenStrictness()
    {
        using TemporaryConfigurationDirectory directory = new();
        byte[] content =
        [
            .. Encoding.UTF8.GetPreamble(),
            .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Information"},"unknown":1}"""),
        ];
        string path = directory.WriteConfigurationBytes(content);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "BOM с неизвестным property");
    }
}
