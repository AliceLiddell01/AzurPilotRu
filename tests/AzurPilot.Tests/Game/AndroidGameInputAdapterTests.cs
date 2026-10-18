using System.Diagnostics;
using AzurPilot.Core.Game;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows.Android;
using Xunit;

namespace AzurPilot.Tests.Game;

public sealed class AndroidGameInputAdapterTests
{
    [Fact]
    public async Task PermitUsesBundledExactTargetOnceAndCannotBeReplayed()
    {
        FakeWindowsProcessRunner runner = new();
        AndroidWindowsHost host = new(runner);
        runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        Assert.True(host.DiscoverAdbExecutable(AndroidTestContext.Installation).IsSuccess);
        GameFrameProvenance frame = GameUiRecognizerTests.ProvenanceForSize(3840, 2160);
        GameTap tap = new(frame, "test", new(10, 20, 100, 80), new(50, 40), TimeSpan.FromSeconds(12));
        runner.EnqueueOutcome(0, "");
        Assert.True((await host.TapGameAsync(tap, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await host.TapGameAsync(tap, TestContext.Current.CancellationToken)).IsFailure);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(AndroidInstallationLayout.GetAdbExecutablePath(AndroidTestContext.Installation.InstallRoot),
            runner.Requests[1].Request.ExecutablePath);
        Assert.Equal(AdbCommandBuilder.BuildTapArguments(frame.Endpoint, new(50, 40)), runner.Requests[1].Request.Arguments);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("outside-safe")]
    [InlineData("outside-frame")]
    public async Task InvalidPermitNeverStartsAdb(string scenario)
    {
        FakeWindowsProcessRunner runner = new();
        AndroidWindowsHost host = new(runner);
        GameFrameProvenance frame = GameUiRecognizerTests.ProvenanceForSize(3840, 2160);
        frame = scenario switch
        {
            "stale" => frame with { CaptureStartedTimestamp = Stopwatch.GetTimestamp() - (Stopwatch.Frequency * 13) },
            "future" => frame with { CaptureStartedTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency },
            _ => frame,
        };
        GameInputPoint point = scenario == "outside-frame" ? new(3840, 2160) : new(50, 40);
        GamePixelRect safe = scenario == "outside-safe" ? new(0, 0, 10, 10) : new(10, 20, 4000, 2200);
        GameTap tap = new(frame, "test", safe, point, TimeSpan.FromSeconds(12));
        Assert.True((await host.TapGameAsync(tap, TestContext.Current.CancellationToken)).IsFailure);
        Assert.Empty(runner.Requests);
    }
}
