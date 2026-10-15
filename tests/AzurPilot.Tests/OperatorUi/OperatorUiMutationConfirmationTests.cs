using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorUi;

[Trait("Category", "Operator")]
public sealed class OperatorUiMutationConfirmationTests
{
    [Theory(DisplayName = "Подтверждение не связывает mutation с сохранением выбора")]
    [InlineData(OperatorUiAction.EnvironmentPrepare, "Запустить окружение", "Подтвердить для mumu:7")]
    [InlineData(OperatorUiAction.GameStart, "Запустить игру", "Подтвердить для mumu:7: Запустить игру")]
    public void SavingChoiceIsSeparateFromMutationConfirmation(
        OperatorUiAction action,
        string title,
        string expectedConfirmation)
    {
        IReadOnlyList<(string Title, bool SaveTarget)> choices =
            AzurPilotOperatorUi.MutationConfirmationChoices(
                action, title, MuMuInstanceId.FromIndex("7"), includeSaveChoice: true);

        Assert.Equal(
            [
                (expectedConfirmation, false),
                (expectedConfirmation + " и сохранить выбор", true),
            ],
            choices);
    }

    [Fact(DisplayName = "Сохранение не предлагается, если target уже сохранён")]
    public void UnchangedTargetHasOneMutationConfirmation()
    {
        IReadOnlyList<(string Title, bool SaveTarget)> choices =
            AzurPilotOperatorUi.MutationConfirmationChoices(
                OperatorUiAction.EnvironmentPrepare, "Запустить окружение", MuMuInstanceId.FromIndex("7"),
                includeSaveChoice: false);

        Assert.Equal([("Подтвердить для mumu:7", false)], choices);
    }

    [Fact(DisplayName = "Отложенное подтверждение живёт только до разрешения target и выбора экземпляра")]
    public void PendingConfirmationEndsWhenTargetOrInventoryIsUnavailable()
    {
        Assert.True(AzurPilotOperatorUi.ShouldRetainPendingConfirmation(
            ApplicationFailure.MuMuInstanceAmbiguous,
            inventoryReturned: false,
            instanceCount: 0));
        Assert.False(AzurPilotOperatorUi.ShouldRetainPendingConfirmation(
            ApplicationFailure.MuMuInstanceNotFound,
            inventoryReturned: false,
            instanceCount: 0));
        Assert.False(AzurPilotOperatorUi.ShouldRetainPendingConfirmation(
            targetFailureCode: null,
            inventoryReturned: false,
            instanceCount: 2));
        Assert.False(AzurPilotOperatorUi.ShouldRetainPendingConfirmation(
            targetFailureCode: null,
            inventoryReturned: true,
            instanceCount: 0));
        Assert.True(AzurPilotOperatorUi.ShouldRetainPendingConfirmation(
            targetFailureCode: null,
            inventoryReturned: true,
            instanceCount: 1));
    }
}
