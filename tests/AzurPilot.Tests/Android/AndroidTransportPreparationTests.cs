using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>Контракты отдельной явной подготовки transport на exact endpoint.</summary>
public sealed class AndroidTransportPreparationTests
{
    [Fact(DisplayName = "Готовый transport подтверждается без лишнего connect")]
    public async Task ReadyTransportDoesNotConnectAgain()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Device());
        context.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 1));

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsSuccess);
        AndroidTransportPreparationOutcome outcome = result.Value!;
        Assert.Equal(AndroidTransportState.Device, outcome.InitialTransport);
        Assert.Equal(AndroidTransportState.Device, outcome.FinalTransport);
        Assert.False(outcome.ConnectAttempted);
        Assert.Empty(context.Host.ConnectRequests);
        Assert.Equal([AndroidTestContext.Endpoint], context.Host.TransportRequests);
        Assert.Equal([AndroidTestContext.Endpoint], context.Host.BootRequests);
    }

    [Fact(DisplayName = "Absent выполняет ровно один connect exact endpoint и подтверждает postconditions")]
    public async Task AbsentConnectsOnceAndObservesReadiness()
    {
        AndroidTestContext context = new();
        int transportPolls = 0;
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            ++transportPolls == 1
                ? AndroidTestContext.Transport(AndroidTransportState.Absent)
                : AndroidTestContext.Device());
        context.Host.ConnectHandler = (endpoint, _) =>
            AndroidTestContext.Command(exitCode: 1);
        context.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 1));

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsSuccess);
        AndroidTransportPreparationOutcome outcome = result.Value!;
        Assert.Equal(AndroidTransportState.Absent, outcome.InitialTransport);
        Assert.Equal(AndroidTransportState.Device, outcome.FinalTransport);
        Assert.True(outcome.ConnectAttempted);
        Assert.Equal([AndroidTestContext.Endpoint], context.Host.ConnectRequests);
        Assert.Equal(2, context.Host.TransportRequests.Count);
        Assert.Equal([AndroidTestContext.Endpoint], context.Host.BootRequests);
        Assert.Contains("connect_attempted=true", outcome.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Текст или exit code connect не заменяет readiness postconditions")]
    public async Task ConnectSuccessWithoutPostconditionsIsBlocked()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(AndroidTransportState.Absent));
        context.Host.ConnectHandler = (_, _) =>
            AndroidTestContext.Command();

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidTransportNotReady, result.FailureInfo!.Code);
        Assert.Equal("absent", result.FailureInfo.Details!["state"]);
        Assert.Equal("absent", result.FailureInfo.Details["initial_transport"]);
        Assert.Equal([AndroidTestContext.Endpoint], context.Host.ConnectRequests);
        Assert.Empty(context.Host.BootRequests);
    }

    [Theory(DisplayName = "Offline и Unknown отказывают без connect")]
    [InlineData(AndroidTransportState.Offline, "offline")]
    [InlineData(AndroidTransportState.Unknown, "unknown")]
    public async Task UnusableInitialTransportDoesNotConnect(AndroidTransportState state, string expectedState)
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(state));

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidTransportNotReady, result.FailureInfo!.Code);
        Assert.Equal(expectedState, result.FailureInfo.Details!["state"]);
        Assert.Equal(expectedState, result.FailureInfo.Details["initial_transport"]);
        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.BootRequests);
    }

    [Fact(DisplayName = "Device с недоказанной загрузкой не получает connect и не становится готовым")]
    public async Task DeviceWithoutCompletedBootDoesNotConnect()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Device());
        context.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 0));

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidNotReady, result.FailureInfo!.Code);
        Assert.Equal("device", result.FailureInfo.Details!["initial_transport"]);
        Assert.Empty(context.Host.ConnectRequests);
        Assert.Equal(
            (int)(context.Timings.ReadinessDeadline / context.Timings.PollInterval),
            context.Host.BootRequests.Count);
    }

    [Fact(DisplayName = "Отмена после наблюдения Absent останавливает подготовку до connect")]
    public async Task CancellationAfterAbsentObservationDoesNotConnect()
    {
        AndroidTestContext context = new();
        using CancellationTokenSource source = new();
        context.Host.TransportHandler = _ =>
        {
            source.Cancel();
            return ApplicationResult<AndroidTransportObservation>.Success(
                AndroidTestContext.Transport(AndroidTransportState.Absent));
        };

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await context.Readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal("absent", result.FailureInfo.Details!["initial_transport"]);
        Assert.Empty(context.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Отмена подготовки логируется с именем prepare_transport")]
    public async Task CancellationDuringPrepareLogsPreparationOperation()
    {
        AndroidTestContext context = new();
        using CancellationTokenSource cancellation = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(AndroidTransportState.Absent));
        context.Host.ConnectHandler = (_, _) =>
        {
            cancellation.Cancel();
            return AndroidTestContext.Command();
        };
        RecordingAndroidReadinessLogger logger = new();
        AndroidReadinessService readiness = new(
            context.Host,
            context.Timings,
            context.TimeProvider,
            logger);

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal("absent", result.FailureInfo.Details!["initial_transport"]);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("операция prepare_transport", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Отмена первичного запроса transport не выдумывает initial_transport")]
    public async Task CancellationBeforeInitialTransportObservationKeepsTransportUnknown()
    {
        AndroidTestContext context = new();
        context.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Failure(
            new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = "transport-query-cancelled",
            });
        RecordingAndroidReadinessLogger logger = new();
        AndroidReadinessService readiness = new(
            context.Host,
            context.Timings,
            context.TimeProvider,
            logger);

        ApplicationResult<AndroidTransportPreparationOutcome> result =
            await readiness.PrepareTransportAsync(AndroidTestContext.Endpoint, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.DoesNotContain("initial_transport", result.FailureInfo.Details!.Keys);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("операция prepare_transport", StringComparison.Ordinal));
    }

    private sealed class RecordingAndroidReadinessLogger : ILogger<AndroidReadinessService>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}
