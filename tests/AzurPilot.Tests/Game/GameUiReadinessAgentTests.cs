using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.Game;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyAgentReusesHealthyEnvironmentAndUsesOneLeaseOnlyForEnsure(bool ensure)
    {
        using GameUiReadinessTests.ReadyHost ui = new("main");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android, gameUi: ui, clock: ui.Clock);
        TestAndroidDevice device = new(android.Host);
        device.SetForeground();
        ui.OnCapture = () =>
        {
            var competing = WindowsInstanceMutationLease.TryAcquire(ui.Target);
            using FileStream? acquired = competing.IsSuccess ? competing.Value : null;
            Assert.Equal(ensure, competing.IsFailure);
        };
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", ensure ? "game-ui-ready-ensure" : "game-ui-ready-observe", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Equal("mumu:7", result.Target);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Equal(0, ui.Taps);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal("Ready", (ensure ? data.GetProperty("ui") : data).GetProperty("state").GetString());
        Assert.True((ensure ? data.GetProperty("ui") : data).GetProperty("stableFrames").GetInt32() >= 3);
    }

    [Fact]
    public async Task ReadyEnsureColdStartUsesExistingLifecycleAndRetainsPreparation()
    {
        using GameUiReadinessTests.ReadyHost ui = new("login") { AdvanceOnTap = true, InitialUnknownFrames = 40 };
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android, gameUi: ui, clock: ui.Clock);
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.MutationCount == 0 ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Running);
        _ = new TestAndroidDevice(android.Host);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-ui-ready-ensure", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        Assert.Equal(2, ui.Taps);
        Assert.True(ui.Captures > 40);
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
        _ = Assert.Single(android.Host.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("Ready", json.RootElement.GetProperty("data").GetProperty("ui").GetProperty("state").GetString());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("preparation").GetProperty("muMuStartRequested").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyAgentTargetDisappearanceAndEndpointChangesFailClosed(bool changedEndpoint)
    {
        using GameUiReadinessTests.ReadyHost ui = new("main");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android, gameUi: ui, clock: ui.Clock);
        ui.OnCapture = () =>
        {
            if (changedEndpoint)
            {
                android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Success(new("other-host", 20312));
            }
            else
            {
                fixture.MuMuHost.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);
            }
        };
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-ui-ready-observe", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Equal(0, ui.Taps);
        Assert.Equal(1, ui.Captures);
        Assert.Equal(0, ui.Viewports);
    }

    [Theory]
    [InlineData("game-ui-ready-observe")]
    [InlineData("game-ui-ready-ensure")]
    public async Task ReadyAgentTransportFailureNeverCapturesOrTaps(string command)
    {
        using GameUiReadinessTests.ReadyHost ui = new("main");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android, gameUi: ui, clock: ui.Clock);
        android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(new()
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = "Проверочный отказ endpoint.",
        });
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.AndroidEndpointUnavailable, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Equal(0, ui.Captures);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Theory]
    [InlineData("no-change", "mayHaveChanged")]
    [InlineData("tap-failure", "mayHaveChanged")]
    [InlineData("post-transport", "mayHaveChanged")]
    public async Task ReadyAgentReportsUnprovenUiEffectAndExactTapCount(string fault, string effect)
    {
        using GameUiReadinessTests.ReadyHost ui = new("login") { AdvanceOnTap = true, Fault = fault };
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android, gameUi: ui, clock: ui.Clock);
        TestAndroidDevice device = new(android.Host);
        device.SetForeground();
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-ui-ready-ensure", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(effect, result.Effect);
        Assert.Equal(1, ui.Taps);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("ui").GetProperty("tapAttempts").GetInt32());
        Assert.NotEqual("Ready", json.RootElement.GetProperty("data").GetProperty("ui").GetProperty("state").GetString());
    }
}
