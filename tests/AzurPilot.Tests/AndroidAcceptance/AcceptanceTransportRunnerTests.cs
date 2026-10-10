using AzurPilot.AndroidAcceptance;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.Android;
using Xunit;

namespace AzurPilot.Tests.AndroidAcceptance;

/// <summary>Проверки композиции явной подготовки и read-only screenshot preflight.</summary>
public sealed class AcceptanceTransportRunnerTests
{
    private static readonly MuMuInstanceId Instance = MuMuInstanceId.FromIndex("1");
    private static readonly AndroidEndpoint Endpoint = AndroidTestContext.Endpoint;

    [Fact(DisplayName = "Неожиданный отказ screenshot host сохраняет безопасную причину и final observations")]
    public async Task UnexpectedCaptureExceptionPreservesSafeReasonAndFinalState()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateReadyContexts();
        const string privateMessage = "личный путь к снимку и закрытые диагностические данные";
        ThrowingCaptureHost capture = new(new InvalidOperationException(privateMessage));
        ScreenshotAcceptanceRunner runner = new(
            muMu.Host, muMu.Service, android.Host, capture, android.Readiness, android.GameState,
            new AndroidCommandAudit(AndroidCommandAuditMode.ScreenshotReadOnly));

        ScreenshotLiveAcceptanceReport report = await runner.RunAsync(
            new ScreenshotLiveOptions(Instance, 3, null), CancellationToken.None);

        Assert.False(report.IsProven);
        Assert.Equal("screenshot_capture_or_scan_failed", report.FailureCode);
        Assert.Equal("screenshot_capture_unexpected_failure", report.CaptureFailureCode);
        Assert.Equal("exception_type:InvalidOperationException", report.CaptureFailureReason);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(0, report.SuccessfulCaptureCount);
        Assert.True(report.DeviceStatePreserved);
        Assert.True(report.AuditPassed);
        Assert.Equal("Running", report.FinalMuMuState);
        Assert.Equal("Device", report.FinalAndroidTransport);
        Assert.Equal("Foreground", report.FinalGameState);
        Assert.Equal(2, muMu.Host.ObservationCount);
        Assert.Equal(2, android.Host.TransportRequests.Count);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.DoesNotContain(privateMessage, ScreenshotAcceptanceJson.Serialize(report), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Отмена screenshot host сохраняет семантику отмены и final observations")]
    public async Task CaptureCancellationRetainsCancellationAndFinalState()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateReadyContexts();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ThrowingCaptureHost capture = new(new OperationCanceledException(cancellation.Token));
        ScreenshotAcceptanceRunner runner = new(
            muMu.Host, muMu.Service, android.Host, capture, android.Readiness, android.GameState,
            new AndroidCommandAudit(AndroidCommandAuditMode.ScreenshotReadOnly));

        // Отмену сообщает host после начала операции, чтобы проверить catch и завершающее наблюдение.
        ScreenshotLiveAcceptanceReport report = await runner.RunAsync(
            new ScreenshotLiveOptions(Instance, 3, null), CancellationToken.None);

        Assert.False(report.IsProven);
        Assert.Equal("screenshot_capture_cancelled", report.FailureCode);
        Assert.Null(report.CaptureFailureCode);
        Assert.Null(report.CaptureFailureReason);
        Assert.True(report.DeviceStatePreserved);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(2, muMu.Host.ObservationCount);
        Assert.Equal(2, android.Host.TransportRequests.Count);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
    }

    [Fact(DisplayName = "Screenshot preflight при Absent блокирует capture и предлагает отдельную подготовку")]
    public async Task ScreenshotAbsentIsBlockedWithoutMutation()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(AndroidTransportState.Absent));
        NoCaptureHost capture = new();
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.ScreenshotReadOnly);
        ScreenshotAcceptanceRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            capture,
            android.Readiness,
            android.GameState,
            audit);

        ScreenshotLiveAcceptanceReport report = await runner.RunAsync(
            new ScreenshotLiveOptions(Instance, 3, null), CancellationToken.None);

        Assert.Equal("BLOCKED", report.Status);
        Assert.False(report.IsProven);
        Assert.Equal("Absent", report.InitialAndroidTransport);
        Assert.Equal("android_transport_absent", report.PreflightBlocker);
        Assert.Equal(
            "dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- "
                + "--prepare-transport --instance mumu:1",
            report.NextStep);
        Assert.Null(report.DeviceStatePreserved);
        Assert.True(report.AuditPassed);
        Assert.Equal(0, report.SuccessfulCaptureCount);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Empty(android.Host.PackageRequests);
        Assert.Empty(android.Host.ProcessRequests);
        Assert.Empty(android.Host.ForegroundRequests);
        Assert.Equal(0, capture.CallCount);
    }

    [Fact(DisplayName = "Отказ capture сохраняет bounded причину при изменении финального состояния")]
    public async Task CaptureFailureDetailsSurviveFinalStateChange()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        int transportObservations = 0;
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            ++transportObservations == 1
                ? AndroidTestContext.Device()
                : AndroidTestContext.Transport(AndroidTransportState.Absent));
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 1));
        android.Host.PackageHandler = (_, _) =>
            ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.Installed);
        android.Host.ProcessesHandler = (_, _) =>
            ApplicationResult<AndroidProcessObservation>.Success(AndroidTestContext.RunningProcesses(123));
        android.Host.ForegroundHandler = _ =>
            ApplicationResult<AndroidForegroundObservation>.Success(AndroidTestContext.GameForeground());
        FailureCaptureHost capture = new(new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
            Message = "capture-failed",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "native_decode_failed",
            },
        });
        ScreenshotAcceptanceRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            capture,
            android.Readiness,
            android.GameState,
            new AndroidCommandAudit(AndroidCommandAuditMode.ScreenshotReadOnly));

        ScreenshotLiveAcceptanceReport report = await runner.RunAsync(
            new ScreenshotLiveOptions(Instance, 3, null), CancellationToken.None);

        Assert.Equal("BLOCKED", report.Status);
        Assert.False(report.DeviceStatePreserved);
        Assert.Equal("screenshot_capture_or_scan_failed", report.FailureCode);
        Assert.Equal(ApplicationFailure.AndroidScreenshotCaptureFailed, report.CaptureFailureCode);
        Assert.Equal("native_decode_failed", report.CaptureFailureReason);
        Assert.Equal(1, capture.CallCount);

        string json = ScreenshotAcceptanceJson.Serialize(report);
        Assert.Contains("\"captureFailureCode\": \"android_screenshot_capture_failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"captureFailureReason\": \"native_decode_failed\"", json, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "BootEvidence не принимает evidence отказа, не связанного с AndroidNotReady")]
    public async Task PreparationDoesNotExposeUnrelatedFailureEvidenceAsBootEvidence()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Device());
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Failure(
            new ApplicationFailure
            {
                Code = ApplicationFailure.NativeUnavailable,
                Message = "native-unavailable",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["evidence"] = "native-library-diagnostic",
                },
            });
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.Equal(ApplicationFailure.NativeUnavailable, report.FailureCode);
        Assert.Null(report.BootEvidence);
    }

    [Fact(DisplayName = "BootEvidence сохраняется для отказа AndroidNotReady")]
    public async Task PreparationPreservesBootEvidenceForAndroidNotReady()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Device());
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 0));
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.Equal(ApplicationFailure.AndroidNotReady, report.FailureCode);
        Assert.NotNull(report.BootEvidence);
        Assert.NotEmpty(report.BootEvidence);
    }

    [Theory(DisplayName = "Screenshot preflight не исправляет Offline и Unknown")]
    [InlineData(AndroidTransportState.Offline, "android_transport_offline")]
    [InlineData(AndroidTransportState.Unknown, "android_transport_unknown")]
    public async Task ScreenshotUnusableTransportRemainsReadOnly(
        AndroidTransportState state,
        string expectedBlocker)
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(state));
        NoCaptureHost capture = new();
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.ScreenshotReadOnly);
        ScreenshotAcceptanceRunner runner = new(
            muMu.Host, muMu.Service, android.Host, capture, android.Readiness, android.GameState, audit);

        ScreenshotLiveAcceptanceReport report = await runner.RunAsync(
            new ScreenshotLiveOptions(Instance, 3, null), CancellationToken.None);

        Assert.Equal("BLOCKED", report.Status);
        Assert.False(report.IsProven);
        Assert.Equal(expectedBlocker, report.PreflightBlocker);
        Assert.Null(report.NextStep);
        Assert.Null(report.DeviceStatePreserved);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
        Assert.Equal(0, capture.CallCount);
    }

    [Fact(DisplayName = "Подготовка при Absent проверяет exact instance и завершает readiness")]
    public async Task PreparationUsesExactInstanceAndOneConnect()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        int transportPolls = 0;
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            ++transportPolls == 1
                ? AndroidTestContext.Transport(AndroidTransportState.Absent)
                : AndroidTestContext.Device());
        android.Host.ConnectHandler = (endpoint, _) =>
            AndroidTestContext.Command(exitCode: 1);
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 1));
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.TransportPreparation);
        AndroidTransportPreparationRunner runner = new(
            muMu.Host, muMu.Service, android.Host, android.Readiness, audit);

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.Equal("PROVEN", report.Status);
        Assert.True(report.IsProven);
        Assert.Equal("mumu:1", report.InstanceId);
        Assert.Equal(Endpoint.ToString(), report.Endpoint);
        Assert.Equal("absent", report.InitialTransport);
        Assert.Equal("device", report.FinalTransport);
        Assert.True(report.ShellAvailable);
        Assert.Equal(1, report.BootCompleted);
        Assert.True(report.ConnectAttempted);
        Assert.True(report.AuditPassed);
        Assert.Equal([Instance], android.Host.EndpointRequests);
        Assert.Equal([Endpoint], android.Host.ConnectRequests);
        Assert.Empty(android.Host.MutationRequests);
    }

    [Fact(DisplayName = "Preparation при отмене не принимает состояние игры за final transport")]
    public async Task PreparationCancellationDoesNotReportGameStateAsFinalTransport()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        using CancellationTokenSource cancellation = new();
        android.Host.TransportHandler = _ =>
        {
            cancellation.Cancel();
            return ApplicationResult<AndroidTransportObservation>.Success(
                AndroidTestContext.Transport(AndroidTransportState.Absent));
        };
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), cancellation.Token);

        Assert.Equal("absent", report.InitialTransport);
        Assert.Null(report.FinalTransport);
        Assert.False(report.ConnectAttempted);
        Assert.Equal(ApplicationFailure.OperationCancelled, report.FailureCode);
    }

    [Theory(DisplayName = "Preparation отказывает для Offline и Unknown без connect")]
    [InlineData(AndroidTransportState.Offline, "android_transport_offline")]
    [InlineData(AndroidTransportState.Unknown, "android_transport_unknown")]
    public async Task PreparationDoesNotMutateUnusableTransport(
        AndroidTransportState state,
        string expectedBlocker)
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Transport(state));
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.TransportPreparation);
        AndroidTransportPreparationRunner runner = new(
            muMu.Host, muMu.Service, android.Host, android.Readiness, audit);

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.Equal("BLOCKED", report.Status);
        Assert.False(report.IsProven);
        Assert.Equal(expectedBlocker, report.PreflightBlocker);
        Assert.Equal(state.ToString().ToLowerInvariant(), report.InitialTransport);
        Assert.False(report.ConnectAttempted);
        Assert.True(report.AuditPassed);
        Assert.Empty(android.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Preparation не ищет endpoint при чужой instance identity")]
    public async Task PreparationRejectsDifferentInstanceBeforeAdb()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        muMu.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
            [MuMuLifecycleTestContext.Instance("2")]);
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.False(report.IsProven);
        Assert.Equal("mumu_instance_identity_not_proven", report.PreflightBlocker);
        Assert.Empty(android.Host.AdbDiscoveryRequests);
        Assert.Empty(android.Host.EndpointRequests);
        Assert.Empty(android.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Preparation не подключается к остановленному MuMu instance")]
    public async Task PreparationRejectsStoppedInstanceBeforeAdb()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        muMu.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.False(report.IsProven);
        Assert.Equal("mumu_instance_not_running", report.PreflightBlocker);
        Assert.Empty(android.Host.AdbDiscoveryRequests);
        Assert.Empty(android.Host.EndpointRequests);
        Assert.Empty(android.Host.ConnectRequests);
    }

    [Fact(DisplayName = "Preparation останавливается при неразрешённом exact endpoint")]
    public async Task PreparationRejectsUnresolvedEndpointBeforeTransportMutation()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.EndpointHandler = (_, _) => ApplicationResult<AndroidEndpoint>.Failure(
            new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidEndpointUnavailable,
                Message = "test-endpoint-unavailable",
            });
        AndroidTransportPreparationRunner runner = new(
            muMu.Host,
            muMu.Service,
            android.Host,
            android.Readiness,
            new AndroidCommandAudit(AndroidCommandAuditMode.TransportPreparation));

        AndroidTransportPreparationReport report = await runner.RunAsync(
            new AndroidTransportPreparationOptions(Instance), CancellationToken.None);

        Assert.False(report.IsProven);
        Assert.Equal("exact_adb_endpoint_unresolved", report.PreflightBlocker);
        Assert.Null(report.Endpoint);
        Assert.Empty(android.Host.TransportRequests);
        Assert.Empty(android.Host.ConnectRequests);
        Assert.Empty(android.Host.BootRequests);
    }

    private static (MuMuLifecycleTestContext MuMu, AndroidTestContext Android) CreateReadyContexts()
    {
        (MuMuLifecycleTestContext muMu, AndroidTestContext android) = CreateContexts();
        android.Host.TransportHandler = _ => ApplicationResult<AndroidTransportObservation>.Success(
            AndroidTestContext.Device());
        android.Host.BootHandler = _ => ApplicationResult<AndroidBootObservation>.Success(
            AndroidTestContext.Boot(shellAvailable: true, bootCompleted: 1));
        android.Host.PackageHandler = (_, _) =>
            ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.Installed);
        android.Host.ProcessesHandler = (_, _) =>
            ApplicationResult<AndroidProcessObservation>.Success(AndroidTestContext.RunningProcesses(123));
        android.Host.ForegroundHandler = _ =>
            ApplicationResult<AndroidForegroundObservation>.Success(AndroidTestContext.GameForeground());
        return (muMu, android);
    }

    private static (MuMuLifecycleTestContext MuMu, AndroidTestContext Android) CreateContexts()
    {
        MuMuLifecycleTestContext muMu = new();
        muMu.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
            [MuMuLifecycleTestContext.Instance("1")]);
        muMu.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);

        AndroidTestContext android = new();
        android.Host.AdbExecutableHandler = _ => ApplicationResult<AndroidAdbExecutable>.Success(
            new AndroidAdbExecutable(
                AndroidTestPaths.Create("install", "adb.exe"),
                "test-adb-version"));
        android.Host.EndpointHandler = (_, instance) => instance == Instance
            ? ApplicationResult<AndroidEndpoint>.Success(Endpoint)
            : ApplicationResult<AndroidEndpoint>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidEndpointUnavailable,
                Message = "test-endpoint-unavailable",
            });
        return (muMu, android);
    }

    private sealed class ThrowingCaptureHost(Exception exception) : IAndroidScreenshotHost
    {
        internal int CallCount { get; private set; }

        public Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<ApplicationResult<CapturedAndroidScreenshot>>(exception);
        }
    }

    private sealed class NoCaptureHost : IAndroidScreenshotHost
    {
        internal int CallCount { get; private set; }

        public Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ApplicationResult<CapturedAndroidScreenshot>.Failure(
                new ApplicationFailure
                {
                    Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
                    Message = "capture-should-not-be-requested",
                }));
        }
    }

    private sealed class FailureCaptureHost(ApplicationFailure failure) : IAndroidScreenshotHost
    {
        internal int CallCount { get; private set; }

        public Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ApplicationResult<CapturedAndroidScreenshot>.Failure(failure));
        }
    }
}
