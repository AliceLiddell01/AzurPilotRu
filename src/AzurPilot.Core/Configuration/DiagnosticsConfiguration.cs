using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Configuration;

/// <summary>Настройки диагностики приложения: минимальный уровень структурированного логирования.</summary>
/// <remarks>
/// Уровень типизирован стандартным <see cref="LogLevel"/>: перечень допустимых значений принадлежит
/// logging stack, поэтому Core не хранит вторую копию этого перечня.
/// </remarks>
public sealed record DiagnosticsConfiguration
{
    /// <summary>Минимальный уровень логирования, попадающий в structured runtime logs.</summary>
    /// <value>
    /// Обязательное свойство <c>minimumLevel</c>, задаваемое JSON-строкой с точным именем уровня
    /// <see cref="LogLevel"/>. Числовая форма, неизвестное имя и произвольный регистр отвергаются как
    /// несоответствие схеме v1.
    /// </value>
    [JsonPropertyName("minimumLevel")]
    [JsonConverter(typeof(StrictLogLevelJsonConverter))]
    public required LogLevel MinimumLevel { get; init; }
}
