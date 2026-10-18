using System.Reflection;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>Production capture поверх общей fake process boundary и реального native decoder.</summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "Android")]
public sealed class AndroidScreenshotCaptureTests
{
    private static readonly MuMuInstanceId InstanceId = MuMuInstanceId.FromIndex("3");
    private static readonly AndroidEndpoint Endpoint = new("capture-host", 16418);

    [Fact(DisplayName = "Capture выполняет одну бинарную exact-target команду и передаёт ownership RGB8")]
    public async Task CaptureUsesOneExactTargetCommandAndOwnsNativeFrame()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        byte[] png = ReadFixture("rgb_red_blue.png");
        fixture.Runner.EnqueueBinaryOutcome(png, standardError: "bounded diagnostic text");
        using CancellationTokenSource cancellation = new();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Host.CaptureScreenshotAsync(
            InstanceId, Endpoint, cancellation.Token);

        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.True(result.IsSuccess);
        using CapturedAndroidScreenshot screenshot = result.Value!;
        Assert.Equal(InstanceId, screenshot.Info.InstanceId);
        Assert.Equal(Endpoint, screenshot.Info.Endpoint);
        Assert.Equal(TimeSpan.Zero, screenshot.Info.CapturedAtUtc.Offset);
        Assert.InRange(screenshot.Info.CapturedAtUtc, before, after);
        Assert.Equal(TimeSpan.FromMilliseconds(17), screenshot.Info.CommandDuration);
        Assert.Equal(png.Length, screenshot.Info.EncodedPngByteCount);
        Assert.Equal(0, screenshot.Info.ProcessExitCode);
        Assert.Equal(new NativeFrameInfo(2, 1, 6, 6, AzurPilotNativeBridge.PixelFormatRgb8), screenshot.Info.FrameInfo);
        Assert.Equal(screenshot.Info.FrameInfo, screenshot.Frame.GetInfo());

        Assert.Equal(2, fixture.Runner.Requests.Count);
        RecordedProcessRequest capture = fixture.Runner.Requests[1];
        Assert.Equal(
            AndroidInstallationLayout.GetAdbExecutablePath(fixture.Installation.InstallRoot),
            capture.Request.ExecutablePath);
        Assert.Equal(["-s", Endpoint.ToString(), "exec-out", "screencap", "-p"], capture.Request.Arguments);
        Assert.Equal(WindowsProcessStandardOutputMode.Binary, capture.Request.StandardOutputMode);
        Assert.Equal(
            checked((int)AzurPilotNativeBridge.GetMaximumPngBytes()),
            capture.Request.MaximumStandardOutputBytes);
        Assert.Equal(AdbClient.DefaultCommandTimeout, capture.Request.Timeout);
        Assert.Equal(cancellation.Token, capture.CancellationToken);
        Assert.Null(capture.Request.WorkingDirectory);
        AndroidTestOperations.AssertNoneRequested(fixture.Runner.Requests.Select(item => item.Request.Arguments));

        screenshot.Dispose();
        screenshot.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => screenshot.Frame.GetInfo());
    }

    [Fact(DisplayName = "Провенанс кадра использует внедрённый TimeProvider")]
    public async Task CaptureProvenanceUsesInjectedTimeProvider()
    {
        MuMuTestTimeProvider timeProvider = new();
        CaptureFixture fixture = new(timeProvider);
        fixture.Discover();
        fixture.Runner.EnqueueBinaryOutcome(ReadFixture("rgb_red_blue.png"));
        DateTimeOffset expectedStart = timeProvider.GetUtcNow();
        long expectedTimestamp = timeProvider.GetTimestamp();

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        Assert.True(result.IsSuccess, result.ToString());
        using CapturedAndroidScreenshot screenshot = result.Value!;
        Assert.Equal(expectedStart, screenshot.Info.CaptureStartedAtUtc);
        Assert.Equal(expectedTimestamp, screenshot.Info.CaptureStartedTimestamp);
        Assert.Equal(timeProvider.GetUtcNow(), screenshot.Info.CapturedAtUtc);
    }

    [Fact(DisplayName = "Каждый Android host требует общий TimeProvider")]
    public void AndroidHostConstructorsRequireTimeProvider()
    {
        ConstructorInfo[] constructors = typeof(AndroidWindowsHost).GetConstructors();
        Assert.NotEmpty(constructors);

        foreach (ConstructorInfo constructor in constructors)
        {
            ParameterInfo clock = Assert.Single(
                constructor.GetParameters(),
                static parameter => parameter.ParameterType == typeof(TimeProvider));
            Assert.False(clock.IsOptional);
        }
    }

    [Fact(DisplayName = "Неизвестный endpoint отвергается до любого процесса или native вызова")]
    public async Task DefaultEndpointFailsClosedWithoutProcess()
    {
        CaptureFixture fixture = new();

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Host.CaptureScreenshotAsync(
            InstanceId, default, CancellationToken.None);

        AssertCaptureFailure(result, "endpoint_invalid");
        Assert.Empty(fixture.Runner.Requests);
        Assert.DoesNotContain("endpoint", result.FailureInfo!.Details!.Keys);
    }

    [Fact(DisplayName = "Неизвестная identity не становится provenance успешного screenshot")]
    public async Task DefaultInstanceFailsClosedWithoutProcess()
    {
        CaptureFixture fixture = new();

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Host.CaptureScreenshotAsync(
            default, Endpoint, CancellationToken.None);

        AssertCaptureFailure(result, "instance_invalid");
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Capture не выполняет скрытый discovery при отсутствии подтверждённого bundled ADB")]
    public async Task CaptureBeforeAdbDiscoveryIsProgrammingError()
    {
        CaptureFixture fixture = new();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.CaptureScreenshotAsync(
            InstanceId, Endpoint, CancellationToken.None));

        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Ненулевой exit code не становится screenshot-ready даже при корректном PNG")]
    public async Task NonzeroExitCodeRejectsValidPng()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        fixture.Runner.EnqueueBinaryOutcome(
            ReadFixture("rgb_red_blue.png"), exitCode: 7, standardError: "private error payload");

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "command_failed");
        Assert.Equal("7", result.FailureInfo!.Details!["exit_code"]);
        AssertSanitized(result, fixture.Installation.InstallRoot, "private error payload");
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Усечённый PNG не декодируется даже если сохранённый prefix является PNG")]
    public async Task TruncatedBinaryOutputIsRejected()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        byte[] png = ReadFixture("rgb_red_blue.png");
        fixture.Runner.EnqueueBinaryOutcome(png, bytesRead: png.Length + 1, truncated: true);

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "png_truncated_or_oversized");
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Theory(DisplayName = "Incomplete binary output не выдаётся за целый screenshot")]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task IncompleteBinaryOutputIsRejected(bool streamEnded, bool mismatchByteCount)
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        byte[] png = ReadFixture("rgb_red_blue.png");
        fixture.Runner.EnqueueBinaryOutcome(
            png,
            bytesRead: mismatchByteCount ? png.Length + 1 : png.Length,
            streamEnded: streamEnded);

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "binary_output_incomplete");
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Текстовый исход runner не подменяет бинарный PNG")]
    public async Task TextOutcomeCannotBecomeScreenshot()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, "unusable text");

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "binary_output_incomplete");
        AssertSanitized(result, fixture.Installation.InstallRoot, "unusable text");
    }

    [Theory(DisplayName = "Exit 0 не скрывает native отказ malformed, unsupported или oversized PNG")]
    [InlineData("corrupt_idat_checksum.png", AzurPilotNativeBridge.StatusInvalidPng)]
    [InlineData("rgba_nonopaque.png", AzurPilotNativeBridge.StatusUnsupportedPng)]
    [InlineData("oversized_dimensions.png", AzurPilotNativeBridge.StatusImageTooLarge)]
    public async Task NativeDecoderFailureIsStructured(string fixtureName, int expectedStatus)
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        fixture.Runner.EnqueueBinaryOutcome(ReadFixture(fixtureName), standardError: "private native capture text");

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "native_decode_failed");
        Assert.Equal(
            expectedStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.FailureInfo!.Details!["native_status_code"]);
        AssertSanitized(result, fixture.Installation.InstallRoot, "private native capture text");
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Произвольные бинарные байты при exit 0 отклоняет существующий PNG decoder")]
    public async Task InvalidBinaryBytesDoNotBecomeFrame()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        fixture.Runner.EnqueueBinaryOutcome([0, 10, 13, 255]);

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        AssertCaptureFailure(result, "native_decode_failed");
        Assert.Equal("4", result.FailureInfo!.Details!["encoded_png_bytes"]);
    }

    [Fact(DisplayName = "Capture сохраняет исходные отказы process boundary, включая отмену")]
    public async Task ProcessProjectionFailurePassesThroughUnchanged()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        ApplicationFailure cancelled = new AdbProcessFailureProjection().Cancelled();
        fixture.Runner.EnqueueFailure(cancelled);

        ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();

        Assert.True(result.IsFailure);
        Assert.Same(cancelled, result.FailureInfo);
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Повторные capture/dispose используют тот же target без lifecycle команд")]
    public async Task RepeatedCaptureAndDisposeHaveNoLifecycleCommands()
    {
        CaptureFixture fixture = new();
        fixture.Discover();
        byte[] png = ReadFixture("rgb_red_blue.png");
        for (int index = 0; index < 5; index++)
        {
            fixture.Runner.EnqueueBinaryOutcome(png, standardErrorTruncated: true);
            ApplicationResult<CapturedAndroidScreenshot> result = await fixture.Capture();
            Assert.True(result.IsSuccess);
            using CapturedAndroidScreenshot screenshot = result.Value!;
            Assert.Equal(2u, screenshot.Frame.GetInfo().Width);
        }

        Assert.Equal(6, fixture.Runner.Requests.Count);
        Assert.All(fixture.Runner.Requests.Skip(1), item => Assert.Equal(
            ["-s", Endpoint.ToString(), "exec-out", "screencap", "-p"], item.Request.Arguments));
    }

    private static void AssertCaptureFailure(ApplicationResult<CapturedAndroidScreenshot> result, string reason)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidScreenshotCaptureFailed, result.FailureInfo!.Code);
        Assert.Equal(reason, result.FailureInfo.Details!["reason"]);
    }

    private static void AssertSanitized(
        ApplicationResult<CapturedAndroidScreenshot> result,
        string installRoot,
        string forbiddenText)
    {
        ApplicationFailure failure = result.FailureInfo!;
        Assert.DoesNotContain(installRoot, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(forbiddenText, failure.Message, StringComparison.Ordinal);
        Assert.All(failure.Details!, detail =>
        {
            Assert.DoesNotContain(installRoot, detail.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbiddenText, detail.Value, StringComparison.Ordinal);
        });
    }

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(PinnedVersions.RepositoryRoot, "native", "tests", "fixtures", name));

    private sealed class CaptureFixture
    {
        internal CaptureFixture(TimeProvider? timeProvider = null)
        {
            string installRoot = AndroidTestPaths.Create("screenshot-install");
            Installation = new MuMuInstallation(
                "test-version", installRoot, Path.Combine(installRoot, "control.exe"));
            Runner = new FakeWindowsProcessRunner();
            Host = new AndroidWindowsHost(Runner, timeProvider ?? TimeProvider.System);
        }

        internal MuMuInstallation Installation { get; }

        internal FakeWindowsProcessRunner Runner { get; }

        internal AndroidWindowsHost Host { get; }

        internal void Discover()
        {
            Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
            Assert.True(Host.DiscoverAdbExecutable(Installation).IsSuccess);
        }

        internal Task<ApplicationResult<CapturedAndroidScreenshot>> Capture()
            => Host.CaptureScreenshotAsync(InstanceId, Endpoint, CancellationToken.None);
    }
}
