using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Diagnostics;
using AzurPilot.Tests.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.App.OperatorServices;
using AzurPilot.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using AzurPilot.Core.Configuration;
using AzurPilot.Tests.Agent;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentInventoryRejectsOversizedEnumerationBeforePerTargetObservation()
    {
        OperatorFixture fixture = CreateFixture([.. Enumerable.Range(0, AgentRequest.MaximumInventoryInstances + 1)
            .Select(index => MuMuLifecycleTestContext.Instance(index.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "instances"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.AgentOutputLimitExceeded, result.ExitCode);
        Assert.Equal(0, fixture.MuMuHost.ObservationCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentConfiguredTargetAndExplicitOverrideDoNotRewriteConfiguration()
    {
        using AgentTestDirectory directory = new();
        string path = Path.Combine(directory.Root, "config.yaml");
        AzurPilotConfiguration config = AzurPilotConfigurationDefaults.Create();
        config = config with { MuMu = config.MuMu with { Instance = "mumu:7" } };
        string source = AzurPilotConfigurationYamlWriter.Write(config);
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);
        var snapshot = AzurPilotConfigurationLoader.Load(path).Value!;
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("3"), MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        AzurPilotOperatorService service = CreateAgentService(fixture, android, snapshot);
        AgentEnvelope configured = await AgentRunner.RunAsync(["agent", "resolve"], (run, token) => run(service, token),
            TestContext.Current.CancellationToken);
        Assert.Equal("mumu:7", configured.Target);
        AgentEnvelope overridden = await AgentRunner.RunAsync(["agent", "resolve", "--target", "mumu:3"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal("mumu:3", overridden.Target);
        Assert.Equal("mumu:7", service.Configuration.Configuration.MuMu.Instance);
        Assert.Equal(source, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(android.Host.MutationRequests);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentHostFailureAfterMutationKeepsProvenResult()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.MutationCount == 0 ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Running);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "mumu-start", "--target", "mumu:7"], async (run, token) =>
        {
            _ = await run(fixture.Service, token);
            throw new InvalidOperationException("shutdown-marker");
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("Running", json.RootElement.GetProperty("data").GetProperty("completed").GetProperty("finalState").GetString());
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentUnexpectedAndroidExceptionRetainsMuMuProof()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.MutationCount == 0 ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Running);
        // У runner нет запланированного ADB-ответа: внешний seam бросает programming exception.
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-start", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("Running", json.RootElement.GetProperty("data").GetProperty("preparation")
            .GetProperty("muMuStartOutcome").GetProperty("finalState").GetString());
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
    }

    [Theory]
    [InlineData("game-start", "Foreground", 1)]
    [InlineData("game-stop", "Stopped", 1)]
    [InlineData("game-restart", "Foreground", 2)]
    [Trait("Category", "Agent")]
    public async Task AgentGameActionsExecuteRealOrchestrationAndExactPostcondition(string command, string finalState, int mutations)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host);
        if (command != "game-start")
        {
            device.SetForeground();
        }

        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        Assert.Equal(mutations, android.Host.MutationRequests.Count);
        Assert.All(android.Host.MutationRequests, request => Assert.Equal(AndroidTestContext.Endpoint, request.Endpoint));
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(finalState, json.RootElement.GetProperty("data").GetProperty("game").GetProperty("finalState").GetString());
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(android.Host.ConnectRequests);
    }

    [Theory]
    [InlineData("android-observe")]
    [InlineData("game-observe")]
    [InlineData("environment-prepare")]
    [Trait("Category", "Agent")]
    public async Task AgentObservationsAndReadyEnvironmentDoNotMutate(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host);
        device.SetStopped();
        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.All(android.Host.EndpointRequests, id => Assert.Equal(MuMuInstanceId.FromIndex("7"), id));
        if (command == "environment-prepare")
        {
            using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
            Assert.Equal("Completed", json.RootElement.GetProperty("data").GetProperty("stage").GetString());
        }
    }

    private static AzurPilotOperatorService CreateAgentService(OperatorFixture fixture, AndroidTestContext android,
        AzurPilotConfigurationSnapshot? snapshot = null)
    {
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        android.Host.AdbExecutableHandler = _ => ApplicationResult<AndroidAdbExecutable>.Success(new("controlled-adb", "controlled-version"));
        android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(AndroidTestContext.Device());
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(AndroidTestContext.Boot(true, 1));
        MuMuLifecycleService lifecycle = new(fixture.MuMuHost, new MuMuInstanceMutationGate(), android.TimeProvider,
            MuMuLifecycleTimings.Default, NullLogger<MuMuLifecycleService>.Instance);
        AzurPilotConfigurationSnapshot configuration = snapshot ?? fixture.Service.Configuration;
        AzurPilotDiagnosticService diagnostics = new(configuration, NativeBoundaryFailureMapper.Map,
            NativeBoundaryFailureMapper.MapIncompatibility, fixture.MuMuHost, lifecycle,
            android.Host, android.Readiness, android.GameState);
        return new(diagnostics, fixture.MuMuHost, lifecycle, android.Host, android.Readiness,
            android.GameState, android.Lifecycle, fixture.ScreenshotHost, configuration);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentExactTargetDoesNotSelectNeighborAndAutoRemainsAmbiguous()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("3"), MuMuLifecycleTestContext.Instance("7"));
        List<MuMuInstanceId> observed = [];
        fixture.MuMuHost.ObservationHandler = id =>
        {
            observed.Add(id);
            return MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Unknown);
        };
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "mumu-observe", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("mumu:7", result.Target);
        Assert.All(observed, id => Assert.Equal(MuMuInstanceId.FromIndex("7"), id));
        Assert.NotEmpty(observed);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("Unknown", json.RootElement.GetProperty("data").GetProperty("state").GetString());
        AgentEnvelope ambiguous = await AgentRunner.RunAsync(["agent", "game-start", "--target", "auto"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.MuMuInstanceAmbiguous, ambiguous.ExitCode);
        Assert.Equal("none", ambiguous.Effect);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Agent")]
    public async Task AgentMuMuStartDistinguishesIdempotentAndChanged(bool running)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            running || fixture.MuMuHost.MutationCount > 0 ? MuMuLifecycleState.Running : MuMuLifecycleState.Stopped);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "mumu-start", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(running ? "none" : "changed", result.Effect);
        Assert.Equal(running ? 0 : 1, fixture.MuMuHost.MutationCount);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("Running", json.RootElement.GetProperty("data").GetProperty("finalState").GetString());
    }

    [Theory]
    [InlineData("environment-prepare")]
    [InlineData("game-start")]
    [Trait("Category", "Agent")]
    public async Task AgentPreservesMuMuPartialEffectWhenAndroidFails(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.MutationCount > 0 ? MuMuLifecycleState.Running : MuMuLifecycleState.Stopped);
        fixture.Runner.EnqueueFailure(new ApplicationFailure { Code = ApplicationFailure.AndroidAdbUnavailable, Message = "seam" });
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.AndroidAdbUnavailable, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
        string json = System.Text.Encoding.UTF8.GetString(AgentJson.Serialize(ref result));
        Assert.Contains("postflightMuMuState", json, StringComparison.Ordinal);
        Assert.Contains("Running", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.MuMuHost.MutationRequests, r => r.Mutation == MuMuLifecycleMutation.Stop);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentScreenshotUsesOneCaptureAndRealNativeRoiFacts()
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        fixture.Runner.EnqueueBinaryOutcome(ScreenshotPngFixture.CreateRgb(16, 9, static (_, _) => (10, 20, 30)));
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "screenshot-scan", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, fixture.ScreenshotHost.CaptureCount);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(16, data.GetProperty("capture").GetProperty("frameInfo").GetProperty("width").GetInt32());
        JsonElement regions = data.GetProperty("regions");
        Assert.Equal(2, regions.GetArrayLength());
        Assert.Equal("FullFrame", regions[0].GetProperty("region").GetString());
        Assert.Equal(144, regions[0].GetProperty("pixelCount").GetInt32());
        Assert.Equal(10, regions[0].GetProperty("meanRed").GetDouble());
        Assert.Equal(20, regions[1].GetProperty("meanGreen").GetDouble());
        JsonElement bounds = regions[1].GetProperty("physicalPixelBounds");
        Assert.True(bounds.GetProperty("right").GetInt32() - bounds.GetProperty("left").GetInt32() < 16);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentUnexpectedServiceExceptionStillHasTargetAndSafeJson()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => throw new InvalidOperationException("private-marker");
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "mumu-observe", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("mumu:7", result.Target);
        Assert.Equal("none", result.Effect);
        Assert.DoesNotContain("private-marker", System.Text.Encoding.UTF8.GetString(AgentJson.Serialize(ref result)), StringComparison.Ordinal);
    }
}
