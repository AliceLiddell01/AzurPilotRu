using System.Text.Json;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Запись structured runtime log, прочитанная из stderr реального процесса приложения.
/// </summary>
/// <remarks>
/// Записи читаются из встроенного JSON console formatter, который является единственным logging
/// provider host-а. Поэтому проверки опираются на фактические structured diagnostics, а не на текст
/// сообщений и не на порядок строк.
/// </remarks>
/// <param name="Category">Категория логирования записи.</param>
/// <param name="LogLevel">Уровень записи.</param>
/// <param name="Message">Отформатированное сообщение записи.</param>
/// <param name="State">Structured properties сообщения.</param>
/// <param name="Scopes">Scopes записи: сюда попадает correlation identifier операции.</param>
internal sealed record StructuredLogRecord(
    string Category,
    string LogLevel,
    string Message,
    IReadOnlyDictionary<string, string> State,
    IReadOnlyDictionary<string, string> Scopes)
{
    /// <summary>Имя property записи JSON console formatter с уровнем логирования.</summary>
    private const string LogLevelProperty = "LogLevel";

    /// <summary>Имя property записи JSON console formatter с категорией логирования.</summary>
    private const string CategoryProperty = "Category";

    /// <summary>Имя property записи JSON console formatter с сообщением.</summary>
    private const string MessageProperty = "Message";

    /// <summary>Имя property записи JSON console formatter со structured properties сообщения.</summary>
    private const string StateProperty = "State";

    /// <summary>Имя property записи JSON console formatter со scopes записи.</summary>
    private const string ScopesProperty = "Scopes";

    /// <summary>Correlation identifier операции, переданный project-owned событием.</summary>
    internal string? CorrelationId => Read(State, "CorrelationId");

    /// <summary>Стабильный application-level код отказа, если запись описывает отказ.</summary>
    internal string? FailureCode => Read(State, "FailureCode");

    /// <summary>Trace identifier операции из scopes записи.</summary>
    internal string? TraceId => Read(Scopes, "TraceId");

    /// <summary>Читает значение structured property записи.</summary>
    /// <param name="properties">Набор properties записи.</param>
    /// <param name="name">Имя property.</param>
    /// <returns>Значение property либо <see langword="null"/>, если property отсутствует.</returns>
    internal static string? Read(IReadOnlyDictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Читает structured записи из потока вывода процесса.</summary>
    /// <param name="stream">Содержимое stdout или stderr процесса.</param>
    /// <returns>Записи JSON console formatter в порядке появления; прочие строки пропускаются.</returns>
    internal static IReadOnlyList<StructuredLogRecord> ReadFrom(string stream)
    {
        List<StructuredLogRecord> records = [];
        foreach (string line in stream.Split('\n'))
        {
            string candidate = line.Trim();
            if (candidate.Length == 0 || candidate[0] != '{')
            {
                continue;
            }

            StructuredLogRecord? record = TryRead(candidate);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static StructuredLogRecord? TryRead(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(LogLevelProperty, out JsonElement level)
                || level.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new StructuredLogRecord(
                ReadText(root, CategoryProperty),
                level.GetString() ?? string.Empty,
                ReadText(root, MessageProperty),
                ReadObject(root, StateProperty),
                ReadScopes(root));
        }
        catch (JsonException)
        {
            // Строка не является записью JSON console formatter.
            return null;
        }
    }

    private static string ReadText(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static Dictionary<string, string> ReadObject(JsonElement root, string propertyName)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                values[property.Name] = ReadValue(property.Value);
            }
        }

        return values;
    }

    private static Dictionary<string, string> ReadScopes(JsonElement root)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (!root.TryGetProperty(ScopesProperty, out JsonElement scopes) || scopes.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement scope in scopes.EnumerateArray())
        {
            if (scope.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty property in scope.EnumerateObject())
            {
                values[property.Name] = ReadValue(property.Value);
            }
        }

        return values;
    }

    private static string ReadValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
