using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.OperatorServices;

/// <summary>Bounded in-memory logging provider для интерактивного оператора.</summary>
/// <remarks>
/// Provider не пишет в stdout или stderr: интерактивный renderer читает snapshot из ограниченного
/// буфера, поэтому background host и process runners не повреждают экран Terminal.Gui.
/// </remarks>
public sealed class AzurPilotOperatorLogBuffer : ILoggerProvider
{
    private const int Capacity = 256;
    private const int MaximumMessageLength = 1_024;
    private const int MaximumCategoryLength = 160;
    private const int MaximumPropertyCount = 12;
    private const int MaximumPropertyLength = 160;

    private readonly Lock _gate = new();
    private readonly Queue<AzurPilotOperatorLogEntry> _entries = new();
    private long _sequence;
    private bool _disposed;

    /// <summary>Создаёт логгер с ограничением в 256 последних структурированных записей.</summary>
    public AzurPilotOperatorLogBuffer()
    {
    }

    /// <summary>Создаёт логгер для указанной категории.</summary>
    /// <param name="categoryName">Категория логирования.</param>
    /// <returns>Логгер без прямого вывода в консоль.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new BufferLogger(this, categoryName);
    }

    /// <summary>Возвращает независимую копию последних записей.</summary>
    public IReadOnlyList<AzurPilotOperatorLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly([.. _entries]);
        }
    }

    /// <summary>Возвращает строки последних записей для отдельной log-панели TUI.</summary>
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_gate)
        {
            return [.. _entries.Select(static entry => entry.ToDisplayLine())];
        }
    }

    /// <summary>Освобождает provider без дополнительного вывода.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void Add<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = Bound(formatter(state, exception), MaximumMessageLength);
        Dictionary<string, string> properties = new(StringComparer.Ordinal);

        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (KeyValuePair<string, object?> pair in values)
            {
                if (properties.Count == MaximumPropertyCount)
                {
                    break;
                }

                if (pair.Key == "{OriginalFormat}")
                {
                    continue;
                }

                string value = pair.Value?.ToString() ?? "null";
                _ = properties.TryAdd(Bound(pair.Key, 64), Bound(value, MaximumPropertyLength));
            }
        }

        Activity? activity = Activity.Current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_entries.Count >= Capacity)
            {
                _ = _entries.Dequeue();
            }

            AzurPilotOperatorLogEntry entry = new(
                Sequence: ++_sequence,
                TimestampUtc: DateTimeOffset.UtcNow,
                Level: level,
                Category: Bound(category, MaximumCategoryLength),
                EventId: eventId.Id,
                EventName: Bound(eventId.Name ?? string.Empty, 96),
                TraceId: activity?.TraceId.ToHexString(),
                SpanId: activity?.SpanId.ToHexString(),
                Message: message,
                Properties: new ReadOnlyDictionary<string, string>(properties));
            _entries.Enqueue(entry);
        }
    }

    private static string Bound(string value, int maximumLength)
    {
        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return singleLine.Length <= maximumLength ? singleLine : string.Concat(singleLine.AsSpan(0, maximumLength - 1), "…");
    }

    private sealed class BufferLogger(AzurPilotOperatorLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                owner.Add(category, logLevel, eventId, state, exception, formatter);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Bounded структурированная запись log buffer.</summary>
public sealed record AzurPilotOperatorLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    int EventId,
    string EventName,
    string? TraceId,
    string? SpanId,
    string Message,
    IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>Формирует однострочное представление для TUI log pane.</summary>
    public string ToDisplayLine()
    {
        string correlation = TraceId is null ? string.Empty : $" trace={TraceId[..Math.Min(8, TraceId.Length)]}";
        string eventLabel = EventName.Length > 0 ? $" {EventName}" : string.Empty;
        string properties = Properties.Count == 0
            ? string.Empty
            : " " + string.Join(" ", Properties.Select(static pair => $"{pair.Key}={pair.Value}"));
        return $"{TimestampUtc:HH:mm:ss.fff} {Level,-11} {Category} [{EventId}{eventLabel}]{correlation} {Message}{properties}";
    }
}
