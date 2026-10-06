using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства формы отказов MuMu lifecycle и bounded details.
/// </summary>
/// <remarks>
/// Ожидаемые отказы host-а пробрасываются без изменений; <c>mumu_lifecycle_timeout</c> синтезируется
/// только при достигнутом deadline, <c>mumu_lifecycle_postcondition_not_met</c> — только когда
/// выполненная mutation сообщила отказ своим кодом выхода. <c>internal_error</c> для ожидаемых
/// MuMu-отказов не используется.
/// </remarks>
public sealed class MuMuLifecycleFailureTests
{
    private static readonly string[] PostconditionDetailsKeys =
        ["elapsed_ms", "exit_code", "instance_id", "operation", "phase", "state"];

    private static readonly string[] TimeoutDetailsKeys =
        ["elapsed_ms", "exit_code", "instance_id", "operation", "state", "target_state"];
    [Fact(DisplayName = "Ожидаемый отказ mutation пробрасывается без изменений")]
    public async Task MutationFailureIsPropagatedUnchanged()
    {
        MuMuLifecycleTestContext context = new();
        ApplicationFailure hostFailure = new()
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Control surface установки не поддерживает mutation экземпляра.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["surface"] = "unsupported",
            },
        };
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ =>
            ApplicationResult<MuMuLifecycleCommandOutcome>.Failure(hostFailure);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Same(hostFailure, result.FailureInfo);
        Assert.Equal("unsupported", result.FailureInfo!.Details!["surface"]);
    }

    [Fact(DisplayName = "Отказ начального наблюдения пробрасывается без изменений")]
    public async Task InitialObservationFailureIsPropagatedUnchanged()
    {
        MuMuLifecycleTestContext context = new();
        ApplicationFailure hostFailure = new()
        {
            Code = ApplicationFailure.MuMuInstallationNotFound,
            Message = "Установка MuMuPlayer не обнаружена.",
        };
        context.Host.ObservationResult = ApplicationResult<MuMuInstanceState>.Failure(hostFailure);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Same(hostFailure, result.FailureInfo);
        Assert.Equal(0, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Отказ наблюдения при polling пробрасывается без подмены на postcondition_not_met")]
    public async Task PollingObservationFailureIsPropagatedUnchanged()
    {
        MuMuLifecycleTestContext context = new();
        ApplicationFailure hostFailure = new()
        {
            Code = ApplicationFailure.NativeUnavailable,
            Message = "Native библиотека недоступна.",
        };
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = ApplicationResult<MuMuInstanceState>.Failure(hostFailure);
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Same(hostFailure, result.FailureInfo);
        Assert.Equal(1, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Отказ mutation по коду выхода даёт mumu_lifecycle_postcondition_not_met с bounded details")]
    public async Task FailedMutationWithoutPostconditionGivesPostconditionNotMet()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(9, "control-refused-mutation");

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecyclePostconditionNotMet, failure.Code);
        Assert.False(failure.IsRetryable);

        Assert.Equal("start", failure.Details!["operation"]);
        Assert.Equal("mumu:1", failure.Details!["instance_id"]);
        Assert.Equal("stopped", failure.Details!["state"]);
        Assert.Equal("postcondition_observation", failure.Details!["phase"]);
        Assert.Equal("9", failure.Details!["exit_code"]);
        Assert.Equal(PostconditionDetailsKeys, failure.Details!.Keys.OrderBy(key => key, StringComparer.Ordinal));

        // Mutation выполнена ровно один раз, и полный вывод control utility в details не попал.
        Assert.Equal(1, context.Host.MutationCount);
        Assert.DoesNotContain("control-refused-mutation", failure.Details!.Values);
    }

    [Fact(DisplayName = "Достигнутый deadline даёт mumu_lifecycle_timeout с bounded details")]
    public async Task ReachedDeadlineGivesTimeoutWithBoundedDetails()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0, "control-full-output");

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.True(failure.IsRetryable);

        Assert.Equal("start", failure.Details!["operation"]);
        Assert.Equal("mumu:1", failure.Details!["instance_id"]);
        Assert.Equal("stopped", failure.Details!["state"]);
        Assert.Equal("running", failure.Details!["target_state"]);
        Assert.Equal("0", failure.Details!["exit_code"]);
        Assert.Equal(TimeoutDetailsKeys, failure.Details!.Keys.OrderBy(key => key, StringComparer.Ordinal));

        // Deadline действительно достигнут: elapsed не меньше deadline владельца чисел времени.
        long elapsedMilliseconds = long.Parse(
            failure.Details!["elapsed_ms"],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(elapsedMilliseconds >= (long)context.Timings.StartDeadline.TotalMilliseconds);

        // Launch и его единственный повтор: единый переход не оставляет повторов после него, а полный
        // вывод control utility в details не попадает.
        Assert.Equal(2, context.Host.MutationCount);
        Assert.Equal(
            [MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
        Assert.DoesNotContain("control-full-output", failure.Details!.Values);
    }

    [Fact(DisplayName = "Отмена до начала операции возвращает operation_cancelled без mutation и наблюдения")]
    public async Task CancellationBeforeStartReturnsOperationCancelled()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);

        using CancellationTokenSource source = new();
        source.Cancel();

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            source.Token);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.OperationCancelled, failure.Code);
        Assert.Equal("gate", failure.Details!["phase"]);

        Assert.Equal(0, context.Host.MutationCount);
        Assert.Equal(0, context.Host.ObservationCount);
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Отмена во время polling возвращает operation_cancelled без повторной mutation")]
    public async Task CancellationDuringPollingReturnsOperationCancelled()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);

        using CancellationTokenSource source = new();
        context.Host.MutationHandler = _ =>
        {
            source.Cancel();
            return MuMuLifecycleTestContext.Command(0);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            source.Token);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.OperationCancelled, failure.Code);
        Assert.Equal("polling", failure.Details!["phase"]);
        Assert.Equal(1, context.Host.MutationCount);
    }

    [Fact(DisplayName = "Ожидаемые MuMu-отказы не подменяются internal_error")]
    public async Task ExpectedMuMuFailuresNeverUseInternalError()
    {
        List<string> observedCodes = [];

        MuMuLifecycleTestContext timeoutContext = new();
        timeoutContext.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        timeoutContext.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);
        observedCodes.Add((await timeoutContext.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None)).FailureInfo!.Code);

        MuMuLifecycleTestContext notMetContext = new();
        notMetContext.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        notMetContext.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(1);
        observedCodes.Add((await notMetContext.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None)).FailureInfo!.Code);

        MuMuLifecycleTestContext unsupportedContext = new();
        unsupportedContext.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);
        observedCodes.Add(unsupportedContext.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto()).Failure!.Code);

        Assert.DoesNotContain(ApplicationFailure.InternalError, observedCodes);
        Assert.Equal(
            [
                ApplicationFailure.MuMuLifecycleTimeout,
                ApplicationFailure.MuMuLifecyclePostconditionNotMet,
                ApplicationFailure.MuMuInstanceNotFound,
            ],
            observedCodes);
    }

    [Fact(DisplayName = "Отсутствующие аргументы lifecycle-операции — ошибка программирования")]
    public async Task MissingOperationArgumentsAreRejected()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => context.Service.StartAsync(
            null!,
            instance,
            CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => context.Service.StopAsync(
            MuMuLifecycleTestContext.Installation,
            null!,
            CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => context.Service.RestartAsync(
            null!,
            null!,
            CancellationToken.None));
    }
}
