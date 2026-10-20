using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Точечное изменение интервалов секции <c>screenshot</c> в тексте YAML: меняются только значения нужных
/// ключей либо добавляются недостающие строки, остальной текст (комментарии, порядок, отступы, переводы строк)
/// сохраняется байт в байт.
/// </summary>
/// <remarks>
/// Позиции берутся из событий того же парсера YamlDotNet, которым пользуется строгий ридер; документ до
/// правки уже прошёл проверку схемы, а результат правки проверяет вызывающий код повторной загрузкой.
/// </remarks>
internal static class AzurPilotConfigurationYamlEdit
{
    private const string Section = "screenshot";
    private const string NormalKey = "normalIntervalMs";
    private const string CombatKey = "combatIntervalMs";

    /// <summary>Задаёт <c>screenshot.normalIntervalMs</c> и, если передан, <c>screenshot.combatIntervalMs</c>.</summary>
    /// <param name="text">Документ без BOM.</param>
    /// <param name="normalIntervalMilliseconds">Новое значение обычного интервала.</param>
    /// <param name="combatIntervalMilliseconds">Новое значение боевого интервала либо null — ключ не трогается.</param>
    /// <param name="failure">Причина отказа, если форма документа не допускает точечной правки.</param>
    /// <returns>Изменённый документ либо null.</returns>
    internal static string? SetScreenshotIntervals(string text, int normalIntervalMilliseconds,
        int? combatIntervalMilliseconds, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(text);
        failure = null;
        List<ParsingEvent> events = [];
        try
        {
            Parser parser = new(new StringReader(text));
            while (parser.MoveNext())
            {
                events.Add(parser.Current!);
            }
        }
        catch (YamlException exception)
        {
            failure = "YAML не разобран: " + exception.Message;
            return null;
        }

        List<(string Key, int Value)> wanted = [(NormalKey, normalIntervalMilliseconds)];
        if (combatIntervalMilliseconds is int combat)
        {
            wanted.Add((CombatKey, combat));
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int rootIndex = events.FindIndex(e => e is MappingStart);
        if (rootIndex < 0 || events[rootIndex] is not MappingStart { Style: not MappingStyle.Flow })
        {
            failure = "корень документа не является блочным mapping.";
            return null;
        }

        string indent = new(' ', NestedIndent(events, rootIndex));
        int index = rootIndex + 1;
        while (events[index] is Scalar key)
        {
            int valueIndex = index + 1;
            if (key.Value == Section)
            {
                return EditSection(text, events, key, valueIndex, wanted, indent, newLine, out failure);
            }

            index = Skip(events, valueIndex);
        }

        // Секции нет: она добавляется в конец документа, остальной текст не меняется.
        string prefix = text.Length == 0 || text.EndsWith('\n') ? text : text + newLine;
        string lines = string.Concat(wanted.Select(entry => indent + Line(entry) + newLine));
        return prefix + Section + ":" + newLine + lines;
    }

    private static string? EditSection(string text, List<ParsingEvent> events, Scalar key, int valueIndex,
        List<(string Key, int Value)> wanted, string indent, string newLine, out string? failure)
    {
        failure = null;
        List<(int Start, int End, string Replacement)> edits = [];
        int anchor;
        string entryIndent = indent;
        Dictionary<string, Scalar> values = new(StringComparer.Ordinal);
        switch (events[valueIndex])
        {
            case Scalar empty:
                // `screenshot:`, `screenshot: ~` или `screenshot: null` — пустая секция; явный null убирается,
                // иначе вложенные строки не образовали бы mapping.
                if (empty.End.Index > empty.Start.Index)
                {
                    edits.Add(((int)empty.Start.Index, (int)empty.End.Index, string.Empty));
                }

                anchor = (int)key.End.Index;
                break;
            case MappingStart { Style: not MappingStyle.Flow }:
                int index = valueIndex + 1;
                anchor = (int)key.End.Index;
                bool first = true;
                while (events[index] is Scalar entryKey)
                {
                    if (events[index + 1] is not Scalar entryValue)
                    {
                        failure = $"значение «{Section}.{entryKey.Value}» не является скаляром.";
                        return null;
                    }

                    if (first)
                    {
                        entryIndent = new string(' ', (int)entryKey.Start.Column - 1);
                        first = false;
                    }

                    values[entryKey.Value] = entryValue;
                    anchor = (int)entryValue.End.Index;
                    index += 2;
                }

                break;
            default:
                failure = $"секция «{Section}» записана не блочным mapping; точечная правка невозможна.";
                return null;
        }

        List<string> inserted = [];
        foreach ((string name, int value) in wanted)
        {
            if (values.TryGetValue(name, out Scalar? scalar))
            {
                edits.Add(((int)scalar.Start.Index, (int)scalar.End.Index, value.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                inserted.Add(newLine + entryIndent + Line((name, value)));
            }
        }

        if (inserted.Count > 0)
        {
            int lineEnd = text.IndexOfAny(['\r', '\n'], anchor);
            int position = lineEnd < 0 ? text.Length : lineEnd;
            edits.Add((position, position, string.Concat(inserted)));
        }

        string result = text;
        foreach ((int start, int end, string replacement) in edits.OrderByDescending(edit => edit.Start))
        {
            result = string.Concat(result.AsSpan(0, start), replacement, result.AsSpan(end));
        }

        return result;
    }

    private static string Line((string Key, int Value) entry)
        => entry.Key + ": " + entry.Value.ToString(CultureInfo.InvariantCulture);

    // Отступ новой секции берётся из первой вложенной секции документа, чтобы не смешивать стили.
    private static int NestedIndent(List<ParsingEvent> events, int rootIndex)
    {
        for (int index = rootIndex + 1; index + 1 < events.Count; index++)
        {
            if (events[index] is MappingStart { Style: not MappingStyle.Flow } && events[index + 1] is Scalar nested)
            {
                return (int)nested.Start.Column - 1;
            }
        }

        return 2;
    }

    private static int Skip(List<ParsingEvent> events, int index)
    {
        int depth = 0;
        do
        {
            depth += events[index] switch
            {
                MappingStart or SequenceStart => 1,
                MappingEnd or SequenceEnd => -1,
                _ => 0,
            };
            index++;
        }
        while (depth > 0);

        return index;
    }
}
