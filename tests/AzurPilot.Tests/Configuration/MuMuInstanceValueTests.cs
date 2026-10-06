using AzurPilot.Core.Configuration;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает синтаксис значения <c>mumu.instance</c> схемы v2: единственный владелец правила —
/// <see cref="MuMuInstanceValue"/>, поэтому проверки идут через него, а не через вторую копию грамматики.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class MuMuInstanceValueTests
{
    [Fact(DisplayName = "Владелец правила называет литерал auto и префикс провайдерской формы")]
    public void OwnerDeclaresLiteralAndPrefix()
    {
        Assert.Equal("auto", MuMuInstanceValue.AutoValue);
        Assert.Equal("mumu:", MuMuInstanceValue.ProviderPrefix);
    }

    [Theory(DisplayName = "Допустимое значение mumu.instance принимается владельцем правила")]
    [InlineData("auto")]
    [InlineData("mumu:0")]
    [InlineData("mumu:1")]
    [InlineData("mumu:12")]
    [InlineData("mumu:1234567890")]
    public void ValidValuesAreAccepted(string value)
    {
        Assert.True(MuMuInstanceValue.IsValid(value), $"Значение «{value}» обязано быть допустимым.");
    }

    [Theory(DisplayName = "Недопустимое значение mumu.instance отвергается владельцем правила")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Auto")]
    [InlineData("AUTO")]
    [InlineData(" auto")]
    [InlineData("auto ")]
    [InlineData("automatic")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("mumu")]
    [InlineData("mumu:")]
    [InlineData("mumu:01")]
    [InlineData("mumu:00")]
    [InlineData("mumu:-1")]
    [InlineData("mumu:+1")]
    [InlineData("mumu:1.0")]
    [InlineData("mumu:1a")]
    [InlineData("mumu: 1")]
    [InlineData("mumu:1 ")]
    [InlineData("mumu:１２")]
    [InlineData("MuMu Player 6.8.0")]
    public void InvalidValuesAreRejected(string? value)
    {
        Assert.False(MuMuInstanceValue.IsValid(value), $"Значение «{value}» не является частью схемы v2.");
    }

    [Fact(DisplayName = "Литерал auto сравнивается с учётом регистра: Auto и AUTO схемой не являются")]
    public void AutoLiteralIsComparedWithOrdinalSemantics()
    {
        Assert.True(MuMuInstanceValue.IsAuto(MuMuInstanceValue.AutoValue));
        Assert.False(MuMuInstanceValue.IsAuto("Auto"));
        Assert.False(MuMuInstanceValue.IsAuto("AUTO"));
        Assert.False(MuMuInstanceValue.IsAuto("auto "));
        Assert.False(MuMuInstanceValue.IsAuto(null));
        Assert.False(MuMuInstanceValue.IsAuto("mumu:0"));
    }
}
