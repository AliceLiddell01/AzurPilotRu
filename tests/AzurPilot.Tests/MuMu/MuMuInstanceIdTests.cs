using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства stable identity Android-экземпляра MuMu.
/// </summary>
/// <remarks>
/// Identity — provider-owned vmindex, а не отображаемое имя: каноническая текстовая форма принадлежит
/// контракту, а её префикс читается у владельца синтаксиса значения конфигурации.
/// </remarks>
public sealed class MuMuInstanceIdTests
{
    [Fact(DisplayName = "Каноническая форма identity — mumu:<Index>")]
    public void CanonicalFormIsProviderOwnedIndex()
    {
        MuMuInstanceId id = MuMuInstanceId.FromIndex("7");

        Assert.Equal("7", id.Index);
        Assert.Equal("mumu:7", id.ToString());
        Assert.Equal(MuMuInstanceValue.ProviderPrefix + "7", id.ToString());
    }

    [Fact(DisplayName = "Одинаковый vmindex даёт равные identity, разный — неравные")]
    public void IdentityEqualityFollowsIndex()
    {
        MuMuInstanceId fromFactory = MuMuInstanceId.FromIndex("3");
        MuMuInstanceId fromConstructor = new("3");

        Assert.Equal(fromFactory, fromConstructor);
        Assert.Equal(fromFactory.GetHashCode(), fromConstructor.GetHashCode());
        Assert.NotEqual(fromFactory, MuMuInstanceId.FromIndex("4"));
        Assert.NotEqual(fromFactory, default);
    }

    [Fact(DisplayName = "Отсутствующий vmindex — ошибка программирования вызывающей стороны")]
    public void NullIndexIsRejected()
    {
        _ = Assert.Throws<ArgumentNullException>(() => MuMuInstanceId.FromIndex(null!));
        _ = Assert.Throws<ArgumentNullException>(() => new MuMuInstanceId(null!));
    }

    [Theory(DisplayName = "Пустой или пробельный vmindex отклоняется")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void BlankIndexIsRejected(string index)
    {
        _ = Assert.Throws<ArgumentException>(() => MuMuInstanceId.FromIndex(index));
        _ = Assert.Throws<ArgumentException>(() => new MuMuInstanceId(index));
    }
}
