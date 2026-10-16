using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.App.Agent;

internal sealed class AgentContext(AzurPilotOperatorService? service)
{
    internal AzurPilotOperatorService Service => service
        ?? throw new InvalidOperationException("Discovery не имеет runtime services.");
    internal MuMuInstanceId? Target { get; set; }
    private readonly AndroidMutationProgress _gameMutation = new();
    private readonly AndroidMutationProgress _androidMutation = new();
    private string _effect = "none";
    internal string Effect
    {
        get => _effect == "changed" || _gameMutation.ChangeProven || _androidMutation.ChangeProven ? "changed"
            : _gameMutation.MutationAttempted || _androidMutation.MutationAttempted ? "mayHaveChanged" : _effect;
        private set => _effect = value;
    }
    internal string Stage { get; private set; } = "dispatch";
    internal AzurPilotOperatorAndroidPreparationOutcome? Preparation { get; private set; }
    internal AgentPayload? Completed { get; set; }

    internal static AgentPayload Result<T>(ApplicationResult<T> result)
    {
        return result.IsSuccess ? new(result.Value) : new(null, result.FailureInfo);
    }

    internal Task<AgentPayload> DiagnosticsAsync(bool detailed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AzurPilotDiagnosticReport report = Service.RefreshDiagnostics();
        object data = detailed ? new
        {
            report.Application,
            configuration = new { report.Configuration.Source, report.Configuration.SchemaVersion, report.Configuration.ValidationStatus },
            native = new
            {
                report.Native.IsAvailable,
                report.Native.IsAbiCompatible,
                report.Native.AbiVersion,
                version = report.Native.OpencvVersion?.ToString(),
                report.Native.Capabilities,
                failure = AgentJson.Failure(report.Native.Failure)
            },
            muMu = new
            {
                report.MuMu.IsInstallationDiscovered,
                report.MuMu.SelectedInstanceId,
                report.MuMu.LifecycleState,
                failure = AgentJson.Failure(report.MuMu.Failure)
            },
            android = new
            {
                report.Android.Endpoint,
                report.Android.TransportState,
                report.Android.IsShellAvailable,
                report.Android.BootCompleted,
                failure = AgentJson.Failure(report.Android.Failure)
            },
            game = report.AzurLane,
        } : new
        {
            nativeAvailable = report.Native.IsAvailable,
            instanceId = report.MuMu.SelectedInstanceId,
            muMuState = report.MuMu.LifecycleState,
            transport = report.Android.TransportState,
            game = report.AzurLane,
        };
        return Task.FromResult(new AgentPayload(data, report.Native.Failure));
    }

    internal Task<AgentPayload> InventoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ApplicationResult<AzurPilotOperatorMuMuInventory> result = Service.DiscoverMuMuInstances(
            AgentRequest.MaximumInventoryInstances, token);
        return Task.FromResult(result.IsFailure ? new AgentPayload(null, result.FailureInfo) : new AgentPayload(new
        {
            version = AgentJson.Safe(result.Value!.InstallationVersion),
            instances = result.Value.Instances.Select(i => new
            {
                instanceId = i.Instance.Id.ToString(),
                displayName = AgentJson.Safe(i.Instance.DisplayName),
                androidVersion = AgentJson.Safe(i.Instance.AndroidVersion),
                state = i.Observation?.State,
                failure = AgentJson.Failure(i.Failure),
            }),
        }));
    }

    internal async Task<AgentPayload> MuMuAsync(MuMuLifecycleOperation operation, CancellationToken token)
    {
        BeginMutation();
        ApplicationResult<MuMuLifecycleOutcome> result = await Service.RunMuMuLifecycleAsync(Target!.Value, operation, token)
            .ConfigureAwait(false);
        Effect = result.IsSuccess
            ? result.Value!.InitialState == result.Value.FinalState && operation != MuMuLifecycleOperation.Restart ? "none" : "changed"
            : FailureEffect(result.FailureInfo!);
        return new(result.IsSuccess ? result.Value : null, result.IsFailure ? result.FailureInfo : null, Effect);
    }

    internal async Task<AgentPayload> PrepareAsync(CancellationToken token)
    {
        BeginMutation();
        RecordPreparation(await Service.PrepareSelectedAndroidAsync(Target!.Value, token,
            new SynchronousProgress<AzurPilotOperatorAndroidPreparationStage>(s => Stage = s.ToString()), RecordPreparation, _androidMutation).ConfigureAwait(false));
        return new(Preparation, Preparation!.Failure, Effect);
    }

    internal async Task<AgentPayload> GameAsync(string action, CancellationToken token)
    {
        Stage = "game-lifecycle";
        Effect = "none";
        ApplicationResult<AzurLaneGameLifecycleOutcome> result = action switch
        {
            "start" => await Service.StartAzurLaneAsync(Target!.Value, token,
                new SynchronousProgress<AzurPilotOperatorAndroidPreparationStage>(s => Stage = s.ToString()),
                () => Stage = "game-lifecycle", RecordPreparation, _gameMutation, _androidMutation).ConfigureAwait(false),
            "stop" => await Service.StopAzurLaneAsync(Target!.Value, token, _gameMutation).ConfigureAwait(false),
            "restart" => await Service.RestartAzurLaneAsync(Target!.Value, token, _gameMutation).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (result.IsSuccess)
        {
            Effect = Effect == "changed" || result.Value!.MutationEvidence != "none" ? "changed" : "none";
        }
        else if (Preparation is null)
        {
            Effect = FailureEffect(result.FailureInfo!);
        }

        return new(new { preparation = Preparation, game = result.IsSuccess ? result.Value : null, stage = Stage }, result.IsFailure ? result.FailureInfo : null, Effect);
    }

    internal AgentPayload Interrupted(ApplicationFailure failure)
    {
        return new(new
        {
            stage = Stage,
            preparation = Preparation,
            completed = Completed?.Data,
            mutation = new { androidPreparation = _androidMutation, game = _gameMutation },
            postcondition = (object?)null,
        }, failure, Effect);
    }

    private void BeginMutation()
    {
        Stage = "mutation";
        Effect = "mayHaveChanged";
    }

    private void RecordPreparation(AzurPilotOperatorAndroidPreparationOutcome outcome)
    {
        Preparation = outcome;
        Stage = outcome.Stage.ToString();
        Effect = outcome.MuMuStartOutcome is not null
            ? "changed"
            : outcome.MuMuStartRequested && (outcome.Failure is null || !MutationWasNotAttempted(outcome.Failure))
                ? "mayHaveChanged" : "none";
    }

    private static string FailureEffect(ApplicationFailure failure)
    {
        return failure.Code is ApplicationFailure.TargetBusy or ApplicationFailure.MutationCoordinationUnavailable
            || MutationWasNotAttempted(failure) ? "none" : "mayHaveChanged";
    }

    private static bool MutationWasNotAttempted(ApplicationFailure failure)
    {
        return failure.Details is { } details
            && details.TryGetValue(ApplicationFailure.MutationAttemptedDetailKey, out string? attempted)
            && string.Equals(attempted, "false", StringComparison.Ordinal);
    }
}
