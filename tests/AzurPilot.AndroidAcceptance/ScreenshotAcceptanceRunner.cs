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
    bool? DeviceStatePreserved,
    bool? AuditPassed,
    string? AuditFailure,
    string AuditSummary,
    string AuditForms,
    string? PreflightBlocker,
    string? NextStep,
    bool IsProven,
    string? FailureCode,
    string? CaptureFailureCode,
    string? CaptureFailureReason)
{
    public string Status => IsProven ? "PROVEN" : "BLOCKED";
}

internal sealed class ScreenshotAcceptanceRunner
{
    private const string PreflightFailure = "screenshot_preflight_not_proven";
    private const string CaptureFailure = "screenshot_capture_or_scan_failed";
    private const string FinalStateFailure = "screenshot_final_state_changed";
    private const string AuditFailureCode = "screenshot_command_audit_failed";

    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly IAndroidHost _androidHost;
    private readonly IAndroidScreenshotHost _screenshotHost;
    private readonly AndroidReadinessService _readiness;
    private readonly AzurLaneGameStateService _gameState;
    private readonly AndroidCommandAudit _audit;

    internal ScreenshotAcceptanceRunner(
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
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
        string? captureFailureCode = null;
        string? captureFailureReason = null;
        bool? deviceStatePreserved = null;
        bool? auditPassed = null;
        string? auditFailure;
        string? preflightBlocker = null;
        string? nextStep = null;

        ApplicationResult<MuMuInstallation> installationResult = _muMuHost.DiscoverInstallation();
        if (installationResult.IsFailure)
        {
            failureCode = installationResult.FailureInfo!.Code;
            preflightBlocker = "mumu_installation_not_available";
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
            failureCode = selection.Failure?.Code ?? "mumu_instance_identity_not_proven";
            preflightBlocker = "mumu_instance_identity_not_proven";
            goto Complete;
        }

        ApplicationResult<MuMuInstanceState> muMuState =
            _muMuHost.ObserveInstanceState(installation, options.InstanceId);
        if (muMuState.IsFailure)
        {
            failureCode = muMuState.FailureInfo!.Code;
            preflightBlocker = "mumu_state_not_observed";
            goto Complete;
        }

        observedInitialMuMuState = muMuState.Value!;
        initialMuMuState = observedInitialMuMuState.State.ToString();
        if (observedInitialMuMuState.State != MuMuLifecycleState.Running)
        {
            failureCode = "mumu_instance_not_running";
            preflightBlocker = "mumu_instance_not_running";
            goto Complete;
        }

        ApplicationResult<AndroidAdbExecutable> adbResult = _androidHost.DiscoverAdbExecutable(installation);
        if (adbResult.IsFailure)
        {
            failureCode = adbResult.FailureInfo!.Code;
            preflightBlocker = "bundled_adb_unavailable";
            goto Complete;
        }

        adbExecutablePath = adbResult.Value!.Path;
        ApplicationResult<AndroidEndpoint> endpointResult =
            _androidHost.ResolveEndpoint(installation, options.InstanceId);
        if (endpointResult.IsFailure)
        {
            failureCode = endpointResult.FailureInfo!.Code;
            preflightBlocker = "adb_endpoint_not_resolved";
            goto Complete;
        }

        endpoint = endpointResult.Value!;
        endpointText = endpoint.Value.ToString();

        ApplicationResult<AndroidReadinessFacts> readinessResult = _readiness.Observe(endpoint.Value);
        if (readinessResult.IsFailure)
        {
            failureCode = readinessResult.FailureInfo!.Code;
            preflightBlocker = "android_readiness_not_observed";
            goto Complete;
        }

        observedInitialReadiness = readinessResult.Value!;
        initialTransport = observedInitialReadiness.Transport.State.ToString();
        initialBootCompleted = observedInitialReadiness.Boot?.BootCompleted;
        if (!IsReady(observedInitialReadiness))
        {
            string transport = observedInitialReadiness.Transport.State.ToString().ToLowerInvariant();
            preflightBlocker = observedInitialReadiness.Transport.State switch
            {
                AndroidTransportState.Absent => "android_transport_absent",
                AndroidTransportState.Offline => "android_transport_offline",
                AndroidTransportState.Unknown => "android_transport_unknown",
                AndroidTransportState.Device => "android_boot_not_proven",
                _ => "android_transport_" + transport,
            };
            failureCode = preflightBlocker;
            if (observedInitialReadiness.Transport.State == AndroidTransportState.Absent)
            {
                nextStep = "dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- "
                    + AndroidTransportPreparationArgumentParser.Mode
                    + " --instance " + options.InstanceValue;
            }

            goto Complete;
        }

        ApplicationResult<AzurLaneGameObservation> gameResult = _gameState.Observe(endpoint.Value);
        if (gameResult.IsFailure)
        {
            failureCode = gameResult.FailureInfo!.Code;
            preflightBlocker = "game_state_not_observed";
            goto Complete;
        }

        observedInitialGame = gameResult.Value!;
        initialGameState = observedInitialGame.State.ToString();
        if (observedInitialGame.State != AzurLaneGameState.Foreground)
        {
            failureCode = "game_state_" + observedInitialGame.State.ToString().ToLowerInvariant();
            preflightBlocker = failureCode;
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
                    captureFailureCode = captured.FailureInfo!.Code;
                    captureFailureReason = GetFailureReason(captured.FailureInfo);
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
                    captureFailureCode = scan.FailureInfo!.Code;
                    captureFailureReason = GetFailureReason(scan.FailureInfo);
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
        catch (Exception exception)
        {
            // Ошибка одного capture не должна обходить завершающее read-only наблюдение состояния.
            failureCode = CaptureFailure;
            captureFailureCode = "screenshot_capture_unexpected_failure";
            captureFailureReason = "exception_type:" + GetSafeExceptionType(exception);
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

        bool? muMuPreserved = finalMuMuResult.IsSuccess
            ? SameMuMuState(observedInitialMuMuState, finalMuMuResult.Value!)
            : null;
        bool? readinessPreserved = finalReadinessResult.IsSuccess
            ? SameReadiness(observedInitialReadiness, finalReadinessResult.Value!)
            : null;
        bool? gamePreserved = finalGameResult.IsSuccess
            ? SameGameState(observedInitialGame, finalGameResult.Value!)
            : null;
        deviceStatePreserved = CombinePreservation(muMuPreserved, readinessPreserved, gamePreserved);

        if (deviceStatePreserved == false)
        {
            failureCode ??= FinalStateFailure;
        }
        else if (deviceStatePreserved is null)
        {
            failureCode ??= "screenshot_final_state_not_observed";
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

        if (auditPassed == false)
        {
            failureCode = AuditFailureCode;
        }

        bool proven = failureCode is null
            && captures.Count == options.CaptureCount
            && deviceStatePreserved == true
            && auditPassed == true;

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
            preflightBlocker,
            nextStep,
            proven,
            proven ? null : failureCode ?? PreflightFailure,
            captureFailureCode,
            captureFailureReason);
    }

    private static string? GetFailureReason(ApplicationFailure failure)
        => failure.Details is not null
            && failure.Details.TryGetValue("reason", out string? reason)
                ? reason
                : null;

    internal static string GetSafeExceptionType(Exception exception)
        => new(exception.GetType().Name
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .Take(64)
            .ToArray());

    private static bool IsReady(AndroidReadinessFacts facts)
        => facts.Transport.State == AndroidTransportState.Device
            && facts.Boot is { ShellAvailable: true, BootCompleted: 1 };

    private static bool? SameMuMuState(MuMuInstanceState initial, MuMuInstanceState final)
        => final.State == MuMuLifecycleState.Unknown ? null : final.State == initial.State;

    private static bool? SameGameState(AzurLaneGameObservation initial, AzurLaneGameObservation final)
        => final.State == AzurLaneGameState.Unknown ? null : final.State == initial.State;

    private static bool? SameReadiness(AndroidReadinessFacts initial, AndroidReadinessFacts final)
    {
        if (initial.Endpoint != final.Endpoint || final.Transport.State == AndroidTransportState.Unknown)
        {
            return initial.Endpoint != final.Endpoint ? false : null;
        }

        if (final.Transport.State != AndroidTransportState.Device)
        {
            return false;
        }

        if (final.Boot is not { } finalBoot)
        {
            return null;
        }

        if (!finalBoot.ShellAvailable || finalBoot.BootCompleted == 0)
        {
            return false;
        }

        if (finalBoot.BootCompleted is null)
        {
            return null;
        }

        return initial.Transport.State == final.Transport.State
            && initial.Boot?.ShellAvailable == finalBoot.ShellAvailable
            && initial.Boot?.BootCompleted == finalBoot.BootCompleted
            && string.Equals(initial.Boot?.AndroidRelease, finalBoot.AndroidRelease, StringComparison.Ordinal)
            && initial.Boot?.SdkLevel == finalBoot.SdkLevel;
    }

    private static bool? CombinePreservation(params bool?[] observations)
    {
        if (observations.Any(static result => result == false))
        {
            return false;
        }

        return observations.All(static result => result == true) ? true : null;
    }
}
