using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Корневой контракт пользовательской конфигурации AzurPilot — эффективная схема v3.
/// </summary>
/// <remarks>
/// <para>
/// Канонический формат схемы — человекочитаемый YAML-файл <c>config.yaml</c>. Схема намеренно
/// минимальна: она содержит только то, что реально используют текущий хост приложения и реализованные
/// capability. Секции будущих capability (ADB, lifecycle игры, screenshot/vision, input, keymap,
/// разрешение экрана, OCR) и placeholder-настройки не заводятся заранее — секция появляется вместе с
/// реализацией capability.
/// </para>
/// <para>
/// Кроме эффективной схемы v3 загрузчик принимает legacy-документы JSON: схему v1
/// (<see cref="AzurPilotConfigurationV1"/>) и схему v2 (<see cref="AzurPilotConfigurationV2"/>). Оба
/// нормализуются к v3 только в памяти: файл на диске при загрузке не меняется. JSON-документ v3 не
/// существует, поэтому <c>schemaVersion: 3</c> в JSON-файле — неподдерживаемая схема.
/// </para>
/// <para>
/// Экземпляр неизменяем и является snapshot: он создаётся один раз при загрузке и не обновляется при
/// изменении файла на диске. Автоматического hot reload и file watcher нет.
/// </para>
/// <para>
/// Имена свойств JSON-контрактов заданы явно в camelCase и совпадают с ключами канонического YAML.
/// Строгий YAML-ридер отдельно объявляет принимаемые ключи, а writer записывает их литералами;
/// тесты схемы проверяют, что эти представления остаются согласованными.
/// </para>
/// </remarks>
public sealed record AzurPilotConfiguration
{
    /// <summary>Версия схемы конфигурации, которую понимает эта сборка.</summary>
    /// <remarks>
    /// Единственный владелец поддерживаемой версии схемы: загрузчик, встроенные значения по умолчанию,
    /// запись канонического YAML и тесты используют это значение, а не собственную копию числа. Версии
    /// legacy-схем принадлежат их контрактам — <see cref="AzurPilotConfigurationV1.LegacySchemaVersion"/>
    /// и <see cref="AzurPilotConfigurationV2.LegacySchemaVersion"/>.
    /// </remarks>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Версия схемы конфигурации, объявленная в документе.</summary>
    /// <value>
    /// Обязательный ключ <c>schemaVersion</c>. Документ без него невалиден, а значение, отличное от
    /// <see cref="CurrentSchemaVersion"/>, означает неподдерживаемую схему.
    /// </value>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Настройки операторского интерфейса.</summary>
    /// <value>Обязательная секция <c>ui</c>.</value>
    [JsonPropertyName("ui")]
    public required UiConfiguration Ui { get; init; }

    /// <summary>Настройки MuMu.</summary>
    /// <value>Обязательная секция <c>mumu</c>.</value>
    [JsonPropertyName("mumu")]
    public required MuMuConfiguration MuMu { get; init; }

    /// <summary>Настройки диагностики приложения.</summary>
    /// <value>Обязательная секция <c>diagnostics</c>.</value>
    [JsonPropertyName("diagnostics")]
    public required DiagnosticsConfiguration Diagnostics { get; init; }

    /// <summary>Настройки логирования.</summary>
    /// <value>Обязательная секция <c>logging</c>.</value>
    [JsonPropertyName("logging")]
    public required LoggingConfiguration Logging { get; init; }

    /// <summary>Настройки транспорта снимков экрана и интервалов наблюдения.</summary>
    /// <value>Необязательная секция <c>screenshot</c>; без неё действуют встроенные значения.</value>
    [JsonPropertyName("screenshot")]
    public required ScreenshotConfiguration Screenshot { get; init; }
}
