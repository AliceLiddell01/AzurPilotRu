using System.Text.Json;
using AzurPilot.App.OperatorUi;
using AzurPilot.TuiHarness;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class LiveEnvironmentAndTerminalRendererTests
{
    [Fact]
    public async Task StopAndObserve_PersistsPostflightWhenUiStopFails()
    {
        using JsonDocument document = JsonDocument.Parse("""{"status":"success","target":"mumu:7","data":{"state":"Stopped"}}""");
        JsonElement observation = document.RootElement.Clone();
        InvalidOperationException stopFailure = new("Ошибка шага остановки через UI.");
        bool stopAttempted = false;
        bool postflightObserved = false;
        bool postflightPersisted = false;

        Exception? observedStopFailure = await LiveEnvironment.StopAndObserveAsync(
            () =>
            {
                stopAttempted = true;
                return Task.FromException(stopFailure);
            },
            () =>
            {
                postflightObserved = true;
                return Task.FromResult(observation);
            },
            postflight =>
            {
                postflightPersisted = postflight.GetProperty("data").GetProperty("state").GetString() == "Stopped";
                return Task.CompletedTask;
            }, "mumu:7");

        Assert.True(stopAttempted);
        Assert.True(postflightObserved);
        Assert.True(postflightPersisted);
        Assert.Same(stopFailure, observedStopFailure);
    }

    [Fact]
    public async Task StopAndObserve_PersistsPostflightBeforeRejectingRunningTarget()
    {
        using JsonDocument document = JsonDocument.Parse("""{"status":"success","target":"mumu:7","data":{"state":"Running"}}""");
        JsonElement observation = document.RootElement.Clone();
        bool postflightPersisted = false;

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(() => LiveEnvironment.StopAndObserveAsync(
            () => Task.CompletedTask,
            () => Task.FromResult(observation),
            _ =>
            {
                postflightPersisted = true;
                return Task.CompletedTask;
            }, "mumu:7"));

        Assert.True(postflightPersisted);
    }

    [Theory]
    [InlineData("mumu:8", "success", "Stopped")]
    [InlineData("mumu:7", "failure", "Stopped")]
    [InlineData("mumu:7", "success", "Unknown")]
    [InlineData("mumu:7", "success", "Running")]
    public void LivePreflightRequiresStoppedAndExactEnvelopeTarget(string target, string status, string state)
    {
        JsonElement observation = JsonSerializer.SerializeToElement(new { target, status, data = new { state } });
        _ = Assert.Throws<HarnessBlockedException>(() => LiveEnvironment.RequireStopped(observation, "mumu:7"));
    }

    [Theory]
    [InlineData("Операция: нет", true)]
    [InlineData("Операция: завершена", true)]
    [InlineData("Операция: отказ", true)]
    [InlineData("Операция: отменена", true)]
    [InlineData("Операция: ошибка", true)]
    [InlineData("Операция: выполняется", false)]
    [InlineData("Запрошена отмена; ожидается завершение операции.", false)]
    [InlineData("", false)]
    public void WaitIdleClassifier_RecognizesOnlyTerminalOperationStates(string operation, bool expectedIdle)
    {
        Assert.Equal(expectedIdle, TerminalRenderer.IsOperationIdle(operation));
    }

    [Fact]
    public void WaitIdleClassifierRecognizesProductionTerminalOperationTexts()
    {
        string[] terminalStates =
        [
            OperatorUiController.OperationIdleText,
            OperatorUiController.OperationCompletedText,
            OperatorUiController.OperationRejectedText,
            OperatorUiController.OperationCancelledText,
            OperatorUiController.OperationErrorText,
        ];

        Assert.All(terminalStates, state => Assert.True(TerminalRenderer.IsOperationIdle(state), state));
    }
}
