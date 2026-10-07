using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Корневой контракт пользовательской конфигурации AzurPilot — эффективная схема v2.
/// </summary>
/// <remarks>
/// <para>
/// Схема намеренно минимальна: она содержит только то, что реально используют текущий application host
/// и реализованные capability. Секции будущих capability (ADB, lifecycle игры, screenshot/vision,
/// input, keymap, разрешение экрана, OCR) и placeholder-настройки не заводятся заранее — секция
/// появляется вместе с реализацией capability.
/// </para>
/// <para>
/// Кроме эффективной схемы v2 загрузчик принимает legacy-документ v1
/// (<see cref="AzurPilotConfigurationV1"/>) и нормализует его к v2 в памяти: на диске при этом ничего
/// не меняется.
/// </para>
/// <para>
/// Экземпляр неизменяем и является snapshot: он создаётся один раз при загрузке и не обновляется при
/// изменении файла на диске. Автоматического hot reload и file watcher нет.
/// </para>
/// <para>
/// Имена JSON-свойств заданы явно и в camelCase: строгая десериализация сравнивает имена с учётом
/// регистра, поэтому контракт не зависит от политики именования.
/// </para>
/// </remarks>
public sealed record AzurPilotConfiguration
{
    /// <summary>Версия схемы конфигурации, которую понимает эта сборка.</summary>
    /// <remarks>
    /// Единственный владелец поддерживаемой версии схемы: и загрузчик, и встроенные значения по
    /// умолчанию, и тесты используют это значение, а не собственную копию числа.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Версия схемы конфигурации, объявленная в документе.</summary>
    /// <value>
    /// Обязательное целочисленное свойство <c>schemaVersion</c>. Документ без него невалиден, а
    /// значение, отличное от <see cref="CurrentSchemaVersion"/> и от legacy-версии
    /// <see cref="AzurPilotConfigurationV1.LegacySchemaVersion"/>, означает неподдерживаемую схему.
    /// </value>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Настройки диагностики приложения.</summary>
    /// <value>Обязательная секция <c>diagnostics</c>.</value>
    [JsonPropertyName("diagnostics")]
    public required DiagnosticsConfiguration Diagnostics { get; init; }

    /// <summary>Настройки MuMu.</summary>
    /// <value>Обязательная секция <c>mumu</c> схемы v2.</value>
    [JsonPropertyName("mumu")]
    public required MuMuConfiguration MuMu { get; init; }
}
