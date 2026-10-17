using AzurPilot.Core.Failures;
using AzurPilot.Core.Android;
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
        fixture.Service.LiveEnvironmentTarget = target;
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Start, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.StartAzurLaneAsync(target, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.PrepareAndroidAsync(target, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.PrepareSelectedAndroidAsync(MuMuInstanceId.FromIndex("8"), token)).Failure!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(MuMuInstanceId.FromIndex("8"), MuMuLifecycleOperation.Stop, token)).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument, (await fixture.Service.RunMuMuLifecycleAsync(target, MuMuLifecycleOperation.Restart, token)).FailureInfo!.Code);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Empty(fixture.MuMuHost.MutationRequests);

        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        fixture.MuMuHost.MutationHandler = _ =>
        {
            fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped, "stopped-after-stop");
            return MuMuLifecycleTestContext.Command(0);
        };
        ApplicationResult<MuMuLifecycleOutcome> stopped = await fixture.Service.RunMuMuLifecycleAsync(
            target, MuMuLifecycleOperation.Stop, token);
        Assert.True(stopped.IsSuccess);
        MuMuMutationRequest stopRequest = Assert.Single(fixture.MuMuHost.MutationRequests);
        Assert.Equal(target, stopRequest.InstanceId);
        Assert.Equal(MuMuLifecycleMutation.Stop, stopRequest.Mutation);

        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        EnqueueAndroidReady(fixture, target, new AndroidEndpoint("127.0.0.1", 20312));
        Assert.True((await fixture.Service.PrepareSelectedAndroidAsync(target, token)).IsSuccess);
        Assert.NotEmpty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task IsolatedUiRejectsEveryExternalMutationBeforeCallingProviders()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.Service.ExternalMutationsDisabled = true;
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
}
