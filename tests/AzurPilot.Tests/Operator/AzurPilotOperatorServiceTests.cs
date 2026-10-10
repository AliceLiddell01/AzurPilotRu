using AzurPilot.App;
using AzurPilot.App.Diagnostics;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.Diagnostics;
using AzurPilot.Tests.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

[Collection(InteropCollection.Name)]
[Trait("Category", "Operator")]
public sealed class AzurPilotOperatorServiceTests
{
    [Fact(DisplayName = "Screenshot разрешает exact target, сканирует оба production ROI и освобождает capture frame")]
    public async Task CaptureAndScanUsesExactTargetAndDisposesOneCapturedFrameAfterBothScans()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint selectedEndpoint = new("127.0.0.1", 20312);
        MuMuInstance selected = MuMuLifecycleTestContext.Instance("7", "selected");
        MuMuInstance neighbor = MuMuLifecycleTestContext.Instance("3", "neighbor");
        OperatorFixture fixture = CreateFixture(neighbor, selected);
        byte[] png = ScreenshotPngFixture.CreateRgb(
            16,
            9,
            static (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)(x + y)));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, selectedEndpoint));
        fixture.Runner.EnqueueBinaryOutcome(png);

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            selectedId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        AzurPilotOperatorScreenshotAnalysis analysis = result.Value!;
        Assert.Equal(selectedId, analysis.Capture.InstanceId);
        Assert.Equal(selectedEndpoint, analysis.Capture.Endpoint);
        Assert.Equal(png.Length, analysis.Capture.EncodedPngByteCount);
        Assert.Equal(
            new NativeFrameInfo(16, 9, 48, 432, AzurPilotNativeBridge.PixelFormatRgb8),
            analysis.Capture.FrameInfo);
        Assert.Equal(
            [ScreenshotDiagnosticRegion.FullFrame, ScreenshotDiagnosticRegion.Center],
            analysis.Regions.Select(static region => region.Region));
        Assert.Same(analysis.Regions[0].Source, analysis.Regions[1].Source);
        Assert.All(analysis.Regions, region =>
        {
            Assert.Equal(16u, region.FrameWidth);
            Assert.Equal(9u, region.FrameHeight);
            Assert.True(region.PixelCount > 0);
            Assert.Equal(ScreenshotAnalysisSourceKind.AndroidCapture, region.Source.Kind);
            Assert.Equal(selectedId, region.Source.InstanceId);
            Assert.Equal(selectedEndpoint, region.Source.Endpoint);
            Assert.Equal(analysis.Capture.FrameInfo, region.Source.CapturedFrameInfo);
        });

        Assert.Equal(1, fixture.ScreenshotHost.CaptureCount);
        Assert.Equal(selectedId, fixture.ScreenshotHost.LastInstanceId);
        Assert.Equal(selectedEndpoint, fixture.ScreenshotHost.LastEndpoint);
        using CapturedAndroidScreenshot captured = Assert.IsType<CapturedAndroidScreenshot>(
            fixture.ScreenshotHost.LastCapture);
        _ = Assert.Throws<ObjectDisposedException>(() => captured.Frame.GetInfo());
        Assert.DoesNotContain(
            typeof(AzurPilotOperatorScreenshotAnalysis).GetProperties(),
            static property => typeof(NativeFrame).IsAssignableFrom(property.PropertyType));

        Assert.Equal(3, fixture.Runner.Requests.Count);
        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(selectedId).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());
        Assert.Equal(
            AndroidTestContext.Installation.ControlExecutablePath,
            fixture.Runner.Requests[1].Request.ExecutablePath);
        RecordedProcessRequest screenshotRequest = fixture.Runner.Requests[2];
        Assert.Equal(AndroidInstallationLayout.GetAdbExecutablePath(AndroidTestContext.Installation.InstallRoot),
            screenshotRequest.Request.ExecutablePath);
        Assert.Equal(
            ["-s", selectedEndpoint.ToString(), "exec-out", "screencap", "-p"],
            screenshotRequest.Request.Arguments);
        Assert.Equal(WindowsProcessStandardOutputMode.Binary, screenshotRequest.Request.StandardOutputMode);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Неизвестная identity отклоняется до ADB и screenshot capture без mutation")]
    public async Task UnknownTargetDoesNotReachAndroidOrScreenshotHost()
    {
        MuMuInstanceId unknownId = MuMuInstanceId.FromIndex("99");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            unknownId,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, result.FailureInfo!.Code);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Equal(0, fixture.MuMuHost.MutationCount);
        Assert.Equal(0, fixture.MuMuHost.ObservationCount);
    }

    [Fact(DisplayName = "Ошибка endpoint exact identity прекращает screenshot до любых target mutations")]
    public async Task UnresolvableEndpointDoesNotCaptureOrMutateTarget()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(
            MuMuLifecycleTestContext.Instance("3"),
            MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, "not a MuMu instance response");

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            selectedId,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, result.FailureInfo!.Code);
        Assert.Equal(2, fixture.Runner.Requests.Count);
        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(selectedId).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Отмена сохраняет operation gate, пока асинхронный use case фактически не завершён")]
    public async Task CancellationRetainsOperationSlotUntilServiceTaskCompletes()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint selectedEndpoint = new("127.0.0.1", 20312);
        BlockingScreenshotHost screenshotHost = new();
        OperatorFixture fixture = CreateFixture(
            screenshotHost,
            MuMuLifecycleTestContext.Instance("7", "selected"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, selectedEndpoint));

        using OperatorUiController controller = new(fixture.Service, InitialDiagnostics());
        Assert.True(controller.Session.SelectInstance(selectedId));
        Assert.True(controller.TryStartOperation(OperatorUiAction.Screenshot));
        await screenshotHost.CaptureStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        controller.CancelOperation();
        await screenshotHost.CancellationRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.True(controller.Session.IsOperationActive);
        Assert.False(controller.TryStartOperation(OperatorUiAction.Instances));
        Assert.Equal(2, fixture.Runner.Requests.Count);

        screenshotHost.ReleaseCapture();
        await controller.WaitForOperationAsync();

        Assert.False(controller.CompleteOperation());
        Assert.False(controller.Session.IsOperationActive);
        Assert.Equal("Операция: отменена", controller.OperationText);
    }

    private static OperatorFixture CreateFixture(params MuMuInstance[] instances)
        => CreateFixture(screenshotHost: null, instances);

    private static OperatorFixture CreateFixture(
        IAndroidScreenshotHost? screenshotHost,
        params MuMuInstance[] instances)
    {
        MuMuInstallation installation = AndroidTestContext.Installation;
        TestMuMuHost muMuHost = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(installation),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(instances),
        };
        MuMuTestTimeProvider timeProvider = new();
        MuMuLifecycleService muMuLifecycle = new(
            muMuHost,
            new MuMuInstanceMutationGate(),
            timeProvider,
            MuMuLifecycleTimings.Default,
            NullLogger<MuMuLifecycleService>.Instance);
        FakeWindowsProcessRunner runner = new();
        AndroidWindowsHost androidHost = new(runner);
        AndroidReadinessService readiness = new(
            androidHost,
            AndroidLifecycleTimings.Default,
            timeProvider,
            NullLogger<AndroidReadinessService>.Instance);
        AzurLaneGameStateService gameState = new(
            androidHost,
            timeProvider,
            NullLogger<AzurLaneGameStateService>.Instance);
        AzurLaneGameLifecycleService gameLifecycle = new(
            androidHost,
            gameState,
            new AndroidGameMutationGate(),
            timeProvider,
            AndroidLifecycleTimings.Default,
            NullLogger<AzurLaneGameLifecycleService>.Instance);
        ObservingScreenshotHost observingScreenshotHost = new(androidHost);
        IAndroidScreenshotHost activeScreenshotHost = screenshotHost ?? observingScreenshotHost;
        string missingConfigurationPath = Path.Combine(
            Path.GetTempPath(),
            "azurpilot-operator-tests",
            $"missing-{Guid.NewGuid():N}.json");
        AzurPilotConfigurationSnapshot configuration = AzurPilotConfigurationLoader.Load(
            missingConfigurationPath).Value!;
        AzurPilotDiagnosticService diagnostics = new(
            configuration,
            NativeBoundaryFailureMapper.Map,
            NativeBoundaryFailureMapper.MapIncompatibility,
            muMuHost,
            muMuLifecycle,
            androidHost,
            readiness,
            gameState);
        AzurPilotOperatorService service = new(
            diagnostics,
            muMuHost,
            muMuLifecycle,
            androidHost,
            readiness,
            gameState,
            gameLifecycle,
            activeScreenshotHost);

        return new OperatorFixture(muMuHost, runner, observingScreenshotHost, service);
    }

    private static AzurPilotDiagnosticReport InitialDiagnostics()
        => new(
            new AzurPilotApplicationDiagnostics(
                "AzurPilot.App", "1.0.0", "test", "net10.0", "win-x64", "Windows", "X64", "X64", 1, true),
            new AzurPilotConfigurationDiagnostics(
                AzurPilotConfigurationSource.BuiltInDefaults, 2, 2, "valid", LogLevel.Information, null),
            new AzurPilotNativeDiagnostics(true, true, 3, new Version(5, 0), [], true, null, "compatible", null),
            new AzurPilotMuMuDiagnostics(
                true, "test", "supported", "mumu.instance", "mumu:7", "selected", MuMuLifecycleState.Running,
                "state=running", null),
            new AzurPilotAndroidDiagnostics(
                true, "adb", "127.0.0.1:20312", AndroidTransportState.Device, true, 1, "15", 35,
                "complete", "ready", null),
            new AzurPilotAzurLaneDiagnostics(
                "Azur Lane Global/EN", "com.YoStarEN.AzurLane", true, true, true,
                AzurLaneGameState.Foreground, "foreground", null));

    private static string InstanceResponse(MuMuInstanceId instanceId, AndroidEndpoint endpoint)
        => "{"
            + $"\"index\":\"{instanceId.Index}\","
            + "\"is_process_started\":true,"
            + "\"is_android_started\":true,"
            + $"\"adb_host_ip\":\"{endpoint.Host}\","
            + $"\"adb_port\":{endpoint.Port}"
            + "}";

    private sealed record OperatorFixture(
        TestMuMuHost MuMuHost,
        FakeWindowsProcessRunner Runner,
        ObservingScreenshotHost ScreenshotHost,
        AzurPilotOperatorService Service);

    private sealed class BlockingScreenshotHost : IAndroidScreenshotHost
    {
        private readonly TaskCompletionSource _captureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCapture() => _captureRelease.TrySetResult();

        public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), CancellationRequested);
            _ = CaptureStarted.TrySetResult();
            await _captureRelease.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Отменённый screenshot не должен возвращать capture.");
        }
    }

    private sealed class ObservingScreenshotHost(AndroidWindowsHost inner) : IAndroidScreenshotHost
    {
        public int CaptureCount { get; private set; }

        public MuMuInstanceId? LastInstanceId { get; private set; }

        public AndroidEndpoint? LastEndpoint { get; private set; }

        public CapturedAndroidScreenshot? LastCapture { get; private set; }

        public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            LastInstanceId = instanceId;
            LastEndpoint = endpoint;
            ApplicationResult<CapturedAndroidScreenshot> result = await inner.CaptureScreenshotAsync(
                instanceId,
                endpoint,
                cancellationToken);
            LastCapture = result.IsSuccess ? result.Value : null;
            return result;
        }
    }
}
