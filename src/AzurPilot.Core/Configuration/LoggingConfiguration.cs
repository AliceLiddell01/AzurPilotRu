using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>Секция настроек логирования схемы v3.</summary>
/// <remarks>
/// Секция не дублирует минимальный уровень логирования из
/// <see cref="DiagnosticsConfiguration.MinimumLevel"/>: здесь живут только настройки отображения
/// уже записанного журнала.
/// </remarks>
public sealed record LoggingConfiguration
{
    /// <summary>Настройки просмотрщика логов.</summary>
    /// <value>Обязательная секция <c>logging.viewer</c>.</value>
    [JsonPropertyName("viewer")]
    public required LogViewerConfiguration Viewer { get; init; }
}
