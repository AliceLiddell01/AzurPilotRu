using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.AndroidAcceptance;

internal sealed record AndroidTransportPreparationReport(
    string Build,
    string InstanceId,
    string? Endpoint,
    string? MuMuState,
    string? InitialTransport,
    string? FinalTransport,
    bool? ShellAvailable,
    int? BootCompleted,
    string? BootEvidence,
    bool ConnectAttempted,
    bool? AuditPassed,
    string? AuditFailure,
    string AuditSummary,
    string AuditForms,
    string Status,
    bool IsProven,
    string? FailureCode,
    string? PreflightBlocker);

/// <summary>Явная подготовка transport в тестовом acceptance-инструменте.</summary>
/// <remarks>
/// Runner проверяет точную MuMu identity и состояние <c>Running</c>, использует bundled ADB и
/// существующий resolver/readiness owner. Единственная возможная mutation находится в
/// <see cref="AndroidReadinessService.PrepareTransportAsync"/> и разрешается только при <c>Absent</c>.
/// </remarks>
internal sealed class AndroidTransportPreparationRunner
{
    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly IAndroidHost _androidHost;
    private readonly AndroidReadinessService _readiness;
    private readonly AndroidCommandAudit _audit;

    internal AndroidTransportPreparationRunner(
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
        AndroidReadinessService readiness,
        AndroidCommandAudit audit)
    {
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(audit);

        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
        _androidHost = androidHost;
        _readiness = readiness;
        _audit = audit;
    }

    internal async Task<AndroidTransportPreparationReport> RunAsync(
        AndroidTransportPreparationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? controlExecutablePath = null;
        string? adbExecutablePath = null;
        string? endpointText = null;
        string? muMuState = null;
        string? initialTransport = null;
        string? finalTransport = null;
        bool? shellAvailable = null;
        int? bootCompleted = null;
        string? bootEvidence = null;
        AndroidEndpoint? endpoint = null;
        AndroidTransportPreparationOutcome? outcome = null;
        ApplicationFailure? failure = null;
        string? failureCodeOverride = null;
        string? blocker = null;
        string? auditFailure = null;
        bool? auditPassed = null;

        ApplicationResult<MuMuInstallation> installationResult = _muMuHost.DiscoverInstallation();
        if (installationResult.IsFailure)
        {
            failure = installationResult.FailureInfo!;
            blocker = "mumu_installation_unavailable";
            goto Complete;
        }

        MuMuInstallation installation = installationResult.Value!;
        controlExecutablePath = installation.ControlExecutablePath;

        MuMuInstanceResolution selection = _muMuLifecycle.Resolve(
            installation,
            MuMuInstanceSelection.Explicit(options.InstanceId));
        if (!selection.IsResolved || selection.Instance!.Id != options.InstanceId)
        {
            failure = selection.Failure;
            failureCodeOverride = failure is null ? "mumu_instance_identity_not_proven" : null;
            blocker = "mumu_instance_identity_not_proven";
            goto Complete;
        }

        ApplicationResult<MuMuInstanceState> stateResult =
            _muMuHost.ObserveInstanceState(installation, options.InstanceId);
        if (stateResult.IsFailure)
        {
            failure = stateResult.FailureInfo!;
            blocker = "mumu_state_not_observed";
            goto Complete;
        }

        muMuState = stateResult.Value!.State.ToString();
        if (stateResult.Value.State != MuMuLifecycleState.Running)
        {
            failureCodeOverride = "mumu_instance_not_running";
            blocker = "mumu_instance_not_running";
            goto Complete;
        }

        ApplicationResult<AndroidAdbExecutable> adbResult = _androidHost.DiscoverAdbExecutable(installation);
        if (adbResult.IsFailure)
        {
            failure = adbResult.FailureInfo!;
            blocker = "bundled_adb_unavailable";
            goto Complete;
        }

        adbExecutablePath = adbResult.Value!.Path;
        ApplicationResult<AndroidEndpoint> endpointResult =
            _readiness.ResolveEndpoint(installation, options.InstanceId);
        if (endpointResult.IsFailure)
        {
            failure = endpointResult.FailureInfo!;
            blocker = "exact_adb_endpoint_unresolved";
            goto Complete;
        }

        endpoint = endpointResult.Value!;
        endpointText = endpoint.Value.ToString();

        ApplicationResult<AndroidTransportPreparationOutcome> prepareResult =
            await _readiness.PrepareTransportAsync(endpoint.Value, cancellationToken).ConfigureAwait(false);
        if (prepareResult.IsFailure)
        {
            failure = prepareResult.FailureInfo!;
            if (failure.Details is not null)
            {
                _ = failure.Details.TryGetValue("initial_transport", out initialTransport);
                if (failure.Code == ApplicationFailure.AndroidTransportNotReady
                    && failure.Details.TryGetValue("state", out string? observedState))
                {
                    finalTransport = observedState;
                }

                _ = failure.Details.TryGetValue("evidence", out bootEvidence);
            }

            blocker = failure.Code == ApplicationFailure.AndroidTransportNotReady
                ? "android_transport_" + (finalTransport ?? "not_ready")
                : failure.Code == ApplicationFailure.AndroidNotReady
                    ? "android_boot_not_proven"
                    : "android_readiness_not_proven";
            goto Complete;
        }

        outcome = prepareResult.Value!;
        initialTransport = outcome.InitialTransportName;
        finalTransport = outcome.FinalTransportName;
        shellAvailable = outcome.Boot.ShellAvailable;
        bootCompleted = outcome.Boot.BootCompleted;
        bootEvidence = outcome.Boot.Evidence;

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

        if (auditPassed == false)
        {
            failureCodeOverride = "android_command_audit_failed";
            blocker = "android_command_audit_failed";
        }

        bool proven = outcome is not null && auditPassed == true;
        string? failureCode = proven
            ? null
            : failureCodeOverride ?? failure?.Code ?? "android_transport_preparation_not_proven";
        blocker ??= proven ? null : "android_transport_preparation_not_proven";
        string auditSummary = controlExecutablePath is null
            ? "не оценивался: preflight не обнаружил control surface"
            : _audit.Describe(adbExecutablePath);
        string auditForms = controlExecutablePath is null
            ? "не оценивались: preflight не обнаружил control surface"
            : _audit.DescribeForms(adbExecutablePath);

        return new AndroidTransportPreparationReport(
            AcceptanceBuildIdentity.For(typeof(AndroidTransportPreparationRunner)).Describe(),
            options.InstanceValue,
            endpointText,
            muMuState,
            initialTransport,
            finalTransport,
            shellAvailable,
            bootCompleted,
            bootEvidence,
            outcome?.ConnectAttempted == true
                || endpoint is AndroidEndpoint resolvedEndpoint
                    && adbExecutablePath is not null
                    && _audit.CountConnectAttempts(adbExecutablePath, resolvedEndpoint) > 0,
            auditPassed,
            auditFailure,
            auditSummary,
            auditForms,
            proven ? "PROVEN" : "BLOCKED",
            proven,
            failureCode,
            blocker);
    }
}
