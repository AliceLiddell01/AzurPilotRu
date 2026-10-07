using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства process-local координации lifecycle-mutation.
/// </summary>
/// <remarks>
/// Одновременные операции одного экземпляра не пересекаются, разные экземпляры не блокируют друг друга, а
/// неиспользуемые записи координации освобождаются. Время не участвует: блокировка снимается сигналом, а
/// не задержкой.
/// </remarks>
public sealed class MuMuLifecycleConcurrencyTests
{
    [Fact(DisplayName = "Одновременные операции одного instance сериализуются")]
    public async Task OperationsOnSameInstanceAreSerialized()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);

        using ManualResetEventSlim mutationEntered = new(false);
        using ManualResetEventSlim releaseMutation = new(false);
        context.Host.MutationHandler = _ =>
        {
            mutationEntered.Set();
            releaseMutation.Wait();
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };

        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        Task<ApplicationResult<MuMuLifecycleOutcome>> first = Task.Run(
            () => context.Service.StartAsync(MuMuLifecycleTestContext.Installation, instance, testCancellation),
            testCancellation);
        mutationEntered.Wait(testCancellation);

        Task<ApplicationResult<MuMuLifecycleOutcome>> second = context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            testCancellation);

        // Вторая операция не может ни наблюдать, ни мутировать, пока первая держит координацию.
        Assert.False(second.IsCompleted);
        Assert.Equal(1, context.Host.ObservationCount);
        Assert.Equal(1, context.Host.MutationCount);

        releaseMutation.Set();

        ApplicationResult<MuMuLifecycleOutcome> firstResult = await first;
        ApplicationResult<MuMuLifecycleOutcome> secondResult = await second;

        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess);

        // Вторая операция началась после первой: её начальное наблюдение уже доказало Running.
        Assert.Equal(MuMuLifecycleState.Running, secondResult.Value!.InitialState);
        Assert.Contains("mutations=none", secondResult.Value!.Evidence, StringComparison.Ordinal);
        Assert.Equal(3, context.Host.ObservationCount);

        Assert.Equal(1, context.Host.MaxConcurrentMutations);
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Операции разных instances не блокируют друг друга")]
    public async Task OperationsOnDifferentInstancesDoNotBlockEachOther()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance blocked = MuMuLifecycleTestContext.Instance("1");
        MuMuInstance independent = MuMuLifecycleTestContext.Instance("2");

        MuMuLifecycleState blockedInstanceState = MuMuLifecycleState.Stopped;
        context.Host.ObservationHandler = instance => instance == blocked.Id
            ? MuMuLifecycleTestContext.Observed(blockedInstanceState)
            : MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);

        using ManualResetEventSlim mutationEntered = new(false);
        using ManualResetEventSlim releaseMutation = new(false);
        context.Host.MutationHandler = request =>
        {
            if (request.InstanceId == blocked.Id)
            {
                blockedInstanceState = MuMuLifecycleState.Running;
                mutationEntered.Set();
                releaseMutation.Wait();
            }

            return MuMuLifecycleTestContext.Command(0);
        };

        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        Task<ApplicationResult<MuMuLifecycleOutcome>> blockedOperation = Task.Run(
            () => context.Service.StartAsync(MuMuLifecycleTestContext.Installation, blocked, testCancellation),
            testCancellation);
        mutationEntered.Wait(testCancellation);

        ApplicationResult<MuMuLifecycleOutcome> independentResult = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            independent,
            testCancellation);

        Assert.True(independentResult.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, independentResult.Value!.FinalState);
        Assert.False(blockedOperation.IsCompleted);
        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(1, context.Host.MaxConcurrentMutations);

        releaseMutation.Set();

        ApplicationResult<MuMuLifecycleOutcome> blockedResult = await blockedOperation;
        Assert.True(blockedResult.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, blockedResult.Value!.FinalState);

        // Координация не удерживается после завершения обеих операций.
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Отменённое ожидание координации возвращает operation_cancelled и не оставляет записей")]
    public async Task CancelledGateWaitReturnsOperationCancelled()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);

        using ManualResetEventSlim mutationEntered = new(false);
        using ManualResetEventSlim releaseMutation = new(false);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            mutationEntered.Set();
            releaseMutation.Wait();
            return MuMuLifecycleTestContext.Command(0);
        };

        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        Task<ApplicationResult<MuMuLifecycleOutcome>> first = Task.Run(
            () => context.Service.StartAsync(MuMuLifecycleTestContext.Installation, instance, testCancellation),
            testCancellation);
        mutationEntered.Wait(testCancellation);

        using CancellationTokenSource source = new();
        Task<ApplicationResult<MuMuLifecycleOutcome>> queued = context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            source.Token);
        source.Cancel();

        ApplicationResult<MuMuLifecycleOutcome> queuedResult = await queued;

        Assert.True(queuedResult.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, queuedResult.FailureInfo!.Code);
        Assert.Equal("gate", queuedResult.FailureInfo!.Details!["phase"]);
        Assert.Equal(1, context.Host.MutationCount);

        releaseMutation.Set();

        ApplicationResult<MuMuLifecycleOutcome> firstResult = await first;
        Assert.True(firstResult.IsSuccess);
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Gate сериализует аренды одного instance и не мешает разным")]
    public async Task GateSerializesSameInstanceAndAllowsDifferentOnes()
    {
        MuMuInstanceMutationGate gate = new();
        MuMuInstanceId first = MuMuInstanceId.FromIndex("1");
        MuMuInstanceId second = MuMuInstanceId.FromIndex("2");

        IDisposable firstLease = await gate.AcquireAsync(first, CancellationToken.None);
        Assert.Equal(1, gate.TrackedInstanceCount);

        IDisposable secondLease = await gate.AcquireAsync(second, CancellationToken.None);
        Assert.Equal(2, gate.TrackedInstanceCount);

        ValueTask<IDisposable> queued = gate.AcquireAsync(first, CancellationToken.None);
        Assert.False(queued.IsCompleted);

        firstLease.Dispose();

        IDisposable queuedLease = await queued;
        Assert.Equal(2, gate.TrackedInstanceCount);

        secondLease.Dispose();
        Assert.Equal(1, gate.TrackedInstanceCount);

        queuedLease.Dispose();
        Assert.Equal(0, gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Отменённая аренда gate не оставляет отслеживаемых записей")]
    public async Task CancelledGateLeaseLeavesNoTrackedEntries()
    {
        MuMuInstanceMutationGate gate = new();
        MuMuInstanceId instance = MuMuInstanceId.FromIndex("1");

        IDisposable lease = await gate.AcquireAsync(instance, CancellationToken.None);
        using CancellationTokenSource source = new();

        ValueTask<IDisposable> queued = gate.AcquireAsync(instance, source.Token);
        source.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.Equal(1, gate.TrackedInstanceCount);

        lease.Dispose();
        Assert.Equal(0, gate.TrackedInstanceCount);
    }
}
