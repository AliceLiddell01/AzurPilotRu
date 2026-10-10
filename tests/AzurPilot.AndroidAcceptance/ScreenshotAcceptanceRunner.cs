using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;

namespace AzurPilot.AndroidAcceptance;

internal sealed record ScreenshotCaptureScanEvidence(
    int Sequence,
    AndroidScreenshotCaptureInfo Capture,
    ScreenshotRegionScanResult Scan);

internal sealed record ScreenshotLiveAcceptanceReport(
    string Build,
    string InstanceId,
    string? Endpoint,
    string? MuMuVersion,
    string? InitialMuMuState,
    string? FinalMuMuState,
    string? InitialAndroidTransport,
    int? InitialBootCompleted,
    string? InitialGameState,
    string? FinalAndroidTransport,
    int? FinalBootCompleted,
    string? FinalGameState,
    int RequestedCaptureCount,
    int SuccessfulCaptureCount,
    IReadOnlyList<ScreenshotCaptureScanEvidence> Captures,
    bool DeviceStatePreserved,
    bool AuditPassed,
    string? AuditFailure,
    string AuditSummary,
    string AuditForms,
    bool IsProven,
    string? FailureCode);

internal sealed class ScreenshotAcceptanceRunner
{
    private const string PreflightFailure = "screenshot_preflight_not_proven";
    private const string CaptureFailure = "screenshot_capture_or_scan_failed";
    private const string FinalStateFailure = "screenshot_final_state_changed_or_unknown";
    private const string AuditFailureCode = "screenshot_command_audit_failed";

    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly AndroidWindowsHost _androidHost;
    private readonly IAndroidScreenshotHost _screenshotHost;
    private readonly AndroidReadinessService _readiness;
    private readonly AzurLaneGameStateService _gameState;
    private readonly AndroidCommandAudit _audit;

    internal ScreenshotAcceptanceRunner(
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        AndroidWindowsHost androidHost,
        IAndroidScreenshotHost screenshotHost,
        AndroidReadinessService readiness,
        AzurLaneGameStateService gameState,
        AndroidCommandAudit audit)
    {
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(screenshotHost);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(audit);

        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
        _androidHost = androidHost;
        _screenshotHost = screenshotHost;
        _readiness = readiness;
        _gameState = gameState;
        _audit = audit;
    }

    internal async Task<ScreenshotLiveAcceptanceReport> RunAsync(
        ScreenshotLiveOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? endpointText = null;
        string? muMuVersion = null;
        string? initialMuMuState = null;
        string? finalMuMuState = null;
        string? initialTransport = null;
        int? initialBootCompleted = null;
        string? initialGameState = null;
        string? finalTransport = null;
        int? finalBootCompleted = null;
        string? finalGameState = null;
        string? controlExecutablePath = null;
        string? adbExecutablePath = null;
        AndroidEndpoint? endpoint = null;
        MuMuInstanceState observedInitialMuMuState;
        AndroidReadinessFacts observedInitialReadiness;
        AzurLaneGameObservation observedInitialGame;
        List<ScreenshotCaptureScanEvidence> captures = [];
        string? failureCode = null;
        bool deviceStatePreserved = false;
        bool auditPassed = false;
        string? auditFailure;

        ApplicationResult<MuMuInstallation> installationResult = _muMuHost.DiscoverInstallation();
        if (installationResult.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        MuMuInstallation installation = installationResult.Value!;
        muMuVersion = installation.Version;
        controlExecutablePath = installation.ControlExecutablePath;

        MuMuInstanceResolution selection = _muMuLifecycle.Resolve(
            installation,
            MuMuInstanceSelection.Explicit(options.InstanceId));
        if (!selection.IsResolved || selection.Instance!.Id != options.InstanceId)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        ApplicationResult<MuMuInstanceState> muMuState =
            _muMuHost.ObserveInstanceState(installation, options.InstanceId);
        if (muMuState.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        observedInitialMuMuState = muMuState.Value!;
        initialMuMuState = observedInitialMuMuState.State.ToString();
        if (observedInitialMuMuState.State != MuMuLifecycleState.Running)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        ApplicationResult<AndroidAdbExecutable> adbResult = _androidHost.DiscoverAdbExecutable(installation);
        if (adbResult.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        adbExecutablePath = adbResult.Value!.Path;
        ApplicationResult<AndroidEndpoint> endpointResult =
            _androidHost.ResolveEndpoint(installation, options.InstanceId);
        if (endpointResult.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        endpoint = endpointResult.Value!;
        endpointText = endpoint.Value.ToString();

        ApplicationResult<AndroidReadinessFacts> readinessResult = _readiness.Observe(endpoint.Value);
        if (readinessResult.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        observedInitialReadiness = readinessResult.Value!;
        initialTransport = observedInitialReadiness.Transport.State.ToString();
        initialBootCompleted = observedInitialReadiness.Boot?.BootCompleted;
        if (!IsReady(observedInitialReadiness))
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        ApplicationResult<AzurLaneGameObservation> gameResult = _gameState.Observe(endpoint.Value);
        if (gameResult.IsFailure)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        observedInitialGame = gameResult.Value!;
        initialGameState = observedInitialGame.State.ToString();
        if (observedInitialGame.State != AzurLaneGameState.Foreground)
        {
            failureCode = PreflightFailure;
            goto Complete;
        }

        try
        {
            for (int index = 0; index < options.CaptureCount; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    failureCode = "screenshot_capture_cancelled";
                    break;
                }

                ApplicationResult<CapturedAndroidScreenshot> captured = await _screenshotHost
                    .CaptureScreenshotAsync(options.InstanceId, endpoint.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (captured.IsFailure)
                {
                    failureCode = CaptureFailure;
                    break;
                }

                using CapturedAndroidScreenshot screenshot = captured.Value!;
                ScreenshotAnalysisSource source = ScreenshotAnalysisSource.ForCapture(screenshot.Info);
                ApplicationResult<ScreenshotRegionScanResult> scan = ScreenshotRegionScanner.Scan(
                    screenshot.Frame,
                    source,
                    ScreenshotDiagnosticRegion.Center);
                if (scan.IsFailure)
                {
                    failureCode = CaptureFailure;
                    break;
                }

                captures.Add(new ScreenshotCaptureScanEvidence(index + 1, screenshot.Info, scan.Value!));
            }
        }
        catch (OperationCanceledException)
        {
            failureCode = "screenshot_capture_cancelled";
        }
        catch (Exception)
        {
            // Ошибка одного capture не должна обходить завершающее read-only наблюдение состояния.
            failureCode = CaptureFailure;
        }

        if (captures.Count != options.CaptureCount)
        {
            failureCode ??= CaptureFailure;
        }

        ApplicationResult<MuMuInstanceState> finalMuMuResult =
            _muMuHost.ObserveInstanceState(installation, options.InstanceId);
        if (finalMuMuResult.IsSuccess)
        {
            finalMuMuState = finalMuMuResult.Value!.State.ToString();
        }

        ApplicationResult<AndroidReadinessFacts> finalReadinessResult = _readiness.Observe(endpoint.Value);
        if (finalReadinessResult.IsSuccess)
        {
            finalTransport = finalReadinessResult.Value!.Transport.State.ToString();
            finalBootCompleted = finalReadinessResult.Value.Boot?.BootCompleted;
        }

        ApplicationResult<AzurLaneGameObservation> finalGameResult = _gameState.Observe(endpoint.Value);
        if (finalGameResult.IsSuccess)
        {
            finalGameState = finalGameResult.Value!.State.ToString();
        }

        deviceStatePreserved = finalMuMuResult.IsSuccess
            && finalMuMuResult.Value!.State == observedInitialMuMuState.State
            && finalReadinessResult.IsSuccess
            && IsReady(finalReadinessResult.Value!)
            && SameReadiness(observedInitialReadiness, finalReadinessResult.Value!)
            && finalGameResult.IsSuccess
            && finalGameResult.Value!.State == observedInitialGame.State;

        if (!deviceStatePreserved)
        {
            failureCode = FinalStateFailure;
        }

    Complete:
        if (controlExecutablePath is not null)
        {
            auditFailure = _audit.FindViolation(
                controlExecutablePath,
                adbExecutablePath,
                options.InstanceId,
                endpoint,
                AzurLaneGameStateService.Package);
            auditPassed = auditFailure is null;
        }
        else
        {
            auditFailure = "audit_preflight_incomplete";
        }

        if (!auditPassed)
        {
            failureCode = AuditFailureCode;
        }

        bool proven = failureCode is null
            && captures.Count == options.CaptureCount
            && deviceStatePreserved
            && auditPassed;

        return new ScreenshotLiveAcceptanceReport(
            AcceptanceBuildIdentity.For(typeof(ScreenshotAcceptanceRunner)).Describe(),
            options.InstanceValue,
            endpointText,
            muMuVersion,
            initialMuMuState,
            finalMuMuState,
            initialTransport,
            initialBootCompleted,
            initialGameState,
            finalTransport,
            finalBootCompleted,
            finalGameState,
            options.CaptureCount,
            captures.Count,
            captures,
            deviceStatePreserved,
            auditPassed,
            auditFailure,
            controlExecutablePath is null ? "недоступно: preflight не завершён" : _audit.Describe(adbExecutablePath),
            controlExecutablePath is null ? "недоступно: preflight не завершён" : _audit.DescribeForms(adbExecutablePath),
            proven,
            proven ? null : failureCode ?? PreflightFailure);
    }

    private static bool IsReady(AndroidReadinessFacts facts)
        => facts.Transport.State == AndroidTransportState.Device
            && facts.Boot is { ShellAvailable: true, BootCompleted: 1 };

    private static bool SameReadiness(AndroidReadinessFacts initial, AndroidReadinessFacts final)
        => initial.Endpoint == final.Endpoint
            && initial.Transport.State == final.Transport.State
            && initial.Boot?.ShellAvailable == final.Boot?.ShellAvailable
            && initial.Boot?.BootCompleted == final.Boot?.BootCompleted
            && string.Equals(initial.Boot?.AndroidRelease, final.Boot?.AndroidRelease, StringComparison.Ordinal)
            && initial.Boot?.SdkLevel == final.Boot?.SdkLevel;
}
