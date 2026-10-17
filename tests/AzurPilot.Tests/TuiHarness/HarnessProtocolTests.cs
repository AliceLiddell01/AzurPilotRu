using System.IO.Pipes;
using System.Text.Json;
using AzurPilot.TuiHarness;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class HarnessProtocolTests
{
    [Theory]
    [InlineData("--columns", "19")]
    [InlineData("--columns", "241")]
    [InlineData("--rows", "9")]
    [InlineData("--rows", "101")]
    [InlineData("--session", "../outside")]
    [InlineData("--name", "../outside")]
    [InlineData("--timeout-ms", "0")]
    [InlineData("--timeout-ms", "30001")]
    [InlineData("--lifetime-seconds", "901")]
    [InlineData("--unknown", "value")]
    public void RejectsUnsafeProtocolBounds(string option, string value)
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["capture", option, value]));
    }

    [Fact]
    public void RejectsDuplicateAndIncompleteOptions()
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["resize", "--rows", "30", "--rows", "25"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["resize", "--rows"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["unknown"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["text", "--value", new string('x', 4097)]));
    }

    [Fact]
    public void ParseUsesOwnedDefaultBudgets()
    {
        HarnessRequest request = HarnessRequest.Parse(["capture"]);

        Assert.Equal(HarnessTimeouts.DefaultRequestTimeoutMs, request.TimeoutMs);
        Assert.Equal(HarnessTimeouts.DefaultLifetimeSeconds, request.LifetimeSeconds);
    }

    [Fact]
    public void LiveGrantRequiresExplicitExactTargetAndFixedScenario()
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["live-environment"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["live-environment", "--instance", "auto", "--authorize", "prepare-and-restore"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["start", "--instance", "mumu:7", "--authorize", "prepare-and-restore"]));
        HarnessRequest parsed = HarnessRequest.Parse(["live-environment", "--instance", "mumu:7", "--authorize", "prepare-and-restore"]);
        Assert.Equal("mumu:7", parsed.LiveInstance);
    }

    [Fact]
    public async Task IpcPreservesUnicodeAndRejectsOversizedFrames()
    {
        using MemoryStream stream = new();
        HarnessRequest request = new() { Command = "text", Session = new string('a', 32), Value = "Кириллица е\u0301 界" };
        await SessionOwner.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        HarnessRequest roundtrip = await SessionOwner.ReadAsync<HarnessRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(request, roundtrip);
        stream.SetLength(0);
        await stream.WriteAsync(BitConverter.GetBytes(65537), TestContext.Current.CancellationToken);
        stream.Position = 0;
        _ = await Assert.ThrowsAsync<ArgumentException>(() => SessionOwner.ReadAsync<HarnessRequest>(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosedSessionIsBlockedAndDoesNotBecomeSuccess()
    {
        HarnessResponse response;
        try
        {
            response = await AzurPilot.TuiHarness.Program.SendAsync(
                new HarnessRequest { Command = "status", Session = Guid.NewGuid().ToString("N"), TimeoutMs = 100 },
                cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (HarnessBlockedException exception)
        {
            response = SessionOwner.Failure(exception, null);
        }

        Assert.Equal("BLOCKED", response.Status);
        Assert.Equal(2, response.ExitCode);
    }

    [Fact]
    public async Task StartupStatusConnectionCanExceedTheRequestTimeout()
    {
        string session = Guid.NewGuid().ToString("N");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        Task<HarnessRequest> serverTask = Task.Run(async () =>
        {
            await Task.Delay(200, cancellation.Token);
            // Создание канала тоже отложено: ConnectAsync действительно ждёт готовности owner.
            await using NamedPipeServerStream server = new(SessionOwner.PipeName(session), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellation.Token);
            HarnessRequest request = await SessionOwner.ReadAsync<HarnessRequest>(server, cancellation.Token);
            await SessionOwner.WriteAsync(server,
                new HarnessResponse("PASS", session, "Операция выполнена."), cancellation.Token);
            return request;
        }, cancellation.Token);

        HarnessResponse response = await AzurPilot.TuiHarness.Program.SendAsync(
            new HarnessRequest { Command = "status", Session = session, TimeoutMs = 100 }, connectionTimeoutMs: 1000,
            cancellationToken: TestContext.Current.CancellationToken);
        HarnessRequest received = await serverTask;

        Assert.Equal("status", received.Command);
        Assert.Equal("PASS", response.Status);
    }

    [Fact]
    public async Task StartupStatusWaitIsCancelledWhenOwnerExitsBeforeCreatingPipe()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        TaskCompletionSource ownerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HarnessResponse> status = WaitForCancellationAsync(cancellation.Token);
        Task<HarnessResponse> waiting = AzurPilot.TuiHarness.Program.AwaitStartupResponseAsync(
            status, ownerExited.Task, cancellation);
        ownerExited.SetResult();

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains("завершился до готовности канала", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectedOwnerCannotLeaveIpcResponseUnbounded()
    {
        string session = Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(SessionOwner.PipeName(session), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        CancellationToken token = TestContext.Current.CancellationToken;
        Task<HarnessResponse> response = AzurPilot.TuiHarness.Program.SendAsync(
            new HarnessRequest { Command = "status", Session = session, TimeoutMs = 100 }, connectionTimeoutMs: 1000,
            cancellationToken: token);
        await server.WaitForConnectionAsync(token);
        HarnessRequest received = await SessionOwner.ReadAsync<HarnessRequest>(server, token);
        Assert.Equal("status", received.Command);
        // Канал подключён, запрос прочитан, ответа намеренно нет. Проверяем реальный IPC-дедлайн.
        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => response.WaitAsync(TimeSpan.FromSeconds(15), token));
        Assert.Contains("не ответил", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationFailureResponseIsDeliveredOnTheConnectedPipe()
    {
        string session = Guid.NewGuid().ToString("N");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using NamedPipeServerStream server = new(SessionOwner.PipeName(session), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task<HarnessResponse> response = AzurPilot.TuiHarness.Program.SendAsync(
            new HarnessRequest { Command = "status", Session = session, TimeoutMs = 100 }, connectionTimeoutMs: 1000,
            cancellationToken: token);
        await server.WaitForConnectionAsync(token);
        _ = await SessionOwner.ReadAsync<HarnessRequest>(server, token);

        await SessionOwner.WriteResponseAsync(server,
            new HarnessResponse("BLOCKED", session, "Ответ об ограниченной операции."));
        HarnessResponse received = await response.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal("BLOCKED", received.Status);
        Assert.Equal("Ответ об ограниченной операции.", received.Message);
    }

    [Fact]
    public void StartupStatusConnectionBudgetCoversSequentialCreationStages()
    {
        Assert.Equal(HarnessTimeouts.StartConnectionMs,
            HarnessTimeouts.ConsoleProfileBudgetMs + HarnessTimeouts.LivePreflightBudgetMs
            + HarnessTimeouts.RendererCreationBudgetMs + HarnessTimeouts.SourceIdentityBudgetMs
            + HarnessTimeouts.RendererStartupWaitMs + HarnessTimeouts.StartupMarginMs);
        Assert.Equal(HarnessTimeouts.StartConnectionMs, AzurPilot.TuiHarness.Program.StartStatusConnectionTimeoutMs);
    }

    [Theory]
    [InlineData(16, 0x000000)]
    [InlineData(21, 0x0000ff)]
    [InlineData(196, 0xff0000)]
    [InlineData(231, 0xffffff)]
    [InlineData(232, 0x080808)]
    [InlineData(244, 0x808080)]
    [InlineData(255, 0xeeeeee)]
    public void ResolvesExtendedPaletteToExpectedRgb(int index, int rgb)
    {
        JsonElement color = JsonSerializer.SerializeToElement(new { mode = "palette", value = index });
        Assert.Equal(rgb, ColorSamples.Resolve(color, default, "background"));
    }

    private static async Task<HarnessResponse> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HarnessResponse("PASS", null, "Недостижимый результат ожидания отмены.");
    }
}
