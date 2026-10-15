using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Строгий JSON-конвертер <see cref="LogLevel"/>: принимает только точное имя уровня логирования.
/// </summary>
/// <remarks>
/// <para>
/// Штатный <c>JsonStringEnumConverter</c> для legacy-схемы не подходит: он сравнивает имена без учёта
/// регистра и принимает числовую форму значения, то есть пропускал бы документ, не соответствующий
/// своей схеме. Здесь имя уровня сравнивается с ordinal-семантикой, а числовая форма отвергается.
/// </para>
/// <para>
/// Перечень допустимых значений не хранится здесь: его владелец — <see cref="LogLevelNames"/>, общий с
/// строгим YAML-ридером, поэтому вторая копия списка не появляется.
/// </para>
/// </remarks>
internal sealed class StrictLogLevelJsonConverter : JsonConverter<LogLevel>
{
    /// <summary>Читает имя уровня логирования из JSON-строки.</summary>
    /// <param name="reader">Читатель JSON, позиция которого указывает на значение.</param>
    /// <param name="typeToConvert">Тип значения; всегда <see cref="LogLevel"/>.</param>
    /// <param name="options">Опции сериализации, переданные вызывающей стороной.</param>
    /// <returns>Уровень логирования, точно соответствующий имени в документе.</returns>
    /// <exception cref="JsonException">
    /// Значение не является JSON-строкой либо не совпадает ни с одним именем <see cref="LogLevel"/>.
    /// </exception>
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Значение minimumLevel должно быть JSON-строкой с именем уровня логирования, "
                + $"а получено {reader.TokenType}.");
        }

        string text = reader.GetString()!;
        if (!LogLevelNames.TryParse(text, out LogLevel level))
        {
            throw new JsonException(
                $"Значение minimumLevel «{text}» неизвестно; допустимы имена: "
                + $"{string.Join(", ", LogLevelNames.All)}.");
        }

        return level;
    }

    /// <summary>Записывает уровень логирования именем его значения.</summary>
    /// <param name="writer">Писатель JSON.</param>
    /// <param name="value">Уровень логирования.</param>
    /// <param name="options">Опции сериализации, переданные вызывающей стороной.</param>
    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
