using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>Секция конфигурации MuMu схемы v2: выбор Android-экземпляра для провайдера.</summary>
/// <remarks>
/// <para>
/// Секция содержит ровно одну реальную настройку — <c>instance</c>. Настройки ADB, lifecycle игры,
/// screenshot/vision, input, keymap, разрешения экрана, OCR и override пути установки сюда не
/// добавляются: настройка появляется вместе с реализацией своей capability, а не заранее.
/// </para>
/// <para>
/// Синтаксис значения принадлежит <see cref="MuMuInstanceValue"/>; контракт секции его не повторяет, а
/// строгое чтение JSON опирается на этот единственный владелец.
/// </para>
/// </remarks>
public sealed record MuMuConfiguration
{
    /// <summary>Выбор Android-экземпляра MuMu.</summary>
    /// <value>
    /// Обязательное строковое свойство <c>instance</c>: либо литерал <c>auto</c>, либо провайдерская
    /// форма <c>mumu:&lt;digits&gt;</c>. Любое другое значение или нестроковый тип отвергаются как
    /// несоответствие схеме v2.
    /// </value>
    [JsonPropertyName("instance")]
    [JsonConverter(typeof(StrictMuMuInstanceJsonConverter))]
    public required string Instance { get; init; }
}
