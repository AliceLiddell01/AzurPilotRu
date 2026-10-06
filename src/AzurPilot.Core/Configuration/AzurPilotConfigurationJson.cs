using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец JSON-контракта конфигурации: строгие опции и source-generated метаданные.
/// </summary>
/// <remarks>
/// <para>
/// Строгая семантика строится на <see cref="JsonSerializerOptions.Strict"/>. Фактически это означает:
/// <c>UnmappedMemberHandling = Disallow</c> (неизвестное свойство — ошибка),
/// <c>AllowDuplicateProperties = false</c> (duplicate property — ошибка),
/// <c>PropertyNameCaseInsensitive = false</c> (binding учитывает регистр),
/// <c>RespectNullableAnnotations = true</c> и <c>RespectRequiredConstructorParameters = true</c>
/// (нарушение nullable и required contract — ошибка), <c>AllowTrailingCommas = false</c>,
/// <c>ReadCommentHandling = Disallow</c>, <c>NumberHandling = Strict</c> и
/// <c>PreferredObjectCreationHandling = Replace</c>. Опции копируются, потому что
/// <see cref="JsonSerializerOptions.Strict"/> доступен только для чтения, а экземпляр опций
/// дополнительно документирует camelCase-имена.
/// </para>
/// <para>
/// Строгость применяется к каждой поддерживаемой source schema: у эффективной схемы v2 и у
/// legacy-схемы v1 свой контракт, поэтому неизвестное свойство для v1 (в том числе секция
/// <c>mumu</c>) и неизвестное свойство для v2 отвергаются, а не игнорируются.
/// </para>
/// <para>
/// Метаданные типов приходят из source-generated контекста, поэтому reflection resolver из
/// <see cref="JsonSerializerOptions.Strict"/> снимается: единственным источником метаданных остаётся
/// <see cref="AzurPilotConfigurationJsonContext"/>. Reflection-based десериализация типов конфигурации
/// не используется нигде.
/// </para>
/// <para>
/// Все настройки задаются до создания контекста: после создания контекста экземпляр опций становится
/// доступен только для чтения, поэтому порядок инициализации здесь обязателен.
/// </para>
/// </remarks>
internal static class AzurPilotConfigurationJson
{
    internal static readonly JsonSerializerOptions Options = CreateStrictOptions();

    internal static readonly AzurPilotConfigurationJsonContext Context = new(Options);

    private static JsonSerializerOptions CreateStrictOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerOptions.Strict)
        {
            // Имена свойств заданы атрибутами JsonPropertyName на контракте; политика camelCase
            // повторяет то же требование для свойства, у которого атрибут ещё не проставлен.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        options.TypeInfoResolver = null;
        return options;
    }
}

/// <summary>Source-generated метаданные эффективной схемы v2 и legacy-контракта v1.</summary>
[JsonSerializable(typeof(AzurPilotConfiguration))]
[JsonSerializable(typeof(AzurPilotConfigurationV1))]
internal sealed partial class AzurPilotConfigurationJsonContext : JsonSerializerContext;
