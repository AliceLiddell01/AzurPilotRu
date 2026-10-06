using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Корневой контракт пользовательской конфигурации AzurPilot — схема v1.
/// </summary>
/// <remarks>
/// <para>
/// Схема намеренно минимальна: она содержит только то, что реально использует текущий application host.
/// Секции будущих capability (MuMu, ADB, lifecycle игры, screenshot/vision, input, OCR) и
/// placeholder-настройки не заводятся заранее — секция появляется вместе с реализацией capability.
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
    /// умолчанию используют это значение, а не собственную копию числа.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Версия схемы конфигурации, объявленная в документе.</summary>
    /// <value>
    /// Обязательное целочисленное свойство <c>schemaVersion</c>. Документ без него невалиден, а
    /// значение, отличное от <see cref="CurrentSchemaVersion"/>, означает неподдерживаемую схему.
    /// </value>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Настройки диагностики приложения.</summary>
    /// <value>Обязательная секция <c>diagnostics</c>.</value>
    [JsonPropertyName("diagnostics")]
    public required DiagnosticsConfiguration Diagnostics { get; init; }
}
