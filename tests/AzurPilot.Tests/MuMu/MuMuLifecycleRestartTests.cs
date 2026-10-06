using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства явной семантики restart.
/// </summary>
/// <remarks>
/// Restart доказывается композицией stop → доказан Stopped → start → доказан Running, а для исходного
/// Stopped определяется как start: остановка уже доказана наблюдением. Провайдерский restart-verb не
/// запрашивается, потому что host-примитив его не содержит.
/// </remarks>
public sealed class MuMuLifecycleRestartTests
{
    [Fact(DisplayName = "Restart из Running — композиция stop → доказан Stopped → start → доказан Running")]
    public async Task RestartFromRunningComposesStopThenStart()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);

        List<MuMuLifecycleState> observedStatesAtMutation = [];
        context.Host.MutationHandler = request =>
        {
            observedStatesAtMutation.Add(context.Host.ObservationResult.Value!.State);
            context.Host.ObservationResult = request.Mutation == MuMuLifecycleMutation.Stop
                ? MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped, "stopped-by-stop")
                : MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running, "running-by-start");
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleOperation.Restart, outcome.Operation);
        Assert.Equal(MuMuLifecycleState.Running, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, outcome.FinalState);
        Assert.Contains("mutations=stop(exit=0),start(exit=0)", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
        Assert.All(
            context.Host.MutationRequests,
            request => Assert.Equal(MuMuInstanceId.FromIndex("1"), request.InstanceId));

        // Start запрошен только после того, как Stopped был доказан наблюдением, а не кодом выхода.
        Assert.Equal(
            [MuMuLifecycleState.Running, MuMuLifecycleState.Stopped],
            observedStatesAtMutation);
    }

    [Fact(DisplayName = "Restart из Stopped определён как start: mutation остановки не выполняется")]
    public async Task RestartFromStoppedIsStart()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
            MuMuLifecycleState.Stopped,
            "stopped-evidence");
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleOperation.Restart, outcome.Operation);
        Assert.Equal(MuMuLifecycleState.Stopped, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, outcome.FinalState);
        Assert.Contains("mutations=start(exit=0)", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(MuMuLifecycleMutation.Start, context.Host.MutationRequests[0].Mutation);
    }

    [Fact(DisplayName = "Restart из Running при недостижимом Stopped падает по deadline без второй mutation")]
    public async Task RestartFromRunningWithoutProvenStopTimesOut()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.Equal("restart", failure.Details!["operation"]);
        Assert.Equal("stopped", failure.Details!["target_state"]);

        // Start не запрашивался: postcondition остановки не был доказан.
        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(MuMuLifecycleMutation.Stop, context.Host.MutationRequests[0].Mutation);
    }

    [Fact(DisplayName = "Restart из Unknown не считает Stopped доказанным и применяет композицию")]
    public async Task RestartFromUnknownComposesStopThenStart()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Unknown);
        context.Host.MutationHandler = request =>
        {
            context.Host.ObservationResult = request.Mutation == MuMuLifecycleMutation.Stop
                ? MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped)
                : MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Unknown, result.Value!.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
    }
}
