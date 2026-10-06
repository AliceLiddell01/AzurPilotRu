using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства семантики выбора экземпляра: автоматический выбор и явный выбор по stable identity.
/// </summary>
/// <remarks>
/// Тип описывает только семантику: синтаксис строки конфигурации принадлежит конфигурации, поэтому
/// проверяется, что сервис не разбирает строки и не знает их формы.
/// </remarks>
public sealed class MuMuInstanceSelectionTests
{
    [Fact(DisplayName = "Auto() означает автоматический выбор без явной identity")]
    public void AutoCarriesNoExplicitIdentity()
    {
        MuMuInstanceSelection selection = MuMuInstanceSelection.Auto();

        Assert.True(selection.IsAutomatic);
        Assert.Null(selection.ExplicitId);
    }

    [Fact(DisplayName = "Explicit(id) означает явный выбор по stable identity")]
    public void ExplicitCarriesIdentity()
    {
        MuMuInstanceId id = MuMuInstanceId.FromIndex("2");
        MuMuInstanceSelection selection = MuMuInstanceSelection.Explicit(id);

        Assert.False(selection.IsAutomatic);
        Assert.Equal(id, selection.ExplicitId);
    }

    [Fact(DisplayName = "Selection сравнивается по значению")]
    public void SelectionIsValueEqual()
    {
        Assert.Equal(MuMuInstanceSelection.Auto(), MuMuInstanceSelection.Auto());
        Assert.Equal(
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("1")),
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("1")));
        Assert.NotEqual(
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("1")),
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("2")));
    }
}
