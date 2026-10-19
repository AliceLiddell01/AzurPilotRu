using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurPilot.App.Agent;

/// <summary>Одна строка trace.jsonl; поля без значения не записываются.</summary>
/// <param name="Seq">Порядковый номер записи внутри прогона.</param>
/// <param name="TMs">Миллисекунды от начала прогона по монотонным часам в момент записи события.</param>
/// <param name="Utc">UTC-момент записи для сопоставления нескольких вызовов.</param>
/// <param name="Kind">Вид события.</param>
/// <param name="Source">Наблюдавшая граница.</param>
internal sealed record AgentDebugTraceLine(long Seq, double TMs, string Utc, string Kind, string Source)
{
    /// <summary>Ссылка на запись кадра прогона (например, f0003).</summary>
    public string? Frame { get; init; }
    /// <summary>FrameId production provenance.</summary>
    public string? FrameId { get; init; }
    /// <summary>Переход каталога.</summary>
    public string? ActionId { get; init; }
    /// <summary>Фаза телеметрии.</summary>
    public string? PhaseId { get; init; }
    /// <summary>Ограниченные факты события.</summary>
    public object? Data { get; init; }
}

/// <summary>
/// Писатель trace.jsonl одного прогона. Номер и время присваиваются под одним замком, поэтому порядок строк
/// совпадает с фактическим порядком записи событий. Превышение лимитов не теряется молча: счётчик
/// пропущенных событий и причина попадают в manifest.
/// </summary>
internal sealed class AgentDebugTrace : IDisposable
{
    internal const int MaximumEvents = 50_000;
    internal const long MaximumBytes = 32L * 1024 * 1024;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new AgentFailureConverter() },
    };

    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly Lock _gate = new();
    private readonly FileStream _stream;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private long _sequence;
    private long _bytes;
    private int _dropped;
    private int _serializationFailures;
    private string? _failure;
    private long _writeTicks;
    private bool _closed;

    internal AgentDebugTrace(string path, TimeProvider clock, long origin)
    {
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        _clock = clock;
        _origin = origin;
    }

    internal long Events { get { lock (_gate) { return _sequence; } } }
    internal long Bytes { get { lock (_gate) { return _bytes; } } }
    internal int Dropped { get { lock (_gate) { return _dropped; } } }
    internal string? Failure { get { lock (_gate) { return _failure ?? (_serializationFailures > 0 ? "serialization_failed" : null); } } }
    internal double WriteMs { get { lock (_gate) { return Math.Round(Stopwatch.GetElapsedTime(0, _writeTicks).TotalMilliseconds, 1); } } }

    /// <summary>Миллисекунды от начала прогона по монотонным часам production.</summary>
    internal double ElapsedMs(long timestamp)
    {
        return Math.Round(_clock.GetElapsedTime(_origin, timestamp).TotalMilliseconds, 1);
    }

    internal void Record(string source, string kind, object? data = null, string? frame = null, Guid? frameId = null,
        string? actionId = null, string? phaseId = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_failure is not null || _sequence >= MaximumEvents || _bytes >= MaximumBytes)
            {
                _dropped++;
                _failure ??= _sequence >= MaximumEvents ? "event_limit" : _bytes >= MaximumBytes ? "byte_limit" : _failure;
                return;
            }

            long started = Stopwatch.GetTimestamp();
            byte[] bytes;
            try
            {
                AgentDebugTraceLine line = new(_sequence + 1, ElapsedMs(_clock.GetTimestamp()),
                    _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    kind, source)
                {
                    Frame = frame,
                    FrameId = frameId?.ToString("N"),
                    ActionId = actionId,
                    PhaseId = phaseId,
                    Data = data,
                };
                bytes = JsonSerializer.SerializeToUtf8Bytes(line, Options);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                // Несериализуемое событие теряется одно и учитывается; следующие события пишутся дальше.
                _dropped++;
                _serializationFailures++;
                _writeTicks += Stopwatch.GetTimestamp() - started;
                return;
            }

            try
            {
                _sequence++;
                _stream.Write(bytes);
                _stream.Write(NewLine);
                // Каждая строка передаётся ОС сразу: аварийное завершение сохраняет уже наблюдённые события.
                _stream.Flush();
                _bytes += bytes.Length + 1;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _dropped++;
                _failure = "write_failed:" + exception.GetType().Name;
            }
            finally
            {
                _writeTicks += Stopwatch.GetTimestamp() - started;
            }
        }
    }

    /// <summary>Закрывает файл; ошибка закрытия сохраняется как причина неполноты.</summary>
    internal void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                _stream.Flush(flushToDisk: true);
                _stream.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure ??= "close_failed:" + exception.GetType().Name;
            }
        }
    }

    public void Dispose()
    {
        Close();
    }
}
