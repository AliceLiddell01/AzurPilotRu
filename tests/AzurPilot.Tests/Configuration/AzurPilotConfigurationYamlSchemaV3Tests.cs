using System.Globalization;
using System.Text;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает строгий контракт канонического YAML эффективной схемы v3: полное чтение документа,
/// необязательные ключи со значениями от владельца defaults, отказ на anchor/alias, tags, flow-стиль,
/// merge-keys, multi-document, дубликаты, неизвестные и отсутствующие ключи, неверные типы и значения,
/// а также отдельный код для неподдерживаемой версии схемы.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationYamlSchemaV3Tests
{
    /// <summary>Валидный канонический документ схемы v3 со всеми обязательными ключами.</summary>
    private const string BaseDocument =
        """
        schemaVersion: 3
        ui:
          mode: normal
        mumu:
          instance: auto
        diagnostics:
          minimumLevel: Information
        logging:
          viewer:
            enabled: false
        """;

    [Fact(DisplayName = "Канонический YAML читается полностью: все поля, включая ui.mode и logging.viewer.enabled")]
    public void CanonicalDocumentIsReadCompletely()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(
            """
            schemaVersion: 3
            ui:
              mode: diagnostic
            mumu:
              instance: "mumu:1"
            diagnostics:
              minimumLevel: Warning
            logging:
              viewer:
                enabled: true
            """);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Канонический документ обязан читаться, получено {result}.");
        AzurPilotConfiguration configuration = result.Value!.Configuration;
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(OperatorUiMode.Diagnostic, configuration.Ui.Mode);
        Assert.Equal("mumu:1", configuration.MuMu.Instance);
        Assert.Equal(LogLevel.Warning, configuration.Diagnostics.MinimumLevel);
        Assert.True(configuration.Logging.Viewer.Enabled);
    }

    [Fact(DisplayName = "Документ со значениями defaults даёт ровно встроенную конфигурацию")]
    public void DocumentWithDefaultValuesEqualsBuiltInDefaults()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(BaseDocument);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Документ со значениями defaults обязан читаться, получено {result}.");
        Assert.Equal(AzurPilotConfigurationDefaults.Create(), result.Value!.Configuration);
    }

    [Theory(DisplayName = "Режим ui.mode принимается строго тремя строками в нижнем регистре")]
    [InlineData("normal", OperatorUiMode.Normal)]
    [InlineData("manual", OperatorUiMode.Manual)]
    [InlineData("diagnostic", OperatorUiMode.Diagnostic)]
    public void UiModeIsAcceptedStrictly(string mode, OperatorUiMode expected)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(WithMode(mode));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Режим «{mode}» обязан приниматься, получено {result}.");
        Assert.Equal(expected, result.Value!.Configuration.Ui.Mode);
    }

    [Theory(DisplayName = "Значение logging.viewer.enabled принимается только литералами true и false")]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ViewerEnabledIsAcceptedStrictly(string literal, bool expected)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(WithViewerEnabled(literal));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Значение «{literal}» обязано приниматься, получено {result}.");
        Assert.Equal(expected, result.Value!.Configuration.Logging.Viewer.Enabled);
    }

    [Theory(DisplayName = "Допустимое значение mumu.instance читается из канонического YAML")]
    [InlineData("auto")]
    [InlineData("mumu:0")]
    [InlineData("mumu:1")]
    [InlineData("mumu:1234567890")]
    public void ValidMuMuInstanceIsAccepted(string instance)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(WithInstance(instance));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Значение «{instance}» обязано приниматься, получено {result}.");
        Assert.Equal(instance, result.Value!.Configuration.MuMu.Instance);
    }

    [Theory(DisplayName = "Допустимое имя diagnostics.minimumLevel берётся у logging stack")]
    [InlineData("Trace")]
    [InlineData("Debug")]
    [InlineData("Information")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    [InlineData("None")]
    public void ValidMinimumLevelIsAccepted(string level)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(WithMinimumLevel(level));

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Уровень «{level}» обязан приниматься, получено {result}.");
        Assert.Equal(Enum.Parse<LogLevel>(level), result.Value!.Configuration.Diagnostics.MinimumLevel);
    }

    [Fact(DisplayName = "Необязательные ключи секций берут значения у владельца defaults")]
    public void OptionalKeysFallBackToBuiltInDefaults()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(
            """
            schemaVersion: 3
            ui:
              mode: manual
            mumu:
            diagnostics:
            logging:
              viewer:
                enabled: true
            """);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Пустые секции обязаны читаться, получено {result}.");
        AzurPilotConfiguration configuration = result.Value!.Configuration;
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();
        Assert.Equal(OperatorUiMode.Manual, configuration.Ui.Mode);
        Assert.Equal(defaults.MuMu.Instance, configuration.MuMu.Instance);
        Assert.Equal(defaults.Diagnostics.MinimumLevel, configuration.Diagnostics.MinimumLevel);
        Assert.True(configuration.Logging.Viewer.Enabled);
    }

    [Fact(DisplayName = "Строковые значения можно записывать в кавычках: смысл строки тот же")]
    public void QuotedStringValuesAreAccepted()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(
            """
            schemaVersion: 3
            ui:
              mode: "manual"
            mumu:
              instance: "mumu:7"
            diagnostics:
              minimumLevel: "Error"
            logging:
              viewer:
                enabled: false
            """);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Кавычки не меняют смысл строки, получено {result}.");
        Assert.Equal(OperatorUiMode.Manual, result.Value!.Configuration.Ui.Mode);
        Assert.Equal("mumu:7", result.Value!.Configuration.MuMu.Instance);
        Assert.Equal(LogLevel.Error, result.Value!.Configuration.Diagnostics.MinimumLevel);
    }

    [Fact(DisplayName = "UTF-8 BOM допускается в каноническом YAML: это кодировка файла")]
    public void ByteOrderMarkIsAccepted()
    {
        using TemporaryConfigurationDirectory directory = new();
        byte[] content =
        [
            .. Encoding.UTF8.GetPreamble(),
            .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(BaseDocument),
        ];
        string path = directory.WriteYamlConfigurationBytes(content);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"BOM относится к кодировке, а не к документу, получено {result}.");
        Assert.Equal(AzurPilotConfigurationDefaults.Create(), result.Value!.Configuration);
    }

    [Fact(DisplayName = "BOM не ослабляет строгость: невалидный YAML с BOM всё равно отклоняется")]
    public void ByteOrderMarkDoesNotWeakenStrictness()
    {
        using TemporaryConfigurationDirectory directory = new();
        byte[] content =
        [
            .. Encoding.UTF8.GetPreamble(),
            .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                "schemaVersion: 3\nui:\n  mode: normal\nunknown: true\n"),
        ];
        string path = directory.WriteYamlConfigurationBytes(content);

        _ = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationInvalid,
            "BOM с неизвестным ключом");
    }

    [Fact(DisplayName = "Файл в некорректной кодировке отклоняется, а не читается с подстановкой символов")]
    public void InvalidUtf8IsRejected()
    {
        using TemporaryConfigurationDirectory directory = new();
        byte[] content =
        [
            .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("schemaVersion: 3\n"),
            0xFF,
            0xFE,
        ];
        string path = directory.WriteYamlConfigurationBytes(content);

        _ = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationInvalid,
            "некорректная кодировка файла");
    }

    [Theory(DisplayName = "Нарушение строгой схемы v3 отклоняется как configuration_invalid")]
    [MemberData(nameof(StrictSchemaViolations))]
    public void StrictSchemaViolationIsRejectedAsConfigurationInvalid(string scenario, string document)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(document);

        _ = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationInvalid,
            scenario);
    }

    [Theory(DisplayName = "Неподдерживаемая версия схемы в YAML даёт отдельный код с details schema_version")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(42)]
    public void UnsupportedSchemaVersionIsRejectedWithStableCode(int schemaVersion)
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(WithSchemaVersion(schemaVersion));

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
        // «версия схемы не поддерживается», а не сообщение о неизвестном ключе.
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(
            """
            schemaVersion: 4
            ui:
              mode: normal
            mumu:
              instance: auto
              futureSetting: true
            diagnostics:
              minimumLevel: Information
            logging:
              viewer:
                enabled: false
            futureSection:
              enabled: true
            """);

        _ = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationSchemaUnsupported,
            "будущая схема с секциями будущих capability");
    }

    [Fact(DisplayName = "Отсутствующий schemaVersion даёт configuration_invalid, а не неподдерживаемую версию")]
    public void MissingSchemaVersionIsNotReportedAsUnsupportedSchema()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteYamlConfiguration(
            """
            ui:
              mode: normal
            mumu:
              instance: auto
            diagnostics:
              minimumLevel: Information
            logging:
              viewer:
                enabled: false
            """);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            AzurPilotConfigurationLoader.Load(path),
            ApplicationFailure.ConfigurationInvalid,
            "отсутствующий schemaVersion");
        Assert.NotEqual(ApplicationFailure.ConfigurationSchemaUnsupported, failure.Code);
    }

    /// <summary>Возвращает набор документов, каждый из которых нарушает строгую схему v3.</summary>
    /// <returns>Пары «описание сценария → текст документа».</returns>
    public static TheoryData<string, string> StrictSchemaViolations => new()
    {
        { "синтаксически невалидный YAML", "ui: [1\n" },
        { "незакрытый flow-стиль", "schemaVersion: 3\nui: {mode: normal\n" },
        { "табуляция как отступ", "schemaVersion: 3\nui:\n\tmode: normal\n" },
        { "пустой файл", string.Empty },
        { "файл из одних комментариев", "# только комментарий\n" },
        { "корень — скаляр", "just-a-string\n" },
        { "корень — последовательность", "- schemaVersion\n- ui\n" },
        { "явный маркер начала документа", "---\nschemaVersion: 3\n" },
        { "два документа в потоке", "schemaVersion: 3\n---\nschemaVersion: 3\n" },
        { "неизвестный ключ в корне", BaseDocument + "\nunknown: true" },
        { "неизвестный ключ в ui", Replace("ui:\n  mode: normal", "ui:\n  mode: normal\n  extra: 1") },
        { "неизвестный ключ в mumu", Replace("mumu:\n  instance: auto", "mumu:\n  instance: auto\n  extra: 1") },
        { "неизвестный ключ в diagnostics", Replace("diagnostics:\n  minimumLevel: Information", "diagnostics:\n  minimumLevel: Information\n  extra: 1") },
        { "неизвестный ключ в logging", Replace("logging:\n  viewer:", "logging:\n  extra: 1\n  viewer:") },
        { "неизвестный ключ в logging.viewer", Replace("    enabled: false", "    enabled: false\n    extra: 1") },
        { "повторяющийся ключ в корне", BaseDocument + "\nschemaVersion: 3" },
        { "повторяющийся ключ в ui", Replace("  mode: normal", "  mode: normal\n  mode: manual") },
        { "повторяющийся ключ в logging.viewer", Replace("    enabled: false", "    enabled: false\n    enabled: true") },
        { "anchor на скаляре", Replace("instance: auto", "instance: &anchor auto") },
        { "anchor на секции", Replace("ui:\n  mode: normal", "ui: &ui\n  mode: normal") },
        { "alias вместо значения", Replace("instance: auto", "instance: *ui") },
        { "tag на скаляре", Replace("instance: auto", "instance: !!str auto") },
        { "tag на секции", Replace("ui:\n  mode: normal", "ui: !!map\n  mode: normal") },
        { "flow-mapping вместо блочной секции", Replace("ui:\n  mode: normal", "ui: {mode: normal}") },
        { "flow-последовательность вместо секции", Replace("ui:\n  mode: normal", "ui: [normal]") },
        { "последовательность вместо секции mumu", Replace("mumu:\n  instance: auto", "mumu:\n  - auto") },
        { "merge-key в секции mumu", Replace("mumu:\n  instance: auto", "mumu:\n  <<: auto") },
        { "отсутствует schemaVersion", "ui:\n  mode: normal\nmumu:\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n" },
        { "отсутствует ui", "schemaVersion: 3\nmumu:\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n" },
        { "отсутствует ui.mode", Replace("ui:\n  mode: normal", "ui:") },
        { "отсутствует mumu", "schemaVersion: 3\nui:\n  mode: normal\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n" },
        { "отсутствует diagnostics", "schemaVersion: 3\nui:\n  mode: normal\nmumu:\nlogging:\n  viewer:\n    enabled: false\n" },
        { "отсутствует logging", "schemaVersion: 3\nui:\n  mode: normal\nmumu:\ndiagnostics:\n" },
        { "отсутствует logging.viewer", Replace("logging:\n  viewer:\n    enabled: false", "logging:") },
        { "отсутствует logging.viewer.enabled", Replace("  viewer:\n    enabled: false", "  viewer:") },
        { "ui не mapping", Replace("ui:\n  mode: normal", "ui: normal") },
        { "mumu не mapping", Replace("mumu:\n  instance: auto", "mumu: auto") },
        { "diagnostics не mapping", Replace("diagnostics:\n  minimumLevel: Information", "diagnostics: Information") },
        { "logging не mapping", Replace("logging:\n  viewer:\n    enabled: false", "logging: viewer") },
        { "logging.viewer не mapping", Replace("  viewer:\n    enabled: false", "  viewer: false") },
        { "ui.mode — секция", Replace("ui:\n  mode: normal", "ui:\n  mode:\n    nested: 1") },
        { "ui.mode — число", Replace("  mode: normal", "  mode: 1") },
        { "ui.mode — булево значение", Replace("  mode: normal", "  mode: true") },
        { "ui.mode — null", Replace("  mode: normal", "  mode:") },
        { "ui.mode — верхний регистр", Replace("  mode: normal", "  mode: Normal") },
        { "ui.mode — неизвестное значение", Replace("  mode: normal", "  mode: batch") },
        { "ui.mode — число строкой", Replace("  mode: normal", "  mode: \"1\"") },
        { "ui.mode — блочный скаляр", Replace("  mode: normal", "  mode: |\n    normal") },
        { "enabled — строка в кавычках", Replace("    enabled: false", "    enabled: \"true\"") },
        { "enabled — yes вместо true", Replace("    enabled: false", "    enabled: yes") },
        { "enabled — верхний регистр", Replace("    enabled: false", "    enabled: False") },
        { "enabled — число", Replace("    enabled: false", "    enabled: 1") },
        { "enabled — null", Replace("    enabled: false", "    enabled:") },
        { "schemaVersion — строка в кавычках", Replace("schemaVersion: 3", "schemaVersion: \"3\"") },
        { "schemaVersion — дробное число", Replace("schemaVersion: 3", "schemaVersion: 3.0") },
        { "schemaVersion — не число", Replace("schemaVersion: 3", "schemaVersion: three") },
        { "mumu.instance — число", Replace("  instance: auto", "  instance: 1") },
        { "mumu.instance — отрицательный номер", Replace("  instance: auto", "  instance: mumu:-1") },
        { "mumu.instance — ведущий нуль", Replace("  instance: auto", "  instance: mumu:01") },
        { "mumu.instance — верхний регистр литерала", Replace("  instance: auto", "  instance: Auto") },
        { "mumu.instance — пустой номер", Replace("  instance: auto", "  instance: \"mumu:\"") },
        { "mumu.instance — display name", Replace("  instance: auto", "  instance: MuMu Player 6.8.0") },
        { "mumu.instance — null", Replace("  instance: auto", "  instance:") },
        { "minimumLevel — число", Replace("  minimumLevel: Information", "  minimumLevel: 2") },
        { "minimumLevel — неверный регистр", Replace("  minimumLevel: Information", "  minimumLevel: information") },
        { "minimumLevel — неизвестное имя", Replace("  minimumLevel: Information", "  minimumLevel: Verbose") },
        { "minimumLevel — null", Replace("  minimumLevel: Information", "  minimumLevel:") },
    };

    /// <summary>Заменяет фрагмент базового документа схемы v3.</summary>
    /// <param name="from">Фрагмент, который заменяется.</param>
    /// <param name="to">Фрагмент замены.</param>
    /// <returns>Текст документа с заменённым фрагментом.</returns>
    private static string Replace(string from, string to)
    {
        Assert.Contains(from, BaseDocument, StringComparison.Ordinal);
        return BaseDocument.Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>Собирает документ с указанным значением <c>ui.mode</c>.</summary>
    /// <param name="mode">Строка режима интерфейса.</param>
    /// <returns>Текст документа схемы v3.</returns>
    private static string WithMode(string mode)
        => "schemaVersion: 3\nui:\n  mode: "
            + mode
            + "\nmumu:\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n";

    /// <summary>Собирает документ с указанным значением <c>logging.viewer.enabled</c>.</summary>
    /// <param name="literal">Литерал булева значения.</param>
    /// <returns>Текст документа схемы v3.</returns>
    private static string WithViewerEnabled(string literal)
        => "schemaVersion: 3\nui:\n  mode: normal\nmumu:\ndiagnostics:\nlogging:\n  viewer:\n    enabled: "
            + literal
            + "\n";

    /// <summary>Собирает документ с указанным значением <c>mumu.instance</c>.</summary>
    /// <param name="instance">Значение выбора экземпляра.</param>
    /// <returns>Текст документа схемы v3.</returns>
    private static string WithInstance(string instance)
        => "schemaVersion: 3\nui:\n  mode: normal\nmumu:\n  instance: "
            + instance
            + "\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n";

    /// <summary>Собирает документ с указанным значением <c>diagnostics.minimumLevel</c>.</summary>
    /// <param name="level">Имя уровня логирования.</param>
    /// <returns>Текст документа схемы v3.</returns>
    private static string WithMinimumLevel(string level)
        => "schemaVersion: 3\nui:\n  mode: normal\nmumu:\ndiagnostics:\n  minimumLevel: "
            + level
            + "\nlogging:\n  viewer:\n    enabled: false\n";

    /// <summary>Собирает документ с указанной версией схемы.</summary>
    /// <param name="schemaVersion">Номер версии схемы.</param>
    /// <returns>Текст документа схемы v3 с чужой версией.</returns>
    private static string WithSchemaVersion(int schemaVersion)
        => "schemaVersion: "
            + schemaVersion.ToString(CultureInfo.InvariantCulture)
            + "\nui:\n  mode: normal\nmumu:\ndiagnostics:\nlogging:\n  viewer:\n    enabled: false\n";
}
