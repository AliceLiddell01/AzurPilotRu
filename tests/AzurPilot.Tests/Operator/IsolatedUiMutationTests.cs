using AzurPilot.Core.Android;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    [Fact]
    public async Task LiveGrantPermitsOnlyEnvironmentForExactTarget()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        MuMuInstanceId target = MuMuInstanceId.FromIndex("7");
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        Assert.True(fixture.Service.InitializeLiveEnvironment(target).IsSuccess);
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Start, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.StartAzurLaneAsync(target, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.PrepareAndroidAsync(target, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.PrepareSelectedAndroidAsync(MuMuInstanceId.FromIndex("8"), token)).Failure!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(MuMuInstanceId.FromIndex("8"), MuMuLifecycleOperation.Stop, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Restart, token)).FailureInfo!.Code);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);

        // До своей попытки подготовки сессия не вправе остановить даже этот экземпляр.
        Assert.True((await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Stop, token)).IsFailure);
        fixture.MuMuHost.MutationHandler = request =>
        {
            fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(
                request.Mutation == MuMuLifecycleMutation.Stop ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };
        EnqueueAndroidReady(fixture, target, new AndroidEndpoint("127.0.0.1", 20312));
        Assert.True((await fixture.Service.PrepareSelectedAndroidAsync(target, token)).IsSuccess);
        MuMuMutationRequest launch = Assert.Single(fixture.MuMuHost.MutationRequests);
        Assert.Equal(target, launch.InstanceId);
        Assert.Equal(MuMuLifecycleMutation.Start, launch.Mutation);
        Assert.NotEmpty(fixture.Runner.Requests);

        ApplicationResult<MuMuLifecycleOutcome> stopped = await fixture.Service.RunMuMuLifecycleAsync(
            target, MuMuLifecycleOperation.Stop, token);
        Assert.True(stopped.IsSuccess);
        MuMuMutationRequest stopRequest = fixture.MuMuHost.MutationRequests[1];
        Assert.Equal(target, stopRequest.InstanceId);
        Assert.Equal(MuMuLifecycleMutation.Stop, stopRequest.Mutation);
        Assert.True((await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Stop, token)).IsFailure);
        Assert.Equal(2, fixture.MuMuHost.MutationCount);
    }

    [Theory]
    [InlineData(MuMuLifecycleState.Running)]
    [InlineData(MuMuLifecycleState.Unknown)]
    public async Task LivePreflightRejectsUnownedInitialState(MuMuLifecycleState initial)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(initial);
        Assert.True(fixture.Service.InitializeLiveEnvironment(MuMuInstanceId.FromIndex("7")).IsFailure);
        await AssertLiveProviderMutationsDenied(fixture);
    }

    [Fact]
    public async Task LivePreflightObservationFailureAndForeignInstanceNeverGrantMutation()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        fixture.MuMuHost.ObservationResult = ApplicationResult<MuMuInstanceState>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuStateUnproven,
            Message = "Наблюдение недоступно.",
        });
        Assert.True(fixture.Service.InitializeLiveEnvironment(MuMuInstanceId.FromIndex("7")).IsFailure);
        await AssertLiveProviderMutationsDenied(fixture);
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        Assert.True(fixture.Service.InitializeLiveEnvironment(MuMuInstanceId.FromIndex("8")).IsFailure);
        await AssertLiveProviderMutationsDenied(fixture);
    }

    [Theory]
    [InlineData(MuMuLifecycleState.Running)]
    [InlineData(MuMuLifecycleState.Unknown)]
    public async Task LivePreparationRechecksStateAfterPreflight(MuMuLifecycleState changed)
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        MuMuInstanceId target = MuMuInstanceId.FromIndex("7");
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        Assert.True(fixture.Service.InitializeLiveEnvironment(target).IsSuccess);
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(changed);
        Assert.False((await fixture.Service.PrepareSelectedAndroidAsync(target, TestContext.Current.CancellationToken)).IsSuccess);
        await AssertLiveProviderMutationsDenied(fixture);
    }

    private static async Task AssertLiveProviderMutationsDenied(OperatorFixture fixture)
    {
        MuMuInstanceId target = MuMuInstanceId.FromIndex("7");
        CancellationToken token = TestContext.Current.CancellationToken;
        foreach (MuMuLifecycleOperation operation in Enum.GetValues<MuMuLifecycleOperation>())
        {
            Assert.True((await fixture.Service.RunMuMuLifecycleAsync(target, operation, token)).IsFailure);
        }

        Assert.False((await fixture.Service.PrepareSelectedAndroidAsync(target, token)).IsSuccess);
        Assert.True((await fixture.Service.PrepareAndroidAsync(target, token)).IsFailure);
        Assert.True((await fixture.Service.StartAzurLaneAsync(target, token)).IsFailure);
        Assert.True((await fixture.Service.StopAzurLaneAsync(target, token)).IsFailure);
        Assert.True((await fixture.Service.RestartAzurLaneAsync(target, token)).IsFailure);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task IsolatedUiRejectsEveryExternalMutationBeforeCallingProviders()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        MuMuInstanceId target = MuMuInstanceId.FromIndex("7");
        CancellationToken token = TestContext.Current.CancellationToken;
        List<ApplicationFailure?> failures = [];
        foreach (MuMuLifecycleOperation operation in Enum.GetValues<MuMuLifecycleOperation>())
        {
            failures.Add((await fixture.Service.RunMuMuLifecycleAsync(target, operation, token)).FailureInfo);
        }

        failures.Add((await fixture.Service.PrepareAndroidAsync(target, token)).FailureInfo);
        failures.Add((await fixture.Service.PrepareSelectedAndroidAsync(target, token)).Failure);
        failures.Add((await fixture.Service.StartAzurLaneAsync(target, token)).FailureInfo);
        failures.Add((await fixture.Service.StopAzurLaneAsync(target, token)).FailureInfo);
        failures.Add((await fixture.Service.RestartAzurLaneAsync(target, token)).FailureInfo);
        Assert.All(failures, failure =>
        {
            Assert.NotNull(failure);
            Assert.Equal(ApplicationFailure.InvalidArgument, failure.Code);
            Assert.Equal("false", failure.Details![ApplicationFailure.MutationAttemptedDetailKey]);
        });
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public void IsolatedSaveRejectsMissingSnapshotWithoutUserFallback()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
        ApplicationResult<AzurPilotConfigurationSnapshot> result = fixture.Service.SaveConfiguration(fixture.Service.Configuration,
            fixture.Service.Configuration.Configuration, out string? backup);
        Assert.Equal(ApplicationFailure.InvalidArgument, result.FailureInfo!.Code);
        Assert.Null(backup);
        Assert.False(File.Exists(fixture.Service.Configuration.FilePath));
    }
}
