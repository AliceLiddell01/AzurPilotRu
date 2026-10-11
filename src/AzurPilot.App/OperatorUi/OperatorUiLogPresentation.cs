using AzurPilot.App.OperatorServices;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.OperatorUi;

internal static class OperatorUiLogPresentation
{
    internal static string FormatSummaryHeader(AzurPilotOperatorLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string category = OperatorUiSession.CleanText(entry.Category, 160);
        string[] segments = category.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string source = segments.Length <= 2 ? category : string.Join('.', segments.TakeLast(2));
        return $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  [{LevelLabel(entry.Level)}]  {source}";
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
            $"Уровень: {LevelLabel(entry.Level)}",
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
}
