using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Строгий JSON-конвертер значения <c>mumu.instance</c>: принимает только формы схемы v2.
/// </summary>
/// <remarks>
/// <para>
/// Конвертер не хранит собственную грамматику: допустимость значения проверяет единственный владелец
/// <see cref="MuMuInstanceValue"/>. Здесь задаётся только форма отказа: значение не той формы или не
/// строкового типа — несоответствие схеме v2, а не подстановка значения по умолчанию.
/// </para>
/// <para>
/// Значение по умолчанию при этом не теряется: <c>mumu.instance = "auto"</c> приходит из владельца
/// встроенных defaults, а не из конвертера.
/// </para>
/// </remarks>
internal sealed class StrictMuMuInstanceJsonConverter : JsonConverter<string>
{
    /// <summary>Читает значение <c>mumu.instance</c> из JSON-строки.</summary>
    /// <param name="reader">Читатель JSON, позиция которого указывает на значение.</param>
    /// <param name="typeToConvert">Тип значения; всегда <see cref="string"/>.</param>
    /// <param name="options">Опции сериализации, переданные вызывающей стороной.</param>
    /// <returns>Строка, точно соответствующая синтаксису схемы v2.</returns>
    /// <exception cref="JsonException">
    /// Значение не является JSON-строкой либо не соответствует синтаксису
    /// <see cref="MuMuInstanceValue"/>.
    /// </exception>
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Значение mumu.instance должно быть JSON-строкой, а получено {reader.TokenType}.");
        }

        string text = reader.GetString()!;
        if (!MuMuInstanceValue.IsValid(text))
        {
            throw new JsonException(
                $"Значение mumu.instance «{text}» недопустимо; ожидается «{MuMuInstanceValue.AutoValue}» "
                + $"либо «{MuMuInstanceValue.ProviderPrefix}<номер>» без ведущих нулей.");
        }

        return text;
    }

    /// <summary>Записывает значение <c>mumu.instance</c> строкой.</summary>
    /// <param name="writer">Писатель JSON.</param>
    /// <param name="value">Значение свойства <c>mumu.instance</c>.</param>
    /// <param name="options">Опции сериализации, переданные вызывающей стороной.</param>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}
