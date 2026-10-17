using System.Text.Json;

namespace AzurPilot.TuiHarness;

// Семантические признаки видимой поверхности. Геометрия Terminal.Gui сюда не копируется.
internal static class TerminalUiObservation
{
    internal static string? Operation(JsonElement snapshot)
    {
        return Lines(snapshot).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Операция:", StringComparison.Ordinal));
    }

    internal static string[] History(JsonElement snapshot)
    {
        string[] lines = Lines(snapshot);
        (int start, int end) = HistoryBounds(lines);
        return [.. lines[start..end].Select(line => line.TrimEnd())];
    }

    internal static (int X, int Y) HistoryPoint(JsonElement snapshot)
    {
        (int start, int end) = HistoryBounds(Lines(snapshot));
        return (snapshot.GetProperty("columns").GetInt32() / 2, start + ((end - start) / 2));
    }

    private static (int Start, int End) HistoryBounds(string[] lines)
    {
        int heading = Array.FindIndex(lines, line => line.TrimStart().StartsWith("── Сообщения и результаты", StringComparison.Ordinal));
        int composer = Array.FindIndex(lines, heading < 0 ? 0 : heading + 1,
            line => line.TrimEnd().Length > 0 && line.TrimEnd().All(character => character == '─'));
        return heading < 0 || composer <= heading
            ? throw new HarnessBlockedException("Не найдены видимые границы истории и области ввода.")
            : (heading + 1, composer);
    }

    internal static void RequireHistoryScrolledUp(JsonElement before, JsonElement after, string? markerPrefix = null)
    {
        string[] oldRows = History(before);
        string[] newRows = History(after);
        // При прокрутке вверх прежние верхние строки сдвигаются вниз. Проверяем перекрытие,
        // а не изменение произвольного прямоугольника или часов/статуса операции.
        bool moved = Enumerable.Range(1, Math.Max(0, newRows.Length - 1)).Any(shift =>
        {
            int overlap = Math.Min(oldRows.Length, newRows.Length - shift);
            return overlap >= 2 && oldRows.Take(overlap).Any(row => !string.IsNullOrWhiteSpace(row))
                && oldRows.Take(overlap).SequenceEqual(newRows.Skip(shift).Take(overlap), StringComparer.Ordinal);
        });
        if (!moved && markerPrefix is not null)
        {
            static int[] Markers(string[] rows, string prefix)
            {
                return [.. rows.Where(row => row.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(row => int.Parse(row[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture))];
            }

            int[] oldMarkers = Markers(oldRows, markerPrefix);
            int[] newMarkers = Markers(newRows, markerPrefix);
            moved = oldMarkers.Length > 0 && newMarkers.Length > 0
                && newMarkers.Max() < oldMarkers.Max() && newMarkers.Min() < oldMarkers.Min();
        }

        if (!moved || oldRows.SequenceEqual(newRows, StringComparer.Ordinal)
            || before.GetProperty("viewportY").GetInt32() != after.GetProperty("viewportY").GetInt32())
        {
            throw new HarnessAssertionException("Не подтверждено смещение истории вверх при неизменном xterm viewport.");
        }
    }

    internal static void RequireHistoryEqual(JsonElement before, JsonElement after)
    {
        if (!History(before).SequenceEqual(History(after), StringComparer.Ordinal)
            || before.GetProperty("viewportY").GetInt32() != after.GetProperty("viewportY").GetInt32())
        {
            throw new HarnessAssertionException("Возврат к концу истории не восстановил исходный видимый текст.");
        }
    }

    private static string[] Lines(JsonElement snapshot)
    {
        return [.. snapshot.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!)];
    }
}
