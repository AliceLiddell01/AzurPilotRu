using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.LogViewer;

/// <summary>Владеет единственным подключением viewer-а и его явным повторным открытием.</summary>
public sealed class AzurPilotLogViewerSession(LogLevel minimumLevel) : ILoggerProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AzurPilotLogViewerPublisher? _publisher;
    private readonly Func<ApplicationResult<AzurPilotLogViewerPublisher>> _open = () => AzurPilotLogViewerPublisher.Start(minimumLevel);

    internal AzurPilotLogViewerSession(LogLevel minimumLevel,
        Func<ApplicationResult<AzurPilotLogViewerPublisher>> open) : this(minimumLevel) => _open = open;

    /// <summary>Открывает viewer, если прежнее окно уже закрыто; параллельные запросы сериализуются.</summary>
    public async Task<ApplicationResult<bool>> OpenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publisher is { IsConnected: true })
            {
                return ApplicationResult<bool>.Success(true);
            }

            AzurPilotLogViewerPublisher? previous = Interlocked.Exchange(ref _publisher, null);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            ApplicationResult<AzurPilotLogViewerPublisher> opened = await Task.Run(
                _open, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return ApplicationResult<bool>.Failure(opened.FailureInfo!);
            }

            Volatile.Write(ref _publisher, opened.Value!);
            return ApplicationResult<bool>.Success(true);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new SessionLogger(this, categoryName);

    /// <summary>Асинхронным lifetime владеет application host, а не LoggerFactory.</summary>
    public void Dispose() { }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        AzurPilotLogViewerPublisher? publisher = Interlocked.Exchange(ref _publisher, null);
        if (publisher is not null)
        {
            await publisher.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SessionLogger(AzurPilotLogViewerSession session, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Никакого lock или ожидания IPC на потоке production logging.
            Volatile.Read(ref session._publisher)?.Provider.CreateLogger(category)
                .Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
