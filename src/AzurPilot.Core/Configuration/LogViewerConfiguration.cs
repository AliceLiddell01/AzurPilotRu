using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>Секция настроек просмотрщика логов схемы v3.</summary>
/// <remarks>
/// Настройка управляет только собственным окном просмотра журнала и не является вторым владельцем
/// минимального уровня логирования: уровень принадлежит <see cref="DiagnosticsConfiguration.MinimumLevel"/>
/// и определяет, что вообще попадает в журнал.
/// </remarks>
public sealed record LogViewerConfiguration
{
    /// <summary>Признак того, что просмотрщик логов включён.</summary>
    /// <value>
    /// Обязательный ключ <c>logging.viewer.enabled</c> булева типа. Принимаются ровно литералы
    /// <c>true</c> и <c>false</c>; строковая форма и любое другое значение отвергаются загрузкой как
    /// несоответствие схеме v3.
    /// </value>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }
}
