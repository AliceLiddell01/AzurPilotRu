using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства orchestration готовности Android: ровно одно target-local подключение, bounded ожидание
/// готовности transport и загрузки Android, deadline, отмена и read-only наблюдение.
/// </summary>
/// <remarks>
/// <para>
/// Проверки идут через настоящий <see cref="AzurPilot.Core.Android.Orchestration.AndroidReadinessService"/>
/// на подменяемой host-границе: подменяются ответы границы, а не orchestration. Время идёт через
/// управляемый источник, поэтому deadline достигается без wall-clock ожидания, а число наблюдений
/// выводится из чисел времени их владельца.
/// </para>
/// <para>
/// Код выхода команды подключения доказательством не является: готовность доказывает наблюдение
/// состояния transport, поэтому неудачное подключение не отменяет операцию и не повторяется.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidReadinessOrchestrationTests
{
    [Fact(DisplayName = "Разрешение endpoint-а адресует точную identity выбранного экземпляра")]
    public void ResolveEndpointAddressesRequestedIdentity()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, instance) =>
            instance == AndroidTestContext.Instance
                ? ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint)
                : ApplicationResult<AndroidEndpoint>.Failure(Failure(ApplicationFailure.AndroidEndpointUnavailable));

        ApplicationResult<AndroidEndpoint> result =
            context.Readiness.ResolveEndpoint(AndroidTestContext.Installation, AndroidTestContext.Instance);

        Assert.True(result.IsSuccess);
        Assert.Equal(AndroidTestContext.Endpoint, result.Value!);

        MuMuInstanceId[] expectedRequests = [AndroidTestContext.Instance];

        Assert.Equal(expectedRequests, context.Host.EndpointRequests);
    }

    [Fact(DisplayName = "Отказ разрешения endpoint-а пробрасывается без изменений")]
    public void ResolveEndpointPropagatesFailure()
    {
        AndroidTestContext context = new();
        ApplicationFailure failure = Failure(ApplicationFailure.AndroidEndpointUnavailable);
        context.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(failure);

        ApplicationResult<AndroidEndpoint> result =
            context.Readiness.ResolveEndpoint(AndroidTestContext.Installation, AndroidTestContext.Instance);

        Assert.True(result.IsFailure);
        Assert.Equal(failure, result.FailureInfo!);
        Assert.Empty(context.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Готовность доказывается наблюдением transport и загрузки Android")]
    public async Task ReadinessIsProvenByObservation()
    {
        AndroidTestContext context = new();
        int transportPolls = 0;
        int bootPolls = 0;

        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => ++transportPolls < 2
            ? TransportResult(AndroidTransportState.Offline)
            : DeviceResult();
        context.Host.BootHandler = _ => ++bootPolls < 2
            ? BootResult(shellAvailable: false, bootCompleted: 0)
            : BootResult(shellAvailable: true, bootCompleted: 1);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AndroidReadinessOutcome outcome = result.Value!;

        Assert.Equal(AndroidTestContext.Endpoint, outcome.Endpoint);
        Assert.Equal(1, outcome.Boot.BootCompleted);
        Assert.True(outcome.Boot.ShellAvailable);
        Assert.Contains("transport=device", outcome.Evidence, StringComparison.Ordinal);
        Assert.Contains("boot=1", outcome.Evidence, StringComparison.Ordinal);
        Assert.Contains("release=15.0", outcome.Evidence, StringComparison.Ordinal);
        Assert.Contains("sdk=35", outcome.Evidence, StringComparison.Ordinal);

        // Mutation transport выполняется ровно одна и только для выбранного endpoint-а.
        AndroidEndpoint[] expectedConnect = [AndroidTestContext.Endpoint];

        Assert.Equal(expectedConnect, context.Host.ConnectRequests);
        Assert.Equal(2, context.Host.TransportRequests.Count);
        Assert.Equal(2, context.Host.BootRequests.Count);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Неудачное подключение не отменяет операцию и не повторяется")]
    public async Task FailedConnectIsNotProofAndIsNotRetried()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) =>
            ApplicationResult<AndroidCommandOutcome>.Failure(Failure(ApplicationFailure.AndroidEndpointUnavailable));
        context.Host.TransportHandler = _ => DeviceResult();
        context.Host.BootHandler = _ => BootResult(shellAvailable: true, bootCompleted: 1);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        _ = Assert.Single(context.Host.ConnectRequests);
        _ = Assert.Single(context.Host.TransportRequests);
        _ = Assert.Single(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Готовность transport не доказывается в пределах границы ожидания")]
    public async Task TransportNotReadyIsReportedWithObservedState()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => TransportResult(AndroidTransportState.Offline);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AndroidTransportNotReady, failure.Code);
        Assert.Equal(AndroidTestContext.Endpoint.ToString(), failure.Details![AndroidDetailKeys.Endpoint]);
        Assert.Equal(AndroidDetailValues.Offline, failure.Details[AndroidDetailKeys.State]);
        Assert.Equal(AndroidDetailValues.TransportPhase, failure.Details[AndroidDetailKeys.Phase]);

        // Ожидание transport ограничено его собственной границей, и наблюдение готовности Android
        // после него не выполняется вовсе.
        Assert.Equal(
            (int)(context.Timings.TransportConnectDeadline / context.Timings.PollInterval),
            context.Host.TransportRequests.Count);
        Assert.Empty(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Истёкшая граница ожидания не отменяет первое наблюдение transport")]
    public async Task ExpiredTransportDeadlineStillObservesTransport()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) =>
        {
            // Подключение transport занимает всё окно ожидания: управляемые часы продвигаются ровно на
            // задержку созданного таймера, поэтому к первому наблюдению граница уже истекла.
            using ITimer elapsed = context.TimeProvider.CreateTimer(
                static _ => { },
                null,
                context.Timings.TransportConnectDeadline,
                Timeout.InfiniteTimeSpan);

            return AndroidTestContext.Command();
        };
        context.Host.TransportHandler = _ => DeviceResult();
        context.Host.BootHandler = _ => BootResult(shellAvailable: true, bootCompleted: 1);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        // Доказанно готовый transport не объявляется неготовым без наблюдения: истёкшая граница заканчивает
        // ожидание только после первого наблюдения, поэтому отказ не сообщал бы ненаблюдённое состояние.
        Assert.True(result.IsSuccess);
        _ = Assert.Single(context.Host.TransportRequests);
    }

    [Fact(DisplayName = "Готовность Android не доказывается в пределах deadline")]
    public async Task BootNotReadyIsReportedWithBoundedPolling()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => DeviceResult();
        context.Host.BootHandler = _ => BootResult(shellAvailable: true, bootCompleted: 0);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AndroidNotReady, failure.Code);
        Assert.Equal(AndroidDetailValues.BootPhase, failure.Details![AndroidDetailKeys.Phase]);
        Assert.Contains("shell=available", failure.Details[AndroidDetailKeys.Evidence], StringComparison.Ordinal);
        Assert.Contains("boot=0", failure.Details[AndroidDetailKeys.Evidence], StringComparison.Ordinal);
        Assert.True(failure.IsRetryable);

        // Ожидание готовности Android ограничено общим deadline операции и интервалом опроса.
        Assert.Equal(
            (int)(context.Timings.ReadinessDeadline / context.Timings.PollInterval),
            context.Host.BootRequests.Count);
        _ = Assert.Single(context.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Недоступный shell не выдаётся за готовность Android")]
    public async Task UnavailableShellIsNotReadiness()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => DeviceResult();
        context.Host.BootHandler = _ => BootResult(
            shellAvailable: false,
            bootCompleted: null,
            release: null,
            sdkLevel: null);

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidNotReady, result.FailureInfo!.Code);
        Assert.Contains(
            "shell=unavailable",
            result.FailureInfo!.Details![AndroidDetailKeys.Evidence],
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Отмена до mutation transport не выполняет подключение")]
    public async Task CancellationBeforeConnectDoesNotMutate()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.TransportPhase,
            result.FailureInfo!.Details![AndroidDetailKeys.Phase]);
        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.TransportRequests);
    }

    [Fact(DisplayName = "Отмена во время ожидания transport сообщает фазу transport")]
    public async Task CancellationDuringTransportPollingIsReported()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        using CancellationTokenSource source = new();
        int polls = 0;
        context.Host.TransportHandler = _ =>
        {
            if (++polls == 3)
            {
                source.Cancel();
            }

            return TransportResult(AndroidTransportState.Offline);
        };

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.TransportPhase,
            result.FailureInfo!.Details![AndroidDetailKeys.Phase]);
        Assert.Equal(3, context.Host.TransportRequests.Count);
        Assert.Empty(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Отмена во время ожидания готовности Android сообщает фазу boot")]
    public async Task CancellationDuringBootPollingIsReported()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => DeviceResult();
        using CancellationTokenSource source = new();
        context.Host.BootHandler = _ =>
        {
            source.Cancel();
            return BootResult(shellAvailable: false, bootCompleted: 0);
        };

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(AndroidDetailValues.BootPhase, result.FailureInfo!.Details![AndroidDetailKeys.Phase]);
        _ = Assert.Single(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Отмена, сообщённая host-ом, приводится к существующему коду отмены")]
    public async Task HostCancellationIsReportedAsOperationCancelled()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.ConnectHandler = (_, _) => AndroidTestContext.Command();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Failure(
            Failure(ApplicationFailure.OperationCancelled));

        ApplicationResult<AndroidReadinessOutcome> result = await context.Readiness.EnsureReadyAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.TransportPhase,
            result.FailureInfo!.Details![AndroidDetailKeys.Phase]);
    }

    [Fact(DisplayName = "Read-only наблюдение готовности не выполняет mutation transport")]
    public void ReadOnlyObservationDoesNotMutateTransport()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => DeviceResult();
        context.Host.BootHandler = _ => BootResult(shellAvailable: true, bootCompleted: 1);

        ApplicationResult<AndroidReadinessFacts> result = context.Readiness.Observe(AndroidTestContext.Endpoint);

        Assert.True(result.IsSuccess);

        AndroidReadinessFacts facts = result.Value!;

        Assert.Equal(AndroidTestContext.Endpoint, facts.Endpoint);
        Assert.Equal(AndroidTransportState.Device, facts.Transport.State);
        Assert.NotNull(facts.Boot);
        Assert.Equal(1, facts.Boot!.BootCompleted);

        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.MutationRequests);
        _ = Assert.Single(context.Host.TransportRequests);
        _ = Assert.Single(context.Host.BootRequests);
    }

    [Fact(DisplayName = "У неготового transport готовность Android не наблюдается и не додумывается")]
    public void ReadOnlyObservationDoesNotGuessBoot()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => TransportResult(AndroidTransportState.Offline);

        ApplicationResult<AndroidReadinessFacts> offline =
            context.Readiness.Observe(AndroidTestContext.Endpoint);

        Assert.True(offline.IsSuccess);
        Assert.Null(offline.Value!.Boot);

        // Нераспознанная форма ответа — тоже неготовый transport, а не готовый.
        context.Host.TransportHandler = _ => TransportResult(AndroidTransportState.Unknown);

        ApplicationResult<AndroidReadinessFacts> unknown =
            context.Readiness.Observe(AndroidTestContext.Endpoint);

        Assert.True(unknown.IsSuccess);
        Assert.Equal(AndroidTransportState.Unknown, unknown.Value!.Transport.State);
        Assert.Null(unknown.Value!.Boot);

        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Read-only наблюдение по identity экземпляра только разрешает endpoint")]
    public void ReadOnlyObservationByInstanceResolvesEndpointOnly()
    {
        AndroidTestContext context = new();
        context.Host.EndpointHandler = (_, _) =>
            ApplicationResult<AndroidEndpoint>.Success(AndroidTestContext.Endpoint);
        context.Host.TransportHandler = _ => TransportResult(AndroidTransportState.Absent);

        ApplicationResult<AndroidReadinessFacts> result = context.Readiness.Observe(
            AndroidTestContext.Installation,
            AndroidTestContext.Instance);

        Assert.True(result.IsSuccess);
        Assert.Equal(AndroidTransportState.Absent, result.Value!.Transport.State);

        MuMuInstanceId[] expectedRequests = [AndroidTestContext.Instance];

        Assert.Equal(expectedRequests, context.Host.EndpointRequests);
        Assert.Empty(context.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Отказ host-а при read-only наблюдении пробрасывается без изменений")]
    public void ReadOnlyObservationPropagatesHostFailure()
    {
        AndroidTestContext context = new();
        ApplicationFailure failure = Failure(ApplicationFailure.AndroidEndpointUnavailable);
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Failure(failure);

        ApplicationResult<AndroidReadinessFacts> result = context.Readiness.Observe(AndroidTestContext.Endpoint);

        Assert.True(result.IsFailure);
        Assert.Equal(failure, result.FailureInfo!);
        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.BootRequests);
    }

    private static ApplicationFailure Failure(string code)
        => new() { Code = code, Message = "test-failure" };

    private static ApplicationResult<AndroidTransportObservation> DeviceResult()
        => ApplicationResult<AndroidTransportObservation>.Success(AndroidTestContext.Device());

    private static ApplicationResult<AndroidTransportObservation> TransportResult(AndroidTransportState state)
        => ApplicationResult<AndroidTransportObservation>.Success(AndroidTestContext.Transport(state));

    private static ApplicationResult<AndroidBootObservation> BootResult(
        bool shellAvailable,
        int? bootCompleted,
        string? release = "15.0",
        int? sdkLevel = 35)
        => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable, bootCompleted, release, sdkLevel));
}
