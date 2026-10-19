using System.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Game;
using Xunit;

namespace AzurPilot.Tests.Game;

[Collection(InteropCollection.Name)]
public sealed class GameUiRecognizerTests
{
    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void RedactedUiFixturesRecognizeIndependentStatesAtEveryScale(int height)
    {
        GameUiRecognizer recognizer = new(GameAssetCatalog.Default);
        foreach (string state in new[] { "main", "dock", "selection" })
        {
            using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(
                Path.Combine(PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", $"{state}-{height}.png")));
            GameFrameProvenance provenance = Provenance(frame);
            GameUiObservation observation = recognizer.Observe(frame, provenance, ["main.dock", "main.depot", "dock.header", "dock.selection-confirm"]);
            Assert.Same(provenance, observation.Frame);
            Assert.Equal(state == "main" ? GameMatchStatus.Unique : GameMatchStatus.Absent, observation.Find("main.dock")!.Status);
            Assert.Equal(state == "main" ? GameMatchStatus.Unique : GameMatchStatus.Absent, observation.Find("main.depot")!.Status);
            Assert.Equal(state != "main" ? GameMatchStatus.Unique : GameMatchStatus.Absent, observation.Find("dock.header")!.Status);
            Assert.Equal(state == "selection" ? GameMatchStatus.Unique : GameMatchStatus.Absent, observation.Find("dock.selection-confirm")!.Status);
            Assert.Null(observation.Find("dock.selection-confirm")!.SafeTapBounds);
            foreach (GameUiMatch match in observation.Matches.Where(m => m.Status == GameMatchStatus.Unique && m.SafeTapBounds is not null))
            {
                GamePixelRect safe = match.SafeTapBounds!.Value;
                GameInputPoint point = new GameGeometry(provenance.Width, provenance.Height).Center(safe);
                Assert.True(match.Bounds!.Value.Contains(point));
                GameAssetVariant variant = GameAssetCatalog.Default.GetAsset(match.AssetId).Variants.Single(v => v.Id == match.VariantId);
                Assert.InRange(match.MeanAbsoluteError, 0, variant.MaximumMeanAbsoluteError);
            }

            Assert.Null(observation.Find("main.depot")!.SafeTapBounds);
        }
    }

    [Theory]
    [InlineData("depot")]
    [InlineData("technology")]
    public void RealOtherPageHeadersNeverProveDock(string page)
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", page + "-2160.png")));
        GameUiObservation observation = new GameUiRecognizer(GameAssetCatalog.Default).Observe(frame, Provenance(frame), ["dock.header"]);
        Assert.Equal(GameMatchStatus.Absent, observation.Find("dock.header")!.Status);
        Assert.Null(observation.Find("dock.header")!.SafeTapBounds);
    }

    // Реальные crops подписи из независимых кадров (иные фоны, чем у исходника шаблона) и другие подписи
    // в той же области: скачивание с цифрами и окно Update поверх загрузки.
    [Theory]
    [InlineData("loading-real-2", GameMatchStatus.Unique)]
    [InlineData("loading-real-3", GameMatchStatus.Unique)]
    [InlineData("loading-real-download", GameMatchStatus.Absent)]
    [InlineData("loading-real-confirm", GameMatchStatus.Absent)]
    public void StartupLoadingCaptionSurvivesIndependentBackgroundsAndRejectsOtherCaptions(string sample, GameMatchStatus expected)
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", sample + "-2160.png")));
        GameUiObservation observation = new GameUiRecognizer(GameAssetCatalog.Default).Observe(frame, Provenance(frame),
            ["loading.startup", "loading.download"]);
        GameUiMatch startup = observation.Find("loading.startup")!;
        Assert.Equal(expected, startup.Status);
        Assert.Null(startup.SafeTapBounds);
        if (expected == GameMatchStatus.Unique)
        {
            Assert.Equal("update-complete", startup.VariantId);
        }

        Assert.Equal(GameMatchStatus.Absent, observation.Find("loading.download")!.Status);
    }

    [Fact]
    public void InvalidGeometryAndDisposedFrameNeverReachMatcher()
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(PinnedVersions.RepositoryRoot, "native", "tests", "fixtures", "rgb_red_blue.png")));
        GameUiRecognizer recognizer = new(GameAssetCatalog.Default);
        Assert.Equal(GameMatchStatus.GeometryRejected, recognizer.Observe(frame, Provenance(frame), ["main.dock"]).Matches[0].Status);
        frame.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => recognizer.Observe(frame, ProvenanceForSize(3840, 2160), ["main.dock"]));
        _ = Assert.Throws<ObjectDisposedException>(() => frame.MatchTemplate(new(0, 0, 2, 1), [], 1, 1, NativeTemplateMode.Rgb));
    }

    internal static GameFrameProvenance Provenance(NativeFrame frame)
    {
        NativeFrameInfo info = frame.GetInfo();
        return ProvenanceForSize((int)info.Width, (int)info.Height);
    }

    internal static GameFrameProvenance ProvenanceForSize(int width, int height)
    {
        return new(Guid.NewGuid(), MuMuInstanceId.FromIndex("7"), new AndroidEndpoint("test-host", 16419),
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), width, height);
    }
}
