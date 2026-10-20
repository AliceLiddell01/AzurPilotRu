using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Capture;
using Xunit;

namespace AzurPilot.Tests.Capture;

/// <summary>Выбор транспорта production-маршрутизатором поверх fake process boundary и реального PNG decoder.</summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "Capture")]
public sealed class ScreenshotCaptureRouterTests : IDisposable
{
    private static readonly MuMuInstanceId InstanceId = MuMuInstanceId.FromIndex("4");
    private static readonly AndroidEndpoint Endpoint = new("router-host", 16432);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "azurpilot-router-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact(DisplayName = "auto без калибровки использует ADB как bootstrap и сообщает причину")]
    public async Task AutomaticWithoutCalibrationBootstrapsWithAdb()
    {
        Fixture fixture = new(_directory, ScreenshotMethodSetting.Automatic);
        fixture.EnqueueFacts();
        fixture.EnqueuePng();

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.CaptureAsync();

        Assert.True(result.IsSuccess, result.IsFailure ? result.FailureInfo!.Code : null);
        using CapturedAndroidScreenshot screenshot = result.Value!;
        Assert.Equal(ScreenshotMethod.Adb, screenshot.Info.Method);
        Assert.Equal(new ScreenshotSelection(InstanceId, "auto", ScreenshotMethod.Adb, "bootstrap_calibration_missing"),
            fixture.Router.LastSelection);
    }

    [Fact(DisplayName = "auto: отказ калиброванного метода — наблюдаемый откат на ADB, повторно метод не пробуется")]
    public async Task CalibratedMethodFailureFallsBackVisibly()
    {
        Fixture fixture = new(_directory, ScreenshotMethodSetting.Automatic);
        fixture.EnqueueFacts();
        ApplicationResult<string> fingerprint = await fixture.Router.FingerprintAsync(InstanceId, Endpoint, CancellationToken.None);
        Assert.True(fingerprint.IsSuccess);
        fixture.Router.Calibration.Save(new ScreenshotCalibration(fingerprint.Value!, "nemu_ipc", 2, 1,
            DateTimeOffset.UtcNow, "test-run", 10));
        fixture.EnqueuePng();
        fixture.EnqueuePng();

        using (CapturedAndroidScreenshot first = (await fixture.CaptureAsync()).Value!)
        {
            Assert.Equal(ScreenshotMethod.Adb, first.Info.Method);
            ScreenshotSelection selection = fixture.Router.LastSelection!;
            Assert.Equal(ScreenshotMethod.NemuIpc, selection.FallbackFrom);
            Assert.StartsWith("method_failed:", selection.Reason, StringComparison.Ordinal);
        }

        using CapturedAndroidScreenshot second = (await fixture.CaptureAsync()).Value!;
        Assert.Equal(ScreenshotMethod.Adb, second.Info.Method);
        Assert.Equal("calibrated_method_rejected_in_process", fixture.Router.LastSelection!.Reason);
    }

    [Fact(DisplayName = "Явный метод не подменяется: UNSUPPORTED возвращается без захвата ADB")]
    public async Task ExplicitMethodFailureIsNotReplaced()
    {
        Fixture fixture = new(_directory, ScreenshotMethodSetting.Explicit(ScreenshotMethod.NemuIpc));
        fixture.EnqueueFacts();

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.CaptureAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.ScreenshotMethodUnsupported, result.FailureInfo!.Code);
        Assert.Equal("nemu_ipc", result.FailureInfo.Details!["method"]);
        Assert.Equal("UNSUPPORTED", result.FailureInfo.Details["verdict"]);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Request.Arguments.Contains("screencap"));
    }

    [Fact(DisplayName = "Отпечаток зависит от identity и фактов Android; калибровка ищется по нему")]
    public async Task FingerprintChangesWithDeviceFacts()
    {
        Fixture first = new(_directory, ScreenshotMethodSetting.Automatic);
        first.EnqueueFacts("34", "14");
        Fixture second = new(_directory, ScreenshotMethodSetting.Automatic);
        second.EnqueueFacts("35", "15");

        string a = (await first.Router.FingerprintAsync(InstanceId, Endpoint, CancellationToken.None)).Value!;
        string b = (await second.Router.FingerprintAsync(InstanceId, Endpoint, CancellationToken.None)).Value!;

        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
        first.Router.Calibration.Save(new ScreenshotCalibration(a, "ADB", 2, 1, DateTimeOffset.UtcNow, "run", 1));
        Assert.NotNull(second.Router.Calibration.Find(a));
        Assert.Null(second.Router.Calibration.Find(b));
        second.Router.Calibration.Invalidate(a);
        Assert.Null(first.Router.Calibration.Find(a));
    }

    private sealed class Fixture
    {
        internal Fixture(string directory, ScreenshotMethodSetting setting)
        {
            string installRoot = Path.Combine(directory, "install-" + Guid.NewGuid().ToString("N"));
            Installation = new MuMuInstallation("test-version", installRoot, Path.Combine(installRoot, "control.exe"));
            Runner = new FakeWindowsProcessRunner();
            Android = new AndroidWindowsHost(Runner, TimeProvider.System);
            Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
            Assert.True(Android.DiscoverAdbExecutable(Installation).IsSuccess);
            Router = new ScreenshotCaptureRouter(Android, TimeProvider.System, setting,
                new ScreenshotCalibrationStore(Path.Combine(directory, "calibration.json")));
        }

        internal MuMuInstallation Installation { get; }

        internal FakeWindowsProcessRunner Runner { get; }

        internal AndroidWindowsHost Android { get; }

        internal ScreenshotCaptureRouter Router { get; }

        internal void EnqueueFacts(string sdk = "35", string release = "15")
            => Runner.EnqueueOutcome(0, sdk + "\nx86_64\n" + release + "\n");

        internal void EnqueuePng()
            => Runner.EnqueueBinaryOutcome(File.ReadAllBytes(Path.Combine(PinnedVersions.RepositoryRoot, "native", "tests",
                "fixtures", "rgb_red_blue.png")));

        internal Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureAsync()
            => Router.CaptureScreenshotAsync(InstanceId, Endpoint, CancellationToken.None);
    }
}
