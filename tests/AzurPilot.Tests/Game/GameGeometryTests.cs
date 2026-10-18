using AzurPilot.Core.Game;
using Xunit;

namespace AzurPilot.Tests.Game;

public sealed class GameGeometryTests
{
    [Theory]
    [InlineData(720, 1, 1, 3, 5)]
    [InlineData(1080, 1, 1, 5, 8)]
    [InlineData(1440, 2, 2, 6, 10)]
    [InlineData(2160, 3, 3, 9, 15)]
    public void MappingRoundsOutwardAndPreservesFullFrame(int height, int left, int top, int right, int bottom)
    {
        GameGeometry geometry = new(height * 16 / 9, height);
        Assert.Equal(new GamePixelRect(left, top, right, bottom), geometry.Map(new(1, 1, 3, 5)));
        Assert.Equal(new GamePixelRect(0, 0, geometry.Width, height), geometry.Map(new(0, 0, GameGeometry.LogicalWidth, GameGeometry.LogicalHeight)));
        GamePixelRect edge = geometry.Map(new(GameGeometry.LogicalWidth - 1, GameGeometry.LogicalHeight - 1, GameGeometry.LogicalWidth, GameGeometry.LogicalHeight));
        Assert.True(edge.Contains(geometry.Center(edge)));
        Assert.InRange(geometry.Center(edge).X, 0, geometry.Width - 1);
        Assert.InRange(geometry.Center(edge).Y, 0, height - 1);
    }

    [Theory]
    [InlineData(720, 1280)]
    [InlineData(1279, 720)]
    [InlineData(1920, 1081)]
    [InlineData(4096, 2160)]
    [InlineData(7680, 4320)]
    [InlineData(0, 0)]
    public void UnsupportedGeometryNeverClipsOrAssumesLetterboxing(int width, int height)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new GameGeometry(width, height));
    }

    [Fact]
    public void InvalidBoundsAndInputPointAreRejected()
    {
        GameGeometry geometry = new(1920, 1080);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Map(new(-1, 0, 1, 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Map(new(1, 1, 1, 2)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Map(new(0, 0, GameGeometry.LogicalWidth + 1, 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Center(new(0, 0, 1921, 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Center(new(1, 1, 0, 0)));
    }
}
