using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using AzurPilot.App;
using AzurPilot.App.LogViewer;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.LogViewer;

/// <summary>
/// Проверяет реальный контракт отдельной лог-консоли: канал, handshake, ограниченную очередь,
/// санитизацию недоверенного текста, однонаправленность и семантику форматирования.
/// </summary>
[Trait("Category", "LogViewer")]
public sealed class AzurPilotLogViewerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(400);

    [Fact(DisplayName = "Настоящий процесс viewer читает события по IPC без второго application host и чисто выходит")]
    public async Task RealViewerProcessReceivesEventsAndExitsWithHost()
    {
        using ProcessWindowHost window = new();
        ApplicationResult<AzurPilotLogViewerPublisher> started = AzurPilotLogViewerPublisher.Start(
            window, LogLevel.Information, TimeSpan.FromSeconds(10));
        Assert.True(started.IsSuccess, started.IsFailure ? started.FailureInfo!.Message : null);
        await using AzurPilotLogViewerPublisher publisher = started.Value!;
        Process viewer = window.Process!;
        Task<string> output = viewer.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = viewer.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        publisher.Publish(Event(1));
        publisher.Publish(Event(2) with { Level = LogLevel.Warning });
        publisher.Publish(Event(3) with { Level = LogLevel.Error });
        await publisher.DisposeAsync();
        await viewer.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string rendered = await output;
        Assert.Equal(AzurPilotExitCode.Success, viewer.ExitCode);
        Assert.Empty(await error);
        Assert.Contains("сообщение-1", rendered, StringComparison.Ordinal);
        Assert.Contains("WARN", rendered, StringComparison.Ordinal);
        Assert.Contains("ERROR", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Native boundary", rendered, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Маскирование защищает сообщения и structured properties от утечки секретов")]
    public void SecretsAreMaskedBeforeTheyReachIpc()
    {
        AzurPilotLogViewerEvent bounded = AzurPilotLogViewerProtocol.Bound(new AzurPilotLogViewerEvent(
            DateTimeOffset.UnixEpoch, LogLevel.Warning, "test", "Authorization: Bearer sensitive-value",
            Properties: [new("ApiKey", "secret-value"), new("InstanceId", "mumu:7")]));
        string wire = AzurPilotLogViewerProtocol.Serialize(bounded);
        Assert.DoesNotContain("sensitive-value", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", wire, StringComparison.Ordinal);
        Assert.Contains("mumu:7", wire, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Предельно длинная Unicode запись помещается в wire limit и не ломает surrogate pair")]
    public void MaximumUnicodeRecordFitsTheWireLimit()
    {
        AzurPilotLogViewerEvent bounded = AzurPilotLogViewerProtocol.Bound(new AzurPilotLogViewerEvent(
            DateTimeOffset.UnixEpoch, LogLevel.Error, new string('я', 400), new string('я', 8000),
            new string('я', 200), new string('я', 200),
            [.. Enumerable.Range(0, 8).Select(_ => new KeyValuePair<string, string>(new string('я', 70), new string('я', 300)))]));
        Assert.True(AzurPilotLogViewerProtocol.EncodeLine(AzurPilotLogViewerProtocol.Serialize(bounded)).Length
            <= AzurPilotLogViewerProtocol.MaximumLineBytes);
        Assert.Equal("ab…", AzurPilotLogViewerProtocol.Sanitize("ab😀x", 4));
    }

    [Fact(DisplayName = "Редактирование секретов не превышает пределы полей и строки канала")]
    public void RedactionExpansionIsBoundedBeforeSerialization()
    {
        string repeatedBearer = string.Concat(Enumerable.Repeat("Bearer a ", 512));
        AzurPilotLogViewerEvent bounded = AzurPilotLogViewerProtocol.Bound(new AzurPilotLogViewerEvent(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            new string('я', 400),
            repeatedBearer,
            Properties: [.. Enumerable.Range(0, 8).Select(index =>
                new KeyValuePair<string, string>($"property-{index}", repeatedBearer))]));
        string line = AzurPilotLogViewerProtocol.Serialize(bounded);

        Assert.True(bounded.Message.Length <= AzurPilotLogViewerProtocol.MaximumMessageLength);
        Assert.All(
            bounded.Properties!,
            property => Assert.True(property.Value.Length <= AzurPilotLogViewerProtocol.MaximumPropertyValueLength));
        Assert.True(AzurPilotLogViewerProtocol.EncodeLine(line).Length <= AzurPilotLogViewerProtocol.MaximumLineBytes);
        Assert.True(AzurPilotLogViewerProtocol.TryDeserialize(line, out _));
        Assert.DoesNotContain("Bearer a", line, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Повторное открытие не дублирует живое окно и восстанавливает поток сразу после disconnect")]
    [InlineData(1)]
    [InlineData(16)]
    public async Task SessionReopensOnlyAfterDisconnect(int reconnects)
    {
        List<LogViewerStand> opened = [];
        await using AzurPilotLogViewerSession session = new(LogLevel.Information, () =>
        {
            LogViewerStand stand = LogViewerStand.Start(handshake: true);
            opened.Add(stand);
            return ApplicationResult<AzurPilotLogViewerPublisher>.Success(stand.Publisher);
        });
        Assert.True((await session.OpenAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await session.OpenAsync(TestContext.Current.CancellationToken)).IsSuccess);
        _ = Assert.Single(opened);
        for (int index = 0; index < reconnects; index++)
        {
            await opened[^1].Viewer.DisconnectAsync();
            Assert.True(await opened[^1].Viewer.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
            Assert.True((await session.OpenAsync(TestContext.Current.CancellationToken)).IsSuccess);
            Assert.Equal(index + 2, opened.Count);
        }

        session.CreateLogger("AzurPilot.Tests").Log(LogLevel.Information, new EventId(1),
            "Журнал восстановлен", null, static (state, _) => state);
        Assert.Equal("Журнал восстановлен", (await opened[^1].Viewer.WaitForCountAsync(1))[0].Message);
        foreach (LogViewerStand stand in opened)
        {
            await stand.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Несколько событий доходят по реальному каналу в исходном порядке")]
    public async Task EventsArriveInOrderOverRealPipe()
    {
        await using LogViewerStand stand = LogViewerStand.Start(handshake: true);

        for (int index = 0; index < 5; index++)
        {
            stand.Publisher.Publish(Event(index));
        }

        IReadOnlyList<AzurPilotLogViewerEvent> received = await stand.Viewer.WaitForCountAsync(5);
        Assert.Equal(
            ["сообщение-0", "сообщение-1", "сообщение-2", "сообщение-3", "сообщение-4"],
            received.Select(item => item.Message));
        Assert.Equal(0, stand.Publisher.DroppedCount);
    }

    [Fact(DisplayName = "Санитизация снимает ANSI/control и ограничивает длину полей")]
    public async Task UntrustedTextIsSanitizedAndBounded()
    {
        await using LogViewerStand stand = LogViewerStand.Start(handshake: true);

        stand.Publisher.Publish(new AzurPilotLogViewerEvent(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            "AzurPilot\u001b[31m.Tests",
            "текст\u001b[2J\u0007с последовательностями " + new string('я', 9000)));

        AzurPilotLogViewerEvent received = (await stand.Viewer.WaitForCountAsync(1))[0];

        Assert.DoesNotContain('\u001b', received.Message);
        Assert.DoesNotContain('\u001b', received.Category);
        Assert.DoesNotContain('\u0007', received.Message);
        Assert.True(received.Message.Length <= 2048);
        Assert.True(received.Category.Length <= 160);
    }

    [Fact(DisplayName = "Ограниченная очередь отбрасывает избыток вместо неограниченного роста")]
    public async Task BoundedQueueDropsExcess()
    {
        // Клиент подтверждает канал, но не читает: очередь publisher-а гарантированно переполняется.
        await using LogViewerStand stand = LogViewerStand.Start(handshake: true, read: false);

        for (int index = 0; index < 5000; index++)
        {
            stand.Publisher.Publish(Event(index));
        }

        Assert.True(stand.Publisher.DroppedCount > 0);
    }

    [Fact(DisplayName = "Публикация не блокирует вызывающий поток при медленном читателе")]
    public async Task PublishDoesNotBlockOnSlowReader()
    {
        await using LogViewerStand stand = LogViewerStand.Start(handshake: true, read: false);

        Task publish = Task.Run(() =>
        {
            for (int index = 0; index < 5000; index++)
            {
                stand.Publisher.Publish(Event(index));
            }
        }, TestContext.Current.CancellationToken);

        Task finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(publish, finished);
    }

    [Fact(DisplayName = "Клиент без handshake не считается подключённым")]
    public void MissingHandshakeFailsStart()
    {
        ApplicationResult<AzurPilotLogViewerPublisher> result =
            LogViewerStand.TryStart(handshake: false, ShortTimeout);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.LogViewerUnavailable, result.FailureInfo!.Code);
    }

    [Fact(DisplayName = "Недоступное отдельное окно — ожидаемый отказ, а не исключение")]
    public void UnavailableWindowHostReturnsFailure()
    {
        ApplicationResult<AzurPilotLogViewerPublisher> result = AzurPilotLogViewerPublisher.Start(
            new UnavailableWindowHost(),
            LogLevel.Information,
            ShortTimeout);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.LogViewerUnavailable, result.FailureInfo!.Code);
        Assert.False(result.FailureInfo.IsRetryable);
    }

    [Fact(DisplayName = "Обрыв канала не ломает publisher, а Dispose идемпотентен")]
    public async Task ChannelBreakKeepsPublisherAliveAndDisposeIsIdempotent()
    {
        LogViewerStand stand = LogViewerStand.Start(handshake: true);
        await stand.Viewer.DisconnectAsync();

        // Клиент ушёл: публикация остаётся безопасной и не бросает.
        stand.Publisher.Publish(Event(1));
        stand.Publisher.Publish(Event(2));

        await stand.Publisher.DisposeAsync();
        await stand.Publisher.DisposeAsync();
        await stand.DisposeAsync();
    }

    [Fact(DisplayName = "Сообщение клиента после handshake не влияет на состояние publisher-а")]
    public async Task ClientMessageAfterHandshakeIsIgnored()
    {
        await using LogViewerStand stand = LogViewerStand.Start(handshake: true);

        // Протокол однонаправленный: единственное сообщение viewer-а — handshake. Попытка «управлять»
        // host-ом через канал не имеет эффекта и не мешает публикации событий.
        await stand.Viewer.SendAsync("{\"command\":\"stop-mumu\"}");

        stand.Publisher.Publish(Event(42));
        AzurPilotLogViewerEvent received = (await stand.Viewer.WaitForCountAsync(1))[0];

        Assert.Equal("сообщение-42", received.Message);
    }

    [Fact(DisplayName = "DisposeAsync завершает канал и не оставляет висящего writer-а")]
    public async Task DisposeAsyncCompletesChannel()
    {
        LogViewerStand stand = LogViewerStand.Start(handshake: true);
        stand.Publisher.Publish(Event(1));
        _ = await stand.Viewer.WaitForCountAsync(1);

        await stand.Publisher.DisposeAsync();

        // После освобождения канал закрыт: читающий цикл клиента видит конец потока.
        Assert.True(await stand.Viewer.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        await stand.DisposeAsync();
    }

    [Fact(DisplayName = "Форматирование несёт семантику уровня и подсвечивает ключевые значения")]
    public void FormatEventCarriesLevelSemanticsAndHighlightsValues()
    {
        AzurPilotLogViewerEvent logEvent = new(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            "AzurPilot.Core.MuMu",
            "Состояние не доказано",
            CorrelationId: "0af7651916cd43dd8448eb211c80319c",
            Properties: [new KeyValuePair<string, string>("InstanceId", "mumu:1")]);

        string markup = AzurPilotLogViewerHost.FormatEvent(logEvent, includeDetails: true);

        Assert.Contains("[yellow]", markup, StringComparison.Ordinal);
        Assert.Contains("WARN", markup, StringComparison.Ordinal);
        Assert.Contains("AzurPilot.Core.MuMu", markup, StringComparison.Ordinal);
        Assert.Contains("0af7651916cd43dd8448eb211c80319c", markup, StringComparison.Ordinal);
        Assert.Contains("InstanceId=mumu:1", markup, StringComparison.Ordinal);
        string compact = AzurPilotLogViewerHost.FormatEvent(logEvent);
        Assert.DoesNotContain("0af7651916cd43dd8448eb211c80319c", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("InstanceId=", compact, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Уровень отображается ожидаемым цветом")]
    [InlineData(LogLevel.Trace, "grey")]
    [InlineData(LogLevel.Debug, "grey")]
    [InlineData(LogLevel.Information, "default")]
    [InlineData(LogLevel.Warning, "yellow")]
    [InlineData(LogLevel.Error, "red")]
    [InlineData(LogLevel.Critical, "red")]
    public void LevelColorIsSemantic(LogLevel level, string expectedColor)
    {
        Assert.Equal(expectedColor, AzurPilotLogViewerHost.LevelColor(level));
    }

    [Fact(DisplayName = "ANSI из текста записи не попадает в разметку отдельного окна")]
    public void AnsiFromLogTextNeverReachesMarkup()
    {
        AzurPilotLogViewerEvent logEvent = new(
            DateTimeOffset.UnixEpoch,
            LogLevel.Error,
            "AzurPilot.Tests",
            "текст\u001b[2J\u001b[31mс последовательностями");

        string markup = AzurPilotLogViewerHost.FormatEvent(logEvent);

        Assert.DoesNotContain('\u001b', markup);
        // Квадратная скобка экранирована: остаток escape-последовательности не станет разметкой.
        Assert.Contains("[[2J", markup, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Разбор аргументов viewer-а отклоняет неполный и неизвестный ввод")]
    public async Task ViewerArgumentsAreStrict()
    {
        Assert.Equal(AzurPilotExitCode.InvalidArgument, await AzurPilotLogViewerHost.RunAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(
            AzurPilotExitCode.InvalidArgument,
            await AzurPilotLogViewerHost.RunAsync([AzurPilotLogViewerHost.CommandLineSwitch], TestContext.Current.CancellationToken));
        Assert.Equal(
            AzurPilotExitCode.InvalidArgument,
            await AzurPilotLogViewerHost.RunAsync(
                [AzurPilotLogViewerHost.CommandLineSwitch, "--log-viewer-pipe", "bad/name"], TestContext.Current.CancellationToken));
        Assert.Equal(
            AzurPilotExitCode.InvalidArgument,
            await AzurPilotLogViewerHost.RunAsync(
                [AzurPilotLogViewerHost.CommandLineSwitch, "--log-viewer-pipe", "ok", "--log-viewer-level", "Loud"], TestContext.Current.CancellationToken));
    }

    private static AzurPilotLogViewerEvent Event(int index) => new(
        DateTimeOffset.UnixEpoch.AddSeconds(index),
        LogLevel.Information,
        "AzurPilot.Tests",
        "сообщение-" + index);

    /// <summary>Отдельное окно, недоступное в среде.</summary>
    private sealed class UnavailableWindowHost : ILogViewerWindowHost
    {
        public bool IsAvailable => false;

        public bool TryOpen(string executablePath, IReadOnlyList<string> arguments) => false;
    }

    private sealed class ProcessWindowHost : ILogViewerWindowHost, IDisposable
    {
        internal Process? Process { get; private set; }
        public bool IsAvailable => true;

        public bool TryOpen(string executablePath, IReadOnlyList<string> arguments)
        {
            ProcessStartInfo start = new(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process = System.Diagnostics.Process.Start(start);
            return Process is not null;
        }

        public void Dispose()
        {
            if (Process is not null)
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                }

                Process.Dispose();
            }
        }
    }

    /// <summary>Отдельное окно, которое подключает клиента канала в том же процессе.</summary>
    private sealed class InProcessWindowHost : ILogViewerWindowHost
    {
        private readonly InProcessViewer _viewer;

        internal InProcessWindowHost(InProcessViewer viewer) => _viewer = viewer;

        public bool IsAvailable => true;

        public bool TryOpen(string executablePath, IReadOnlyList<string> arguments)
        {
            string? pipeName = null;
            for (int index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] == "--log-viewer-pipe")
                {
                    pipeName = arguments[index + 1];
                    break;
                }
            }

            Assert.False(string.IsNullOrWhiteSpace(pipeName), "Имя канала обязано передаваться viewer-у.");
            _viewer.Start(pipeName);
            return true;
        }
    }

    /// <summary>Клиент канала: подключается, при необходимости подтверждает готовность и читает записи.</summary>
    private sealed class InProcessViewer
    {
        private readonly ConcurrentQueue<AzurPilotLogViewerEvent> _received = new();
        private readonly bool _handshake;
        private readonly bool _read;
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NamedPipeClientStream? _client;

        internal InProcessViewer(bool handshake, bool read)
        {
            _handshake = handshake;
            _read = read;
        }

        internal void Start(string pipeName) => _ = Task.Run(() => RunAsync(pipeName));

        internal async Task<IReadOnlyList<AzurPilotLogViewerEvent>> WaitForCountAsync(int count)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (_received.Count < count && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(_received.Count >= count, $"Ожидалось записей: {count}, получено: {_received.Count}.");
            return [.. _received];
        }

        internal async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
            => await Task.WhenAny(_completed.Task, Task.Delay(timeout)) == _completed.Task;

        internal async Task SendAsync(string line)
        {
            NamedPipeClientStream client = _client ?? throw new InvalidOperationException("Канал ещё не подключён.");
            await client.WriteAsync(AzurPilotLogViewerProtocol.EncodeLine(line));
            await client.FlushAsync();
        }

        internal async Task DisconnectAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }

        private async Task RunAsync(string pipeName)
        {
            try
            {
                NamedPipeClientStream client = new(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _client = client;
                await client.ConnectAsync();
                if (_handshake)
                {
                    await client.WriteAsync(
                        AzurPilotLogViewerProtocol.EncodeLine(AzurPilotLogViewerProtocol.HandshakeLine));
                    await client.FlushAsync();
                }

                if (!_read)
                {
                    // Клиент намеренно не читает: так проверяется политика переполнения очереди.
                    return;
                }

                AzurPilotLogViewerLineReader reader = new(
                    client,
                    AzurPilotLogViewerProtocol.MaximumLineBytes);
                while (true)
                {
                    string? line = await reader.ReadLineAsync(CancellationToken.None);
                    if (line is null)
                    {
                        break;
                    }

                    if (AzurPilotLogViewerProtocol.TryDeserialize(line, out AzurPilotLogViewerEvent? logEvent))
                    {
                        _received.Enqueue(logEvent);
                    }
                }
            }
            catch (IOException)
            {
                // Обрыв канала — ожидаемое завершение клиента.
            }
            catch (ObjectDisposedException)
            {
                // Канал закрыт тестом.
            }
            finally
            {
                _ = _completed.TrySetResult();
            }
        }
    }

    /// <summary>Стенд: реальный publisher и подключённый к нему клиент канала.</summary>
    private sealed class LogViewerStand : IAsyncDisposable
    {
        private LogViewerStand(AzurPilotLogViewerPublisher publisher, InProcessViewer viewer)
        {
            Publisher = publisher;
            Viewer = viewer;
        }

        internal AzurPilotLogViewerPublisher Publisher { get; }

        internal InProcessViewer Viewer { get; }

        internal static LogViewerStand Start(bool handshake, bool read = true)
        {
            (ApplicationResult<AzurPilotLogViewerPublisher> result, InProcessViewer viewer) =
                TryStartCore(handshake, TimeSpan.FromSeconds(10), read);
            Assert.True(result.IsSuccess, result.ToString());
            return new LogViewerStand(result.Value!, viewer);
        }

        internal static ApplicationResult<AzurPilotLogViewerPublisher> TryStart(
            bool handshake,
            TimeSpan timeout,
            bool read = true) => TryStartCore(handshake, timeout, read).Result;

        private static (ApplicationResult<AzurPilotLogViewerPublisher> Result, InProcessViewer Viewer) TryStartCore(
            bool handshake,
            TimeSpan timeout,
            bool read)
        {
            InProcessViewer viewer = new(handshake, read);
            ApplicationResult<AzurPilotLogViewerPublisher> result = AzurPilotLogViewerPublisher.Start(
                new InProcessWindowHost(viewer),
                LogLevel.Information,
                timeout);
            return (result, viewer);
        }

        public async ValueTask DisposeAsync()
        {
            await Viewer.DisconnectAsync();
            await Publisher.DisposeAsync();
        }
    }
}
