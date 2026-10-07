using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства start и stop: идемпотентность, ровно одна mutation и postcondition-наблюдение.
/// </summary>
/// <remarks>
/// Переход доказывается наблюдением авторитетного состояния конкретного экземпляра, а не кодом выхода
/// control utility и не фиксированной задержкой. Время идёт через управляемый
/// <see cref="MuMuTestTimeProvider"/>, поэтому fixed sleeps не используются.
/// </remarks>
public sealed class MuMuLifecycleStartStopTests
{
    [Fact(DisplayName = "Start при доказанном Running успешен без mutation")]
    public async Task StartWhenRunningDoesNotMutate()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
            MuMuLifecycleState.Running,
            "running-evidence");

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleOperation.Start, outcome.Operation);
        Assert.Equal(MuMuLifecycleState.Running, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, outcome.FinalState);
        Assert.Equal<MuMuInstanceId?>(MuMuInstanceId.FromIndex("1"), outcome.InstanceId);
        Assert.Contains("mutations=none", outcome.Evidence, StringComparison.Ordinal);
        Assert.Contains("running-evidence", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(0, context.Host.MutationCount);
        Assert.Equal(1, context.Host.ObservationCount);
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Start при Stopped выполняет ровно одну mutation и доказывает Running наблюдением")]
    public async Task StartFromStoppedMutatesOnceAndProvesRunning()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
            MuMuLifecycleState.Stopped,
            "stopped-evidence");
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
                MuMuLifecycleState.Running,
                "running-after-start");
            return MuMuLifecycleTestContext.Command(0, "start-output");
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleState.Stopped, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, outcome.FinalState);
        Assert.Contains("mutations=start(exit=0)", outcome.Evidence, StringComparison.Ordinal);
        Assert.Contains("running-after-start", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(MuMuLifecycleMutation.Start, context.Host.MutationRequests[0].Mutation);
        Assert.Equal(MuMuInstanceId.FromIndex("1"), context.Host.MutationRequests[0].InstanceId);

        // Начальное наблюдение и наблюдение postcondition: polling идёт одним авторитетным источником.
        Assert.Equal(2, context.Host.ObservationCount);
        Assert.Equal(1, context.Host.MaxConcurrentMutations);
    }

    [Fact(DisplayName = "Start при Unknown не считает состояние доказанным и выполняет mutation")]
    public async Task StartFromUnknownMutatesOnce()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Unknown);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Unknown, result.Value!.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);
        Assert.Equal(1, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Stop при доказанном Stopped успешен без mutation")]
    public async Task StopWhenStoppedDoesNotMutate()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("2");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StopAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleOperation.Stop, outcome.Operation);
        Assert.Equal(MuMuLifecycleState.Stopped, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Stopped, outcome.FinalState);
        Assert.Contains("mutations=none", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(0, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Stop при Running выполняет ровно одну mutation и доказывает Stopped наблюдением")]
    public async Task StopFromRunningMutatesOnceAndProvesStopped()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("2");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
                MuMuLifecycleState.Stopped,
                "stopped-after-stop");
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StopAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(MuMuLifecycleState.Running, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Stopped, outcome.FinalState);
        Assert.Contains("mutations=stop(exit=0)", outcome.Evidence, StringComparison.Ordinal);

        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(MuMuLifecycleMutation.Stop, context.Host.MutationRequests[0].Mutation);
        Assert.Equal(MuMuInstanceId.FromIndex("2"), context.Host.MutationRequests[0].InstanceId);
    }

    [Fact(DisplayName = "Stop при Unknown не считает состояние доказанным и выполняет mutation")]
    public async Task StopFromUnknownMutatesOnce()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("2");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Unknown);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StopAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Stopped, result.Value!.FinalState);
        Assert.Equal(1, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Ненулевой код выхода не мешает успеху, если postcondition доказан наблюдением")]
    public async Task NonZeroExitCodeIsNotProofOfFailure()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
                MuMuLifecycleState.Running,
                "running-despite-exit-code");
            return MuMuLifecycleTestContext.Command(1, "control-reported-failure");
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);
        Assert.Contains("start(exit=1)", result.Value!.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Нулевой код выхода не является успехом: без доказанного Running операция падает по deadline")]
    public async Task ZeroExitCodeIsNotProofOfSuccess()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);

        // Launch и его единственный повтор: polling сам по себе повторных mutation не выполняет, поэтому
        // mutation ровно две — единый переход не превращает код выхода 0 в успех.
        Assert.Equal(2, context.Host.MutationCount);
        Assert.Equal(
            [MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));

        // Polling ограничен deadline и интервалом опроса владельца чисел времени вместе с окном эффекта и
        // подтверждающим наблюдением перед повтором.
        long expectedPolls = 1
            + (long)(context.Timings.StartDeadline / context.Timings.PollInterval)
            + (long)(context.Timings.LaunchEffectWindow / context.Timings.PollInterval)
            + 2;
        Assert.InRange(context.Host.ObservationCount, 2L, expectedPolls);
    }
}
