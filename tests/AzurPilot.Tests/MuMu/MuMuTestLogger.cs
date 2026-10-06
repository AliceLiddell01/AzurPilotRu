using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Tests.MuMu;

/// <summary>Одна записанная запись structured log orchestration.</summary>
/// <param name="Level">Уровень записи.</param>
/// <param name="EventId">EventId source-generated события.</param>
/// <param name="Message">Отформатированное сообщение записи.</param>
internal sealed record MuMuLogRecord(LogLevel Level, int EventId, string Message);

/// <summary>
/// Логгер, который запоминает записи orchestration для проверок уровня и состава событий.
/// </summary>
/// <remarks>
/// Логгер включён для всех уровней: так проверяется, что каждый poll уходит максимум на
/// <see cref="LogLevel.Debug"/>, а полный вывод control utility не попадает в логи ни на одном уровне.
/// </remarks>
internal sealed class MuMuTestLogger : ILogger<MuMuLifecycleService>
{
    private readonly Lock _sync = new();
    private readonly List<MuMuLogRecord> _records = [];

    /// <summary>Записи в порядке появления.</summary>
    internal IReadOnlyList<MuMuLogRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return [.. _records];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string message = formatter(state, exception);
        lock (_sync)
        {
            _records.Add(new MuMuLogRecord(logLevel, eventId.Id, message));
        }
    }
}
