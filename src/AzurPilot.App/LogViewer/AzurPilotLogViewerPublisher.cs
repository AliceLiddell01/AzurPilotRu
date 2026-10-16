using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Threading.Channels;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.LogViewer;

/// <summary>
/// Сторона хоста приложения: публикует структурированные события журнала в отдельный процесс viewer-а.
/// </summary>
/// <remarks>
/// <para>
/// Этот потребитель журнала не управляет приложением: publisher только отправляет события и не
/// принимает ни одной команды управления. Канал однонаправленный, ограничен текущим пользователем, а
/// публикация никогда не блокирует вызывающий поток — при переполнении записи отбрасываются со
/// счётчиком, который затем сообщается viewer-у отдельной записью.
/// </para>
/// <para>
/// Отказ открытия окна не является отказом запуска: интерактивный экран продолжает работать, а журнал
/// не перенаправляется на него.
/// </para>
/// </remarks>
public sealed class AzurPilotLogViewerPublisher : IAsyncDisposable
{
    /// <summary>Предел очереди: медленный viewer не растит память процесса безгранично.</summary>
    private const int QueueCapacity = 1024;

    /// <summary>Сколько ждать подключения viewer-а и его handshake.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Сколько ждать дозаписи очереди при штатном завершении.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly NamedPipeServerStream _server;
    private readonly Channel<AzurPilotLogViewerEvent> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly Task _writer;
    private readonly Task _clientReader;
    private readonly AzurPilotLogViewerProvider _provider;
    private long _droppedCount;
    private long _totalDroppedCount;
    private int _disposed;

    private AzurPilotLogViewerPublisher(NamedPipeServerStream server)
    {
        _server = server;
        _queue = Channel.CreateBounded<AzurPilotLogViewerEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            // TryWrite в режиме ожидания возвращает false вместо блокировки: переполнение наблюдаемо,
            // а вызывающий поток не останавливается на медленном читателе.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _provider = new AzurPilotLogViewerProvider(this);
        _writer = Task.Run(() => WriteLoopAsync(_shutdown.Token), CancellationToken.None);
        _clientReader = Task.Run(() => DiscardClientInputAsync(_shutdown.Token), CancellationToken.None);
    }

    /// <summary>Провайдер логирования для подключения к системе логирования хоста приложения.</summary>
    public ILoggerProvider Provider => _provider;

    /// <summary>Живы отправитель и читатель текущего канала.</summary>
    internal bool IsConnected => !_writer.IsCompleted && !_clientReader.IsCompleted && _server.IsConnected;

    /// <summary>Проверяет закрытие читателя реальным I/O, а не кэшированным флагом Named Pipe.</summary>
    internal async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            return false;
        }

        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await _outputGate.WaitAsync(probe.Token).ConfigureAwait(false);
            try
            {
                // Пустую запись viewer игнорирует. Общий семафор не даёт ему разорвать JSON-событие;
                // запись в закрытый канал обнаруживает EOF даже до завершения асинхронного чтения.
                await _server.WriteAsync(AzurPilotLogViewerProtocol.EncodeLine(string.Empty), probe.Token).ConfigureAwait(false);
                await _server.FlushAsync(probe.Token).ConfigureAwait(false);
                return IsConnected;
            }
            finally
            {
                _ = _outputGate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Тайм-аут занятой записи не доказывает закрытие подключённого читателя.
            return IsConnected;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Число событий, отброшенных из-за переполнения очереди.</summary>
    internal long DroppedCount => Interlocked.Read(ref _totalDroppedCount);

    /// <summary>Открывает отдельное окно viewer-а и канал к нему.</summary>
    /// <param name="viewerLevel">Минимальный уровень, который должен показывать viewer.</param>
    /// <returns>Готовый publisher либо ожидаемый отказ <c>log_viewer_unavailable</c>.</returns>
    public static ApplicationResult<AzurPilotLogViewerPublisher> Start(LogLevel? viewerLevel = null)
        => Start(WindowsTerminalWindowHost.Instance, viewerLevel, HandshakeTimeout);

    /// <summary>Открывает канал через явно заданную границу окна: тестируемый seam.</summary>
    /// <param name="windowHost">Граница запуска отдельного окна.</param>
    /// <param name="viewerLevel">Минимальный уровень, который должен показывать viewer.</param>
    /// <param name="handshakeTimeout">Предел ожидания подключения и handshake.</param>
    /// <returns>Готовый publisher либо ожидаемый отказ <c>log_viewer_unavailable</c>.</returns>
    internal static ApplicationResult<AzurPilotLogViewerPublisher> Start(
        ILogViewerWindowHost windowHost,
        LogLevel? viewerLevel,
        TimeSpan handshakeTimeout)
    {
        ArgumentNullException.ThrowIfNull(windowHost);
        if (!windowHost.IsAvailable)
        {
            return Unavailable("Отдельное окно Windows Terminal недоступно: wt.exe не найден в PATH.");
        }

        string executablePath = Path.ChangeExtension(typeof(AzurPilotHost).Assembly.Location, ".exe");
        if (!File.Exists(executablePath))
        {
            return Unavailable("Путь исполняемого файла приложения не определён: отдельная консоль не открыта.");
        }

        string pipeName = AzurPilotLogViewerProtocol.CreatePipeName();
        NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: 64 * 1024);

        using CancellationTokenSource handshake = new();
        try
        {
            Task connection = server.WaitForConnectionAsync(handshake.Token);
            if (!windowHost.TryOpen(executablePath, BuildViewerArguments(pipeName, viewerLevel)))
            {
                server.Dispose();
                return Unavailable("Отдельное окно Windows Terminal не удалось открыть: команда запуска отклонена.");
            }

            if (!connection.Wait(handshakeTimeout))
            {
                server.Dispose();
                return Unavailable(
                    "Отдельная лог-консоль не подключилась за отведённое время: журнал остаётся только в диагностике.");
            }

            if (!TryReadHandshake(server, handshakeTimeout))
            {
                server.Dispose();
                return Unavailable("Отдельная лог-консоль не подтвердила готовность канала.");
            }

            // Успешный путь владеет каналом сам: CTS ожидания здесь больше не нужен, а его token не
            // передаётся writer-у — иначе завершение ожидания отменяло бы уже установленный канал.
            return ApplicationResult<AzurPilotLogViewerPublisher>.Success(
                new AzurPilotLogViewerPublisher(server));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            server.Dispose();
            return Unavailable("Канал отдельной лог-консоли не открылся: " + exception.Message);
        }
        catch (AggregateException exception)
        {
            server.Dispose();
            return Unavailable("Канал отдельной лог-консоли не открылся: " + exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Публикует событие журнала, не блокируя вызывающий поток.
    /// </summary>
    /// <param name="logEvent">Событие хоста приложения.</param>
    /// <remarks>
    /// Поля приводятся к пределам канала и санитизируются до постановки в очередь, поэтому недоверенный
    /// текст не доходит до чужого терминала даже при мгновенном чтении.
    /// </remarks>
    public void Publish(AzurPilotLogViewerEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(AzurPilotLogViewerProtocol.Bound(logEvent)))
        {
            _ = Interlocked.Increment(ref _droppedCount);
            _ = Interlocked.Increment(ref _totalDroppedCount);
        }
    }

    /// <summary>Завершает канал, дожидаясь дозаписи очереди, и освобождает ресурсы.</summary>
    /// <returns>Завершение операции освобождения.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = _queue.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Медленный или зависший viewer не должен задерживать выход приложения.
        }
        catch (OperationCanceledException)
        {
            // Отмена уже означает завершение записи.
        }

        _shutdown.Cancel();
        await _writer.ConfigureAwait(false);
        await _clientReader.ConfigureAwait(false);
        _provider.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
        _outputGate.Dispose();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private static List<string> BuildViewerArguments(string pipeName, LogLevel? viewerLevel)
    {
        List<string> arguments =
        [
            AzurPilotLogViewerHost.CommandLineSwitch,
            "--log-viewer-pipe",
            pipeName,
        ];
        if (viewerLevel is LogLevel level)
        {
            arguments.Add("--log-viewer-level");
            arguments.Add(level.ToString("G"));
        }

        return arguments;
    }

    private static bool TryReadHandshake(NamedPipeServerStream server, TimeSpan timeout)
    {
        AzurPilotLogViewerLineReader reader = new(server, AzurPilotLogViewerProtocol.MaximumLineBytes);
        Task<string?> read = reader.ReadLineAsync(CancellationToken.None);
        return read.Wait(timeout)
            && string.Equals(read.Result, AzurPilotLogViewerProtocol.HandshakeLine, StringComparison.Ordinal);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AzurPilotLogViewerEvent item in _queue.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ReportDroppedAsync(cancellationToken).ConfigureAwait(false);
                    byte[] payload = AzurPilotLogViewerProtocol.EncodeLine(
                        AzurPilotLogViewerProtocol.Serialize(item));
                    await _server.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await _server.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ = _outputGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение: канал закрывается вместе с приложением.
        }
        catch (IOException)
        {
            // Обрыв канала — ожидаемое состояние: viewer мог быть закрыт пользователем.
        }
        catch (ObjectDisposedException)
        {
            // Канал уже освобождён: писать больше некуда.
        }
    }

    private async Task DiscardClientInputAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        int received = 0;
        try
        {
            while (await _server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is int count && count != 0)
            {
                received += count;
                if (received > AzurPilotLogViewerProtocol.MaximumLineBytes)
                {
                    // Никакие команды после handshake не разбираются. Заливающий вход клиент
                    // отключается; хост приложения и состояние приложения не затрагиваются.
                    _server.Disconnect();
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Закрытие читателя не является отказом хоста приложения.
        }
    }

    private async Task ReportDroppedAsync(CancellationToken cancellationToken)
    {
        long dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped == 0)
        {
            return;
        }

        AzurPilotLogViewerEvent notice = new(
            DateTimeOffset.UtcNow,
            LogLevel.Warning,
            "AzurPilot.LogViewer",
            $"Отдельная консоль не успевала читать журнал: пропущено записей — {dropped}.");
        byte[] payload = AzurPilotLogViewerProtocol.EncodeLine(AzurPilotLogViewerProtocol.Serialize(notice));
        await _server.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _server.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ApplicationResult<AzurPilotLogViewerPublisher> Unavailable(string message)
        => ApplicationResult<AzurPilotLogViewerPublisher>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.LogViewerUnavailable,
            Message = message,
            IsRetryable = false,
        });

    /// <summary>Провайдер логирования: превращает записи логгера в события канала.</summary>
    private sealed class AzurPilotLogViewerProvider : ILoggerProvider
    {
        private readonly AzurPilotLogViewerPublisher _publisher;

        internal AzurPilotLogViewerProvider(AzurPilotLogViewerPublisher publisher) => _publisher = publisher;

        public ILogger CreateLogger(string categoryName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            return new AzurPilotLogViewerLogger(_publisher, categoryName);
        }

        /// <summary>
        /// Освобождение канала принадлежит publisher-у: провайдер не владеет ни каналом, ни очередью,
        /// поэтому отдельного состояния «провайдер освобождён» здесь не заводится.
        /// </summary>
        public void Dispose()
        {
        }

        private sealed class AzurPilotLogViewerLogger : ILogger
        {
            private readonly AzurPilotLogViewerPublisher _publisher;
            private readonly string _category;

            internal AzurPilotLogViewerLogger(AzurPilotLogViewerPublisher publisher, string category)
            {
                _publisher = publisher;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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
                    message = message.Length == 0
                        ? exception.Message
                        : message + " " + exception.Message;
                }

                _publisher.Publish(new AzurPilotLogViewerEvent(
                    DateTimeOffset.UtcNow,
                    logLevel,
                    _category,
                    message,
                    eventId.Id == 0 ? null : eventId.Id.ToString(CultureInfo.InvariantCulture),
                    ReadCorrelationId(state),
                    ReadProperties(state)));
            }

            private static string? ReadCorrelationId<TState>(TState state)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (KeyValuePair<string, object?> pair in values)
                    {
                        if (pair.Key == AzurPilotLogViewerProtocol.CorrelationPropertyName)
                        {
                            return Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                        }
                    }
                }

                Activity? activity = Activity.Current;
                return activity is null ? null : activity.TraceId.ToHexString();
            }

            private static List<KeyValuePair<string, string>>? ReadProperties<TState>(TState state)
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    return null;
                }

                List<KeyValuePair<string, string>>? properties = null;
                foreach (KeyValuePair<string, object?> pair in values)
                {
                    // Шаблон сообщения и correlation identifier не дублируются среди прочих свойств:
                    // первый не является данными записи, второй показывается отдельно.
                    if (pair.Key is AzurPilotLogViewerProtocol.OriginalFormatPropertyName
                        or AzurPilotLogViewerProtocol.CorrelationPropertyName)
                    {
                        continue;
                    }

                    string? text = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    properties ??= [];
                    properties.Add(new KeyValuePair<string, string>(pair.Key, text));
                }

                return properties;
            }
        }
    }
}
