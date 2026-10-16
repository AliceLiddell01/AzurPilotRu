using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.LogViewer;

/// <summary>Владеет единственным подключением viewer-а и его явным повторным открытием.</summary>
public sealed class AzurPilotLogViewerSession(LogLevel minimumLevel) : ILoggerProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AzurPilotLogViewerPublisher? _publisher;
    private bool _disposed;
    private readonly Func<ApplicationResult<AzurPilotLogViewerPublisher>> _open = () => AzurPilotLogViewerPublisher.Start(minimumLevel);

    internal AzurPilotLogViewerSession(LogLevel minimumLevel,
        Func<ApplicationResult<AzurPilotLogViewerPublisher>> open) : this(minimumLevel) => _open = open;

    /// <summary>Открывает viewer, если прежнее окно уже закрыто; параллельные запросы сериализуются.</summary>
    public async Task<ApplicationResult<bool>> OpenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_publisher is AzurPilotLogViewerPublisher existing
                && await existing.CheckConnectionAsync(cancellationToken).ConfigureAwait(false))
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

    /// <summary>Асинхронным временем жизни владеет хост приложения, а не LoggerFactory.</summary>
    public void Dispose() { }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AzurPilotLogViewerPublisher? publisher = Interlocked.Exchange(ref _publisher, null);
            if (publisher is not null)
            {
                await publisher.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }

        // Ожидающие OpenAsync должны получить проверку _disposed, а не освобождённый семафор.
        // WaitHandle не создаётся; управляемый SemaphoreSlim собирается вместе с сессией.
        GC.SuppressFinalize(this);
    }

    private sealed class SessionLogger(AzurPilotLogViewerSession session, string category) : ILogger
    {
        private LoggerBinding? _binding;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Логирование не ждёт IPC; кэш привязан к конкретному подключению.
            AzurPilotLogViewerPublisher? publisher = Volatile.Read(ref session._publisher);
            if (publisher is null)
            {
                return;
            }

            LoggerBinding? binding = Volatile.Read(ref _binding);
            if (binding is null || !ReferenceEquals(binding.Publisher, publisher))
            {
                binding = new LoggerBinding(publisher, publisher.Provider.CreateLogger(category));
                Volatile.Write(ref _binding, binding);
            }

            binding.Logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed record LoggerBinding(AzurPilotLogViewerPublisher Publisher, ILogger Logger);
}
