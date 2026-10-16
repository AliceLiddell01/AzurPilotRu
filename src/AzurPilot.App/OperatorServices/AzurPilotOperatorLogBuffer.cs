using System.Collections.ObjectModel;
using System.Diagnostics;
using AzurPilot.App.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.OperatorServices;

/// <summary>Провайдер журнала с ограниченным буфером в памяти для интерактивного оператора.</summary>
/// <remarks>
/// Провайдер не пишет в stdout или stderr: интерактивный интерфейс читает снимок из ограниченного
/// буфера, поэтому фоновые операции хоста и запуск процессов не повреждают экран Terminal.Gui.
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
        string rawMessage = formatter(state, exception);
        if (exception is not null)
        {
            rawMessage += " " + exception.Message;
        }

        List<KeyValuePair<string, string>> rawProperties = [];
        if (state is IEnumerable<KeyValuePair<string, object?>> structured)
        {
            rawProperties.AddRange(structured.Where(pair => pair.Key != "{OriginalFormat}")
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value?.ToString() ?? "null")));
        }

        string message = AzurPilotLogSanitizer.Message(rawMessage, rawProperties, MaximumMessageLength);
        Dictionary<string, string> properties = new(StringComparer.Ordinal);

        if (rawProperties.Count != 0)
        {
            foreach (KeyValuePair<string, string> pair in rawProperties)
            {
                if (properties.Count == MaximumPropertyCount)
                {
                    break;
                }

                _ = properties.TryAdd(Bound(pair.Key, 64),
                    AzurPilotLogSanitizer.Property(pair.Key, pair.Value, MaximumPropertyLength));
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
        => AzurPilotLogSanitizer.Text(value, maximumLength);

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

/// <summary>Ограниченная структурированная запись журнала.</summary>
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
    IReadOnlyDictionary<string, string> Properties);
