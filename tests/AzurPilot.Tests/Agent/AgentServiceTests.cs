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
        fixture.Runner.RunException = new InvalidOperationException("adb-boundary-marker");
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-start", "--target", "mumu:7"],
            (run, token) => run(fixture.Service, token), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("failure", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(ApplicationFailure.InternalError, json.RootElement.GetProperty("failure").GetProperty("code").GetString());
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

    [Theory]
    [InlineData("game-start", false)]
    [InlineData("game-stop", false)]
    [InlineData("game-restart", false)]
    [InlineData("game-start", true)]
    [InlineData("game-restart", true)]
    [Trait("Category", "Agent")]
    public async Task AgentGameFailureBeforeMutationHasNoEffect(string command, bool launcherFailure)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host);
        if (launcherFailure)
        {
            android.Host.LauncherHandler = (_, _) => ApplicationResult<AndroidLauncherResolution>.Failure(new()
            {
                Code = ApplicationFailure.AzurLaneLauncherUnresolved,
                Message = "Проверочный отказ разрешения launcher.",
            });
        }
        else
        {
            device.SetNotInstalled();
        }

        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(launcherFailure ? AzurPilotExitCode.AzurLaneLauncherUnresolved : AzurPilotExitCode.AzurLanePackageMissing, result.ExitCode);
        Assert.Equal("failure", result.Status);
        Assert.Equal("none", json.RootElement.GetProperty("effect").GetString());
        Assert.Equal(launcherFailure ? ApplicationFailure.AzurLaneLauncherUnresolved : ApplicationFailure.AzurLanePackageMissing,
            json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("false", json.RootElement.GetProperty("failure").GetProperty("details")
            .GetProperty(ApplicationFailure.MutationAttemptedDetailKey).GetString());
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Theory]
    [InlineData("game-start", "before", "none")]
    [InlineData("game-start", "mutation", "mayHaveChanged")]
    [InlineData("game-start", "postcondition", "mayHaveChanged")]
    [InlineData("game-stop", "mutation", "mayHaveChanged")]
    [InlineData("game-restart", "mutation", "mayHaveChanged")]
    [InlineData("game-restart", "second-mutation", "changed")]
    [Trait("Category", "Agent")]
    public async Task AgentGameExceptionPreservesMutationEvidence(string command, string phase, string effect)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host);
        if (command != "game-start")
        {
            device.SetForeground();
        }

        if (phase == "before")
        {
            android.Host.PackageHandler = (_, _) => throw new InvalidOperationException("private-game-marker");
        }
        else if (phase == "postcondition")
        {
            device.MutationApplied = _ => android.Host.PackageHandler = (_, _) => throw new InvalidOperationException("private-game-marker");
        }
        else
        {
            var apply = android.Host.MutationHandler!;
            android.Host.MutationHandler = (request, token) => phase == "second-mutation" && request.Mutation == AndroidGameMutation.ForceStop
                ? apply(request, token) : throw new InvalidOperationException("private-game-marker");
        }

        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        byte[] bytes = AgentJson.Serialize(ref result);
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("failure", result.Status);
        Assert.Equal(effect, json.RootElement.GetProperty("effect").GetString());
        Assert.Equal(ApplicationFailure.InternalError, json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").GetProperty("postcondition").ValueKind);
        JsonElement mutation = json.RootElement.GetProperty("data").GetProperty("mutation").GetProperty("game");
        Assert.Equal(phase != "before", mutation.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(phase == "second-mutation", mutation.GetProperty("changeProven").GetBoolean());
        Assert.Equal(phase == "before" ? 0 : phase == "second-mutation" ? 2 : 1, android.Host.MutationRequests.Count);
        Assert.DoesNotContain("private-game-marker", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Theory]
    [InlineData("game-start")]
    [InlineData("game-stop")]
    [Trait("Category", "Agent")]
    public async Task AgentIdempotentGameResultSurvivesShutdownFailure(string command)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host);
        if (command == "game-start")
        {
            device.SetForeground();
        }

        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"], async (run, token) =>
        {
            AgentPayload completed = await run(service, token);
            Assert.Null(completed.Failure);
            Assert.Equal("none", completed.Effect);
            throw new InvalidOperationException("shutdown-marker");
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("none", result.Effect);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(command == "game-start" ? "Foreground" : "Stopped",
            json.RootElement.GetProperty("data").GetProperty("completed").GetProperty("game").GetProperty("finalState").GetString());
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Theory]
    [InlineData("environment-prepare", "resolution", "none")]
    [InlineData("game-start", "resolution", "none")]
    [InlineData("environment-prepare", "offline", "none")]
    [InlineData("game-start", "offline", "none")]
    [InlineData("environment-prepare", "connect-exception", "mayHaveChanged")]
    [InlineData("game-start", "connect-exception", "mayHaveChanged")]
    [InlineData("environment-prepare", "boot-exception", "changed")]
    [InlineData("game-start", "boot-exception", "changed")]
    [Trait("Category", "Agent")]
    public async Task AgentPreparationEffectUsesBoundaryEvidence(string command, string phase, string effect)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        if (phase == "resolution")
        {
            android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(new()
            {
                Code = ApplicationFailure.AndroidEndpointUnavailable,
                Message = "Проверочный отказ разрешения endpoint.",
            });
        }
        else if (phase == "offline")
        {
            android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
                AndroidTestContext.Transport(AndroidTransportState.Offline));
        }
        else
        {
            android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
                android.Host.ConnectRequests.Count == 0 ? AndroidTestContext.Transport(AndroidTransportState.Absent) : AndroidTestContext.Device());
            android.Host.ConnectHandler = (_, _) => phase == "connect-exception"
                ? throw new InvalidOperationException("connect-marker") : AndroidTestContext.Command();
            android.Host.BootHandler = _ => throw new InvalidOperationException("boot-marker");
        }

        AgentEnvelope result = await AgentRunner.RunAsync(["agent", command, "--target", "mumu:7"],
            (run, token) => run(service, token), TestContext.Current.CancellationToken);
        Assert.Equal(phase == "resolution" ? AzurPilotExitCode.AndroidEndpointUnavailable
            : phase == "offline" ? AzurPilotExitCode.AndroidTransportNotReady : AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.Equal("failure", result.Status);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(effect, json.RootElement.GetProperty("effect").GetString());
        if (phase is "connect-exception" or "boot-exception")
        {
            JsonElement mutation = json.RootElement.GetProperty("data").GetProperty("mutation").GetProperty("androidPreparation");
            Assert.True(mutation.GetProperty("mutationAttempted").GetBoolean());
            Assert.Equal(phase == "boot-exception", mutation.GetProperty("changeProven").GetBoolean());
        }

        Assert.Equal(phase is "resolution" or "offline" ? 0 : 1, android.Host.ConnectRequests.Count);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Agent")]
    public async Task AgentFailedOrCancelledGameAttemptDoesNotClaimRollback(bool cancel)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        TestAndroidDevice device = new(android.Host) { StartReachesForeground = false, StartExitCode = 1 };
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancel)
        {
            device.MutationApplied = _ => cancellation.Cancel();
        }

        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "game-start", "--target", "mumu:7"],
            (run, token) => run(service, token), cancellation.Token);
        Assert.Equal(cancel ? AzurPilotExitCode.OperationCancelled : AzurPilotExitCode.AzurLaneLifecyclePostconditionNotMet, result.ExitCode);
        Assert.Equal(cancel ? "cancelled" : "failure", result.Status);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal("mayHaveChanged", json.RootElement.GetProperty("effect").GetString());
        Assert.Equal(cancel ? ApplicationFailure.OperationCancelled : ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
            json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("true", json.RootElement.GetProperty("failure").GetProperty("details")
            .GetProperty(ApplicationFailure.MutationAttemptedDetailKey).GetString());
        Assert.Equal(AndroidGameMutation.Start, Assert.Single(android.Host.MutationRequests).Mutation);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
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

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentMuMuStartTargetResolutionFailureReportsNoMutation()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);
        AgentContext context = new(fixture.Service) { Target = MuMuInstanceId.FromIndex("7") };

        AgentPayload result = await context.MuMuAsync(MuMuLifecycleOperation.Start, TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, result.Failure!.Code);
        Assert.Equal("false", result.Failure.Details![ApplicationFailure.MutationAttemptedDetailKey]);
        Assert.Equal("none", result.Effect);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact]
    [Trait("Category", "Agent")]
    public async Task AgentGameStopEndpointResolutionFailureReportsNoMutation()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AndroidTestContext android = new();
        AzurPilotOperatorService service = CreateAgentService(fixture, android);
        android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = "Проверочный отказ разрешения endpoint.",
        });
        AgentContext context = new(service) { Target = MuMuInstanceId.FromIndex("7") };

        AgentPayload result = await context.GameAsync("stop", TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, result.Failure!.Code);
        Assert.Equal("false", result.Failure.Details![ApplicationFailure.MutationAttemptedDetailKey]);
        Assert.Equal("none", result.Effect);
        Assert.Empty(android.Host.MutationRequests);
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
