using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства границ логирования MuMu lifecycle.
/// </summary>
/// <remarks>
/// События bounded: результат выбора экземпляра, запрошенная операция, доказанный postcondition и отказ.
/// Каждый poll не логируется на <see cref="LogLevel.Information"/>, а полный вывод control utility и
/// полное observation evidence не попадают ни в итог, ни в логи.
/// </remarks>
public sealed class MuMuLifecycleLoggingTests
{
    [Fact(DisplayName = "Poll evidence логируется максимум на Debug, а не на Information")]
    public async Task PollEvidenceIsNotLoggedAtInformation()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);

        IReadOnlyList<MuMuLogRecord> records = context.Logger.Records;

        // На Information приходятся только запрос операции и, при отказе, одно событие отказа: каждый
        // poll идёт на Debug, поэтому Information-записей на порядки меньше числа опросов.
        Assert.Equal(1, records.Count(record => record.Level == LogLevel.Information));
        Assert.Equal(1, records.Count(record => record.Level == LogLevel.Error));
        Assert.True(
            records.Count(record => record.Level == LogLevel.Debug) > 1,
            "Poll evidence должен логироваться на Debug, а не исчезать совсем.");
        Assert.True(context.Host.ObservationCount > records.Count(record => record.Level == LogLevel.Information));
    }

    [Fact(DisplayName = "Полный вывод control utility и полное observation evidence не попадают ни в итог, ни в логи")]
    public async Task FullControlOutputNeverLeaks()
    {
        MuMuLifecycleTestContext context = new();
        string longObservationEvidence = "observation-" + new string('o', 4096);
        string longControlOutput = "control-output-" + new string('c', 4096);

        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
            MuMuLifecycleState.Stopped,
            longObservationEvidence);
        context.Host.MutationHandler = _ =>
        {
            context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(
                MuMuLifecycleState.Running,
                longObservationEvidence);
            return MuMuLifecycleTestContext.Command(0, longControlOutput);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;

        Assert.DoesNotContain(longControlOutput, outcome.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(longObservationEvidence, outcome.Evidence, StringComparison.Ordinal);
        Assert.True(
            outcome.Evidence.Length < longObservationEvidence.Length,
            "Evidence итога должно быть ограничено, а не повторять полное observation evidence.");

        Assert.DoesNotContain(
            context.Logger.Records,
            record => record.Message.Contains(longControlOutput, StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.Logger.Records,
            record => record.Message.Contains(longObservationEvidence, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Отказ lifecycle логируется bounded стабильным кодом")]
    public async Task FailureIsLoggedWithStableCode()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            MuMuLifecycleTestContext.Instance("1"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        MuMuLogRecord failureRecord = context.Logger.Records.Single(record => record.Level == LogLevel.Error);
        Assert.Contains(ApplicationFailure.MuMuLifecycleTimeout, failureRecord.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Успешная операция логирует доказанный postcondition и выбранный instance")]
    public async Task SuccessAndSelectionAreLogged()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1", "Экземпляр для выбора");
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([instance]);
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto());
        Assert.True(resolution.IsResolved);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);
        Assert.True(result.IsSuccess);

        IReadOnlyList<MuMuLogRecord> records = context.Logger.Records;
        Assert.Contains(
            records,
            record => record.Message.Contains("mumu:1", StringComparison.Ordinal));
        Assert.Contains(
            records,
            record => record.Level == LogLevel.Information
                && record.Message.Contains("running", StringComparison.Ordinal));
        Assert.Equal(0, context.Host.MutationCount);
    }
}
