using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Tests.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Game;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Fact(DisplayName = "Оператор возвращает ApplicationResult при native отказе игрового UI")]
    public async Task GameUiNativeBoundaryFailureIsMappedAtOperatorBoundary()
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        NativeBoundaryUnavailableException exception = new("Native библиотека AzurPilot.Native недоступна в тесте.");
        OperatorFixture fixture = CreateFixtureWithGameUi(
            new ThrowingGameUiHost(exception), MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));

        ApplicationResult<GameUiObservation> result = await fixture.Service.ObserveGameUiAsync(
            selected, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.NativeUnavailable, result.FailureInfo!.Code);

        OperatorFixture roundtripFixture = CreateFixtureWithGameUi(
            new ThrowingGameUiHost(exception), MuMuLifecycleTestContext.Instance("7"));
        roundtripFixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        roundtripFixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        QueueReadyAndroid(roundtripFixture);

        var roundtrip = await roundtripFixture.Service.RunGameDockRoundtripAsync(
            selected, new GameActionProgress(), CancellationToken.None);

        Assert.True(roundtrip.IsFailure);
        Assert.Equal(ApplicationFailure.NativeUnavailable, roundtrip.FailureInfo!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentDockRoundtripUsesProductionCaptureMatcherInputAndOneLease(bool absent)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        OperatorFixture scripted = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        scripted.ScreenshotHost.OnCapture = () =>
        {
            ApplicationResult<FileStream> competing = WindowsInstanceMutationLease.TryAcquire(selected);
            using FileStream? unexpected = competing.IsSuccess ? competing.Value : null;
            Assert.True(competing.IsFailure);
            Assert.Equal(ApplicationFailure.TargetBusy, competing.FailureInfo!.Code);
            Assert.Contains(scripted.Runner.Requests, r => r.Request.Arguments.Contains("sys.boot_completed", StringComparer.Ordinal));
        };
        scripted.Runner.OnRun = request =>
        {
            if (request.Arguments.Contains("get-state", StringComparer.Ordinal)
                || request.Arguments.Contains("connect", StringComparer.Ordinal)
                || request.Arguments.Contains("getprop", StringComparer.Ordinal))
            {
                var competing = WindowsInstanceMutationLease.TryAcquire(selected);
                using FileStream? unexpected = competing.IsSuccess ? competing.Value : null;
                Assert.Equal(ApplicationFailure.TargetBusy, competing.FailureInfo!.Code);
                Assert.Equal(0, scripted.ScreenshotHost.CaptureCount);
            }
        };
        // Явная последовательность команд оставляет fake process границу независимой от алгоритма.
        scripted.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        scripted.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        if (absent)
        {
            scripted.Runner.EnqueueOutcome(1, "", $"error: device '{endpoint}' not found");
            scripted.Runner.EnqueueOutcome(0, "connected");
        }

        QueueReadyAndroid(scripted);
        QueueGameCapture(scripted, "main");
        scripted.Runner.EnqueueOutcome(0, "");
        QueueGameCapture(scripted, "dock");
        // Кадр-доказательство открытия служит исходным кадром возврата; viewport перед вводом проверяется заново.
        QueueViewport(scripted);
        scripted.Runner.EnqueueOutcome(0, "");
        QueueGameCapture(scripted, "main");

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(scripted.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        Assert.Equal(3, scripted.ScreenshotHost.CaptureCount);
        Assert.Equal(2, scripted.Runner.Requests.Count(r => r.Request.Arguments.Contains("tap", StringComparer.Ordinal)));
        Assert.All(scripted.Runner.Requests.Skip(2), r =>
        {
            if (r.Request.Arguments[0] == "connect")
            {
                Assert.Equal(["connect", endpoint.ToString()], r.Request.Arguments);
                return;
            }

            Assert.Equal("-s", r.Request.Arguments[0]);
            Assert.Equal(endpoint.ToString(), r.Request.Arguments[1]);
        });
        Assert.Empty(scripted.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.True(json.RootElement.GetProperty("data").GetProperty("open").GetProperty("changeProven").GetBoolean());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("close").GetProperty("changeProven").GetBoolean());
        Assert.Equal(absent, json.RootElement.GetProperty("data").GetProperty("androidPreparation").GetProperty("connectAttempted").GetBoolean());
        Assert.Equal(absent ? 1 : 0, scripted.Runner.Requests.Count(r => r.Request.Arguments[0] == "connect"));
        JsonElement open = json.RootElement.GetProperty("data").GetProperty("open");
        JsonElement close = json.RootElement.GetProperty("data").GetProperty("close");
        string[] frameIds = [open.GetProperty("before").GetProperty("frame").GetProperty("frameId").GetString()!,
            open.GetProperty("after").GetProperty("frame").GetProperty("frameId").GetString()!,
            close.GetProperty("before").GetProperty("frame").GetProperty("frameId").GetString()!,
            close.GetProperty("after").GetProperty("frame").GetProperty("frameId").GetString()!];
        Assert.Equal(3, frameIds.Distinct().Count());
        Assert.Equal(frameIds[1], frameIds[2]);
        Assert.Equal(0, close.GetProperty("sourceObservations").GetInt32());
        foreach (JsonElement action in new[] { open, close })
        {
            JsonElement timing = action.GetProperty("timing");
            JsonElement work = timing.GetProperty("work");
            Assert.Equal(1, work.GetProperty("taps").GetInt32());
            Assert.Equal(2, work.GetProperty("viewportChecks").GetInt32());
            Assert.True(timing.GetProperty("tapCompletedAtMs").GetDouble() <= timing.GetProperty("proofFrameAtMs").GetDouble());
            Assert.True(timing.GetProperty("proofFrameAtMs").GetDouble() <= timing.GetProperty("elapsedMs").GetDouble());
        }

        Assert.Equal(2, open.GetProperty("timing").GetProperty("work").GetProperty("captures").GetInt32());
        Assert.Equal(1, close.GetProperty("timing").GetProperty("work").GetProperty("captures").GetInt32());
        JsonElement total = json.RootElement.GetProperty("data").GetProperty("timing");
        Assert.True(total.GetProperty("leaseAcquiredAtMs").GetDouble() <= total.GetProperty("preparedAtMs").GetDouble());
        Assert.True(total.GetProperty("preparedAtMs").GetDouble() <= total.GetProperty("totalMs").GetDouble());
        ApplicationResult<FileStream> released = WindowsInstanceMutationLease.TryAcquire(selected);
        Assert.True(released.IsSuccess);
        using FileStream finalLease = released.Value!;
    }

    [Fact]
    public async Task BusyTargetRejectsGameScenarioBeforeCaptureOrInput()
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        ApplicationResult<FileStream> acquired = WindowsInstanceMutationLease.TryAcquire(selected);
        Assert.True(acquired.IsSuccess);
        using FileStream lease = acquired.Value!;
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.TargetBusy, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Empty(fixture.Runner.Requests);
    }

    private static void QueueGameCapture(OperatorFixture fixture, string state)
    {
        fixture.Runner.EnqueueBinaryOutcome(File.ReadAllBytes(Path.Combine(PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", state + "-2160.png")));
        QueueViewport(fixture);
    }

    private static void QueueViewport(OperatorFixture fixture)
    {
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Input);
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Window);
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Focus);
    }

    [Theory]
    [InlineData(AndroidTransportState.Offline)]
    [InlineData(AndroidTransportState.Unknown)]
    [InlineData(AndroidTransportState.Absent)]
    [InlineData(AndroidTransportState.Device)]
    public async Task DockUnreadyTransportNeverCapturesOrTaps(AndroidTransportState initial)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(AndroidTestContext.Transport(initial));
        android.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(AndroidTestContext.Boot(false, null));
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(initial == AndroidTransportState.Device ? AzurPilotExitCode.AndroidNotReady : AzurPilotExitCode.AndroidTransportNotReady, result.ExitCode);
        Assert.Equal(initial == AndroidTransportState.Absent ? "mayHaveChanged" : "none", result.Effect);
        Assert.Equal(initial == AndroidTransportState.Absent ? 1 : 0, android.Host.ConnectRequests.Count);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.False(json.RootElement.GetProperty("data").GetProperty("gameUiMutation").GetProperty("mutationAttempted").GetBoolean());
    }

    [Theory]
    [InlineData("transport-lost", 0, "none")]
    [InlineData("recovered-transport-lost", 0, "changed")]
    [InlineData("foreground", 0, "none")]
    [InlineData("viewport", 0, "none")]
    [InlineData("ui", 0, "none")]
    [InlineData("tap-unknown", 1, "mayHaveChanged")]
    [InlineData("post-capture-lost", 1, "mayHaveChanged")]
    public async Task DockRechecksUiAfterReadinessAndNeverRetriesUncertainTap(string scenario, int taps, string effect)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        if (scenario == "recovered-transport-lost")
        {
            fixture.Runner.EnqueueOutcome(1, "", $"error: device '{endpoint}' not found");
            fixture.Runner.EnqueueOutcome(0, "connected");
        }

        QueueReadyAndroid(fixture);
        if (scenario is "transport-lost" or "recovered-transport-lost")
        {
            fixture.Runner.EnqueueBinaryOutcome([], exitCode: 1, standardError: $"error: device '{endpoint}' not found");
        }
        else
        {
            fixture.Runner.EnqueueBinaryOutcome(File.ReadAllBytes(Path.Combine(PinnedVersions.RepositoryRoot,
                "tests", "fixtures", "game", (scenario == "ui" ? "dock" : "main") + "-2160.png")));
            if (scenario != "ui")
            {
                fixture.Runner.EnqueueOutcome(0, scenario == "viewport" ? "unproven viewport" : GameInputViewportTests.Input);
                fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Window);
                fixture.Runner.EnqueueOutcome(0, scenario == "foreground" ? "unproven foreground" : GameInputViewportTests.Focus);
                if (taps > 0)
                {
                    fixture.Runner.EnqueueOutcome(scenario == "tap-unknown" ? 1 : 0, "");
                    if (scenario == "post-capture-lost")
                    {
                        fixture.Runner.EnqueueBinaryOutcome([], exitCode: 1, standardError: $"error: device '{endpoint}' not found");
                    }
                }
            }
        }

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(effect, result.Effect);
        Assert.Equal(taps, fixture.Runner.Requests.Count(r => r.Request.Arguments.Contains("tap", StringComparer.Ordinal)));
        Assert.Equal(scenario == "recovered-transport-lost" ? 1 : 0, fixture.Runner.Requests.Count(r => r.Request.Arguments[0] == "connect"));
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("open").GetProperty("changeProven").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("close").ValueKind);
        if (scenario is "transport-lost" or "recovered-transport-lost" or "post-capture-lost")
        {
            Assert.Equal("absent", json.RootElement.GetProperty("failure").GetProperty("details").GetProperty("transport_state").GetString());
        }
    }

    private static void QueueReadyAndroid(OperatorFixture fixture)
    {
        fixture.Runner.EnqueueOutcome(0, "device\n");
        fixture.Runner.EnqueueOutcome(0, "1\n");
        fixture.Runner.EnqueueOutcome(0, "15\n");
        fixture.Runner.EnqueueOutcome(0, "35\n");
    }

    [Theory]
    [InlineData("game-ui-observe")]
    [InlineData("game-input-observe")]
    [InlineData("screenshot-scan")]
    public async Task ReadOnlyCaptureCommandsReportAbsentWithoutConnect(string command)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        fixture.Runner.EnqueueBinaryOutcome([], exitCode: 1, standardError: $"error: device '{endpoint}' not found");
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.AndroidScreenshotCaptureFailed, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Equal(3, fixture.Runner.Requests.Count);
        Assert.DoesNotContain(fixture.Runner.Requests, r => r.Request.Arguments.Contains("connect", StringComparer.Ordinal));
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("absent", json.RootElement.GetProperty("failure").GetProperty("details").GetProperty("transport_state").GetString());
    }

    [Theory]
    [InlineData("connect-cancelled")]
    [InlineData("deadline")]
    public async Task DockInterruptedReadinessNeverCapturesOrTaps(string scenario)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            android.Host.ConnectRequests.Count == 0 ? AndroidTestContext.Transport(AndroidTransportState.Absent) : AndroidTestContext.Device());
        android.Host.ConnectHandler = (_, _) =>
        {
            if (scenario == "connect-cancelled")
            {
                cancellation.Cancel();
            }
            else
            {
                using ITimer elapsed = android.TimeProvider.CreateTimer(static _ => { }, null,
                    android.Timings.ReadinessDeadline, Timeout.InfiniteTimeSpan);
            }

            return AndroidTestContext.Command();
        };
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(service, token), cancellation.Token);
        Assert.Equal(scenario == "connect-cancelled" ? AzurPilotExitCode.OperationCancelled : AzurPilotExitCode.AndroidNotReady, result.ExitCode);
        Assert.Equal(scenario == "connect-cancelled" ? "mayHaveChanged" : "changed", result.Effect);
        _ = Assert.Single(android.Host.ConnectRequests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    private sealed class ThrowingGameUiHost(AzurPilotNativeBoundaryException exception) : IGameUiHost
    {
        public Task<ApplicationResult<GameUiObservation>> ObserveAsync(
            MuMuInstanceId instanceId, AndroidEndpoint endpoint, IReadOnlyList<string> assets,
            CancellationToken cancellationToken)
            => Task.FromException<ApplicationResult<GameUiObservation>>(exception);

        public Task<ApplicationResult<bool>> ValidateInputAsync(
            GameFrameProvenance frame, CancellationToken cancellationToken)
            => Task.FromException<ApplicationResult<bool>>(exception);

        public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(
            GameTap tap, CancellationToken cancellationToken)
            => Task.FromException<ApplicationResult<AndroidCommandOutcome>>(exception);
    }
}
