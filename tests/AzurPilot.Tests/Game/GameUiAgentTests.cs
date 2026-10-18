using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Game;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Fact]
    public async Task AgentDockRoundtripUsesProductionCaptureMatcherInputAndOneLease()
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
        };
        // Явная последовательность команд оставляет fake process границу независимой от алгоритма.
        scripted.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        scripted.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
        QueueGameCapture(scripted, "main");
        scripted.Runner.EnqueueOutcome(0, "");
        QueueGameCapture(scripted, "dock");
        QueueGameCapture(scripted, "dock");
        scripted.Runner.EnqueueOutcome(0, "");
        QueueGameCapture(scripted, "main");

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-dock-roundtrip", "--target", "mumu:7"],
            (run, token) => run(scripted.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        Assert.Equal(4, scripted.ScreenshotHost.CaptureCount);
        Assert.Equal(2, scripted.Runner.Requests.Count(r => r.Request.Arguments.Contains("tap", StringComparer.Ordinal)));
        Assert.All(scripted.Runner.Requests.Skip(2), r =>
        {
            Assert.Equal("-s", r.Request.Arguments[0]);
            Assert.Equal(endpoint.ToString(), r.Request.Arguments[1]);
        });
        Assert.Empty(scripted.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.True(json.RootElement.GetProperty("data").GetProperty("open").GetProperty("changeProven").GetBoolean());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("close").GetProperty("changeProven").GetBoolean());
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
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Input);
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Window);
        fixture.Runner.EnqueueOutcome(0, GameInputViewportTests.Focus);
    }
}
