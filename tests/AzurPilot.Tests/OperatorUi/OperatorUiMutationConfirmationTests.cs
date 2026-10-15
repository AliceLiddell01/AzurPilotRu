using AzurPilot.App.OperatorUi;
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
}
