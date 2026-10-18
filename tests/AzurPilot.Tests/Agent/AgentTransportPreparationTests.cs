using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Theory]
    [InlineData("android-prepare")]
    [InlineData("game-dock-roundtrip")]
    public async Task UnresolvedEndpointNeverConnectsOrStartsAnything(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(new()
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = "Endpoint не подтверждён.",
        });
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.AndroidEndpointUnavailable, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Empty(android.Host.TransportRequests);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
    }

    [Theory]
    [InlineData("android-observe")]
    [InlineData("game-observe")]
    public async Task ReadOnlyAbsentTransportNeverConnectsOrInventsGameFacts(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(AndroidTransportState.Absent));
        android.Host.PackageHandler = (_, _) => ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.QueryFailed);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("none", result.Effect);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(android.Host.BootRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        if (command == "game-observe")
        {
            JsonElement facts = data.GetProperty("facts");
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("installed").ValueKind);
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("processRunning").ValueKind);
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("foreground").ValueKind);
        }
        else
        {
            Assert.Equal("Absent", data.GetProperty("transport").GetProperty("state").GetString());
        }
    }

    [Theory]
    [InlineData(AndroidTransportState.Absent, "changed", 1)]
    [InlineData(AndroidTransportState.Device, "none", 0)]
    [InlineData(AndroidTransportState.Offline, "none", 0)]
    [InlineData(AndroidTransportState.Unknown, "none", 0)]
    public async Task AgentTransportPreparationReportsIndependentFactsAndNeverStartsEnvironment(
        AndroidTransportState initial, string effect, int connects)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        android.Host.TransportHandler = endpoint =>
        {
            var competing = WindowsInstanceMutationLease.TryAcquire(MuMuInstanceId.FromIndex("7"));
            using FileStream? unexpected = competing.IsSuccess ? competing.Value : null;
            Assert.Equal(ApplicationFailure.TargetBusy, competing.FailureInfo!.Code);
            Assert.Equal(AndroidTestContext.Endpoint, endpoint);
            return ApplicationResult<AndroidTransportObservation>.Success(
                AndroidTestContext.Transport(android.Host.ConnectRequests.Count == 0 ? initial : AndroidTransportState.Device));
        };
        android.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "android-prepare", "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);

        bool ready = initial is AndroidTransportState.Absent or AndroidTransportState.Device;
        Assert.Equal(ready ? 0 : AzurPilotExitCode.AndroidTransportNotReady, result.ExitCode);
        Assert.Equal(effect, result.Effect);
        Assert.Equal(connects, android.Host.ConnectRequests.Count);
        Assert.All(android.Host.ConnectRequests, e => Assert.Equal(AndroidTestContext.Endpoint, e));
        Assert.All(android.Host.EndpointRequests, id => Assert.Equal(MuMuInstanceId.FromIndex("7"), id));
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        JsonElement mutation = data.GetProperty("mutation");
        Assert.Equal(initial.ToString(), mutation.GetProperty("initialTransport").GetString());
        Assert.Equal(connects == 1, mutation.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(connects == 1, mutation.GetProperty("changeProven").GetBoolean());
        JsonElement boot = mutation.GetProperty("lastReadiness").GetProperty("boot");
        if (ready)
        {
            Assert.True(boot.GetProperty("shellAvailable").GetBoolean());
            Assert.Equal(1, boot.GetProperty("bootCompleted").GetInt32());
            Assert.Equal(connects == 1, data.GetProperty("preparation").GetProperty("connectAttempted").GetBoolean());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, boot.ValueKind);
            Assert.Empty(android.Host.BootRequests);
        }
    }

    [Theory]
    [InlineData("connect-failed", "mayHaveChanged", AzurPilotExitCode.AndroidTransportNotReady)]
    [InlineData("connect-cancelled", "mayHaveChanged", AzurPilotExitCode.OperationCancelled)]
    [InlineData("connect-exception", "mayHaveChanged", AzurPilotExitCode.InternalError)]
    [InlineData("boot-exception", "changed", AzurPilotExitCode.InternalError)]
    [InlineData("shell-unavailable", "changed", AzurPilotExitCode.AndroidNotReady)]
    [InlineData("boot-incomplete", "changed", AzurPilotExitCode.AndroidNotReady)]
    [InlineData("deadline", "changed", AzurPilotExitCode.AndroidNotReady)]
    public async Task AgentTransportFailurePreservesAttemptAndProofSeparately(string scenario, string effect, int exitCode)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(android.Host.ConnectRequests.Count == 0 || scenario == "connect-failed"
                ? AndroidTransportState.Absent : AndroidTransportState.Device));
        android.Host.ConnectHandler = (_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            if (scenario == "connect-exception")
            {
                throw new InvalidOperationException("private-connect-marker");
            }

            if (scenario == "connect-cancelled")
            {
                cancellation.Cancel();
                return ApplicationResult<AndroidCommandOutcome>.Failure(new()
                {
                    Code = ApplicationFailure.OperationCancelled,
                    Message = "Отмена подключения.",
                });
            }

            if (scenario == "deadline")
            {
                using ITimer elapsed = android.TimeProvider.CreateTimer(static _ => { }, null,
                    android.Timings.ReadinessDeadline, Timeout.InfiniteTimeSpan);
            }

            return scenario == "connect-failed"
                ? ApplicationResult<AndroidCommandOutcome>.Failure(new()
                {
                    Code = ApplicationFailure.AndroidEndpointUnavailable,
                    Message = "Подключение не выполнено.",
                }) : AndroidTestContext.Command();
        };
        android.Host.BootHandler = _ => scenario == "boot-exception"
            ? throw new InvalidOperationException("private-boot-marker")
            : ApplicationResult<AndroidBootObservation>.Success(
                AndroidTestContext.Boot(scenario != "shell-unavailable", scenario == "boot-incomplete" ? 0 : 1));

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "android-prepare", "--target", "mumu:7"],
            (run, token) => run(service, token), cancellation.Token);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(effect, result.Effect);
        Assert.Equal(AndroidTestContext.Endpoint, Assert.Single(android.Host.ConnectRequests));
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        JsonElement mutation = scenario.EndsWith("exception", StringComparison.Ordinal)
            ? data.GetProperty("mutation").GetProperty("androidPreparation") : data.GetProperty("mutation");
        Assert.Equal("Absent", mutation.GetProperty("initialTransport").GetString());
        Assert.True(mutation.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(effect == "changed", mutation.GetProperty("changeProven").GetBoolean());
        JsonElement boot = mutation.GetProperty("lastReadiness").GetProperty("boot");
        if (scenario is "shell-unavailable" or "boot-incomplete")
        {
            Assert.Equal(scenario != "shell-unavailable", boot.GetProperty("shellAvailable").GetBoolean());
            Assert.Equal(scenario == "boot-incomplete" ? 0 : 1, boot.GetProperty("bootCompleted").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, boot.ValueKind);
        }

        Assert.DoesNotContain("private-", json.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
    }

    [Theory]
    [InlineData("android-prepare")]
    [InlineData("game-dock-roundtrip")]
    public async Task TransportMutationsRevalidateRemovedIdentityAndRejectBusyTarget(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        var service = CreateAgentService(fixture, android);
        AgentContext context = new(service) { Target = MuMuInstanceId.FromIndex("7") };
        fixture.MuMuHost.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);
        AgentPayload removed = command == "android-prepare"
            ? await context.PrepareTransportAsync(TestContext.Current.CancellationToken)
            : await context.GameDockAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, removed.Failure!.Code);
        Assert.Equal("none", removed.Effect);
        Assert.Empty(android.Host.EndpointRequests);
        Assert.Empty(android.Host.ConnectRequests);

        fixture.MuMuHost.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([MuMuLifecycleTestContext.Instance("7")]);
        var acquired = WindowsInstanceMutationLease.TryAcquire(MuMuInstanceId.FromIndex("7"));
        Assert.True(acquired.IsSuccess);
        using FileStream lease = acquired.Value!;
        AgentEnvelope busy = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.TargetBusy, busy.ExitCode);
        Assert.Equal("none", busy.Effect);
        Assert.Empty(android.Host.TransportRequests);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
    }
}
