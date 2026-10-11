using System.Text;
using AzurPilot.App.OperatorServices;
using Microsoft.Extensions.Logging;
using Terminal.Gui.Text;

namespace AzurPilot.App.OperatorUi;

internal static class OperatorUiLogPresentation
{
    internal static string FormatSummaryHeader(AzurPilotOperatorLogEntry entry)
        => FormatSummaryHeader(entry, maxWidth: null);

    internal static string FormatSummaryHeader(AzurPilotOperatorLogEntry entry, int maxWidth)
        => FormatSummaryHeader(entry, (int?)maxWidth);

    private static string FormatSummaryHeader(AzurPilotOperatorLogEntry entry, int? maxWidth)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string category = OperatorUiSession.CleanText(entry.Category, 160);
        string[] segments = category.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string source = segments.Length <= 2 ? category : string.Join('.', segments.TakeLast(2));
        string prefix = $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  [{LevelLabel(entry.Level)}]  ";
        if (maxWidth is int width)
        {
            if (width <= 0)
            {
                return string.Empty;
            }

            int sourceWidth = width - CellWidth(prefix);
            if (CellWidth(source) > sourceWidth)
            {
                source = segments.Length > 1 ? segments[^1] : category;
                source = TruncateToColumns(source, sourceWidth);
            }

            return TruncateToColumns(prefix + source, width);
        }

        return prefix + source;
    }

    internal static string SummaryMessage(AzurPilotOperatorLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return OperatorUiSession.CleanText(entry.Message, 1_024);
    }

    internal static IReadOnlyList<string> FormatTechnicalDetails(AzurPilotOperatorLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<string> lines =
        [
            $"Время (UTC): {entry.TimestampUtc:O}",
            $"Уровень: {entry.Level}",
            $"Источник: {OperatorUiSession.CleanText(entry.Category, 160)}",
            $"Событие: {entry.EventId} {OperatorUiSession.CleanText(entry.EventName, 96)}",
            $"Trace ID: {OperatorUiSession.CleanText(entry.TraceId ?? "не задан", 128)}",
            $"Span ID: {OperatorUiSession.CleanText(entry.SpanId ?? "не задан", 64)}",
            $"Сообщение: {SummaryMessage(entry)}",
        ];

        if (entry.Properties.Count != 0)
        {
            lines.Add("Параметры:");
            lines.AddRange(entry.Properties.Select(static pair =>
                $"{OperatorUiSession.CleanText(pair.Key, 64)}: {OperatorUiSession.CleanText(pair.Value, 160)}"));
        }

        return lines;
    }

    internal static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "ОТЛАДКА",
        LogLevel.Information => "ИНФО",
        LogLevel.Warning => "ВНИМАНИЕ",
        LogLevel.Error => "ОШИБКА",
        LogLevel.Critical => "КРИТИЧНО",
        LogLevel.None => "—",
        _ => "—",
    };

    private static int CellWidth(string value)
        => value.EnumerateRunes().Sum(static rune => Math.Max(0, rune.GetColumns()));

    private static string TruncateToColumns(string value, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        if (CellWidth(value) <= maxWidth)
        {
            return value;
        }

        const string ellipsis = "…";
        int contentWidth = Math.Max(0, maxWidth - CellWidth(ellipsis));
        StringBuilder result = new();
        int width = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeWidth = Math.Max(0, rune.GetColumns());
            if (width + runeWidth > contentWidth)
            {
                break;
            }

            _ = result.Append(rune);
            width += runeWidth;
        }

        return result.Append('…').ToString();
    }
}
