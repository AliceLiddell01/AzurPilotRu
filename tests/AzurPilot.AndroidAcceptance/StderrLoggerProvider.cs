using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Logging provider инструмента приёмки Android: structured записи идут в <c>stderr</c>.
/// </summary>
/// <remarks>
/// <para>
/// Инструмент использует существующий стек <c>Microsoft.Extensions.Logging</c>, но не подключает сторонних
/// provider-ов: новых пакетов в репозитории не появляется. Записи уходят в <c>stderr</c>, поэтому
/// человекочитаемый отчёт в <c>stdout</c> остаётся единственной поверхностью итога и structured log с ним
/// не смешивается.
/// </para>
/// <para>
/// Уровень <see cref="LogLevel.Debug"/> не выводится: bounded polling readiness и lifecycle сообщает
/// наблюдения на уровне Debug, и для приёмки достаточно итоговых событий уровня Information и выше.
/// </para>
/// </remarks>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    /// <summary>Минимальный уровень выводимых записей.</summary>
    internal const LogLevel MinimumLevel = LogLevel.Information;

    private static int _writtenRecordCount;
    private static int _warningRecordCount;
    private static int _errorRecordCount;

    /// <summary>Число записей, выведенных в <c>stderr</c> за прогон.</summary>
    internal static int WrittenRecordCount => Volatile.Read(ref _writtenRecordCount);

    /// <summary>Число выведенных записей уровня Warning.</summary>
    internal static int WarningRecordCount => Volatile.Read(ref _warningRecordCount);

    /// <summary>Число выведенных записей уровня Error и выше.</summary>
    internal static int ErrorRecordCount => Volatile.Read(ref _errorRecordCount);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Ресурсов, требующих освобождения, у provider-а нет.
    }

    /// <summary>Учитывает выведенную запись по уровню.</summary>
    /// <param name="logLevel">Уровень выведенной записи.</param>
    internal static void CountRecord(LogLevel logLevel)
    {
        _ = Interlocked.Increment(ref _writtenRecordCount);

        if (logLevel < LogLevel.Warning)
        {
            return;
        }

        _ = logLevel >= LogLevel.Error
            ? Interlocked.Increment(ref _errorRecordCount)
            : Interlocked.Increment(ref _warningRecordCount);
    }
}

/// <summary>
/// Structured logger инструмента приёмки Android, пишущий одну JSON-запись в строку <c>stderr</c>.
/// </summary>
internal sealed class StderrLogger : ILogger
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _category;

    /// <summary>Создаёт logger для категории.</summary>
    /// <param name="category">Категория logger-а.</param>
    internal StderrLogger(string category)
    {
        _category = category;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        // Correlation scope приёмке не нужен: прогон однопоточный и однократный, а operation identity
        // несёт сам отчёт.
        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None && logLevel >= StderrLoggerProvider.MinimumLevel;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        if (exception is not null)
        {
            message = message + " [" + exception.GetType().FullName + "]";
        }

        Dictionary<string, string> record = new(StringComparer.Ordinal)
        {
            ["Timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["LogLevel"] = logLevel.ToString(),
            ["EventId"] = eventId.Id.ToString(CultureInfo.InvariantCulture),
            ["Category"] = _category,
            ["Message"] = message,
        };

        Console.Error.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
        StderrLoggerProvider.CountRecord(logLevel);
    }
}
