using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Game;

namespace AzurPilot.App.OperatorServices;

/// <summary>Lifecycle и UI — разные доказательства одной операции.</summary>
public sealed record GameUiEnsureOutcome(AzurLaneGameLifecycleOutcome? Game, GameUiReadinessOutcome? Ui);

/// <summary>
/// Фазы lifecycle внутри ensure на той же монотонной шкале, что и фазы UI. Сохраняется вызывающим и при отказе.
/// </summary>
public sealed class GameUiEnsureTimeline(TimeProvider clock)
{
    private static readonly string[] Known = ["lifecycle:lease", "lifecycle:mumu", "lifecycle:android", "lifecycle:game"];
    private readonly List<(string Id, long Entered, long? Exited, string? Reason)> _phases = [];

    internal long Origin { get; } = clock.GetTimestamp();

    /// <summary>Наблюдавшиеся фазы lifecycle и невстреченные с NotObserved.</summary>
    public IReadOnlyList<GameUiPhase> Phases
    {
        get
        {
            List<GameUiPhase> result = [.. _phases.Select(p => new GameUiPhase(p.Id, GameUiPhaseStatus.Observed,
                Milliseconds(p.Entered), p.Exited is long exited ? Milliseconds(exited) : null,
                p.Exited is long end ? Math.Round(clock.GetElapsedTime(p.Entered, end).TotalMilliseconds, 1) : null,
                1, p.Reason, null))];
            result.AddRange(Known.Where(id => _phases.All(p => p.Id != id)).Select(GameUiPhase.NotObserved));
            return result;
        }
    }

    internal void Enter(string id)
    {
        long now = clock.GetTimestamp();
        Exit(now, null);
        _phases.Add((id, now, null, null));
    }

    internal void Exit(string reason)
    {
        Exit(clock.GetTimestamp(), reason);
    }

    private void Exit(long now, string? reason)
    {
        int last = _phases.Count - 1;
        if (last >= 0 && _phases[last].Exited is null)
        {
            _phases[last] = _phases[last] with { Exited = now, Reason = reason ?? "completed" };
        }
    }

    private double Milliseconds(long timestamp)
    {
        return Math.Round(clock.GetElapsedTime(Origin, timestamp).TotalMilliseconds, 1);
    }
}

public sealed partial class AzurPilotOperatorService
{
    /// <summary>Только наблюдение; не подключает transport, не запускает окружение и не нажимает.</summary>
    public async Task<ApplicationResult<GameUiReadinessOutcome>> ObserveGameUiReadyAsync(MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
        if (target.IsFailure)
        {
            return ApplicationResult<GameUiReadinessOutcome>.Failure(target.FailureInfo!);
        }

        try
        {
            return ApplicationResult<GameUiReadinessOutcome>.Success(await Readiness(instanceId, target.Value!.Endpoint)
                .RunAsync(instanceId, target.Value.Endpoint, false, new(), cancellationToken).ConfigureAwait(false));
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<GameUiReadinessOutcome>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }
    }

    /// <summary>
    /// Один bounded flow и одна межпроцессная аренда: MuMu → Android → процесс → UI. Общий срок задаёт
    /// token вызывающего (Agent CLI deadline); собственного более короткого срока здесь нет.
    /// </summary>
    public Task<ApplicationResult<GameUiEnsureOutcome>> EnsureGameUiReadyAsync(MuMuInstanceId instanceId,
        GameActionProgress uiMutation, AndroidMutationProgress gameMutation, AndroidMutationProgress androidMutation,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? preparation, CancellationToken cancellationToken,
        GameUiEnsureTimeline? timeline = null)
    {
        timeline ??= CreateGameUiTimeline();
        timeline.Enter("lifecycle:lease");
        return WithMutationLeaseAsync(instanceId, async () =>
        {
            AzurPilotOperatorAndroidPreparationOutcome? prepared = null;
            bool androidEntered = false;
            ApplicationResult<AzurLaneGameLifecycleOutcome> started = await StartAzurLaneCoreAsync(instanceId, cancellationToken,
                progress: new SynchronousProgress<AzurPilotOperatorAndroidPreparationStage>(stage =>
                {
                    if (stage == AzurPilotOperatorAndroidPreparationStage.ResolveMuMu)
                    {
                        timeline.Enter("lifecycle:mumu");
                    }
                    else if (stage == AzurPilotOperatorAndroidPreparationStage.PrepareAndroid && !androidEntered)
                    {
                        androidEntered = true;
                        timeline.Exit(prepared?.MuMuStartOutcome is not null ? "started" : "already_running");
                        timeline.Enter("lifecycle:android");
                    }
                }),
                onGameStarting: () =>
                {
                    timeline.Exit(prepared?.AndroidPreparation?.ConnectAttempted == true ? "connected" : "already_ready");
                    timeline.Enter("lifecycle:game");
                },
                onPreparationCompleted: outcome =>
                {
                    prepared = outcome;
                    preparation?.Invoke(outcome);
                },
                mutationProgress: gameMutation,
                preparationMutationProgress: androidMutation).ConfigureAwait(false);
            if (started.IsFailure)
            {
                timeline.Exit(started.FailureInfo!.Code);
                return ApplicationResult<GameUiEnsureOutcome>.Failure(started.FailureInfo!);
            }

            timeline.Exit(started.Value!.InitialState == AzurLaneGameState.Stopped ? "started" : "already_running");
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<GameUiEnsureOutcome>.Failure(target.FailureInfo!);
            }

            try
            {
                GameUiReadinessOutcome ready = await Readiness(instanceId, target.Value!.Endpoint)
                    .RunAsync(instanceId, target.Value.Endpoint, true, uiMutation, cancellationToken,
                        allowInitialTransition: started.Value.InitialState == AzurLaneGameState.Stopped,
                        origin: timeline.Origin).ConfigureAwait(false);
                return ApplicationResult<GameUiEnsureOutcome>.Success(new(started.Value, ready));
            }
            catch (AzurPilotNativeBoundaryException exception)
            {
                return ApplicationResult<GameUiEnsureOutcome>.Failure(NativeBoundaryFailureMapper.Map(exception));
            }
        }, cancellationToken);
    }

    /// <summary>Шкала фаз ensure на часах этого сервиса.</summary>
    public GameUiEnsureTimeline CreateGameUiTimeline()
    {
        return new(_timeProvider);
    }

    private GameUiReadinessService Readiness(MuMuInstanceId instanceId, AndroidEndpoint endpoint)
    {
        GameAssetCatalog catalog = GameAssetCatalog.Default;
        return new(new ExactGameUiHost(this, instanceId, endpoint), catalog.Readiness, catalog.GetAction, _timeProvider);
    }

    // Повторное разрешение exact identity/endpoint закрывает исчезновение target между кадрами.
    // Это проверка operator association; захват, matcher и ввод остаются у существующего host.
    private sealed class ExactGameUiHost(AzurPilotOperatorService owner, MuMuInstanceId instanceId,
        AndroidEndpoint endpoint) : IGameUiHost
    {
        private ApplicationFailure? Check(MuMuInstanceId id, AndroidEndpoint address)
        {
            if (id != instanceId || address != endpoint)
            {
                return GameUiActionRunner.Failure("target_mismatch");
            }

            ApplicationResult<ResolvedAndroidTarget> current = owner.ResolveAndroidTarget(instanceId);
            return current.IsFailure ? current.FailureInfo : current.Value.Endpoint != endpoint
                ? GameUiActionRunner.Failure("endpoint_changed") : null;
        }

        public Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId id, AndroidEndpoint address,
            IReadOnlyList<string> assets, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationFailure? failure = Check(id, address);
            return failure is null ? owner._gameUi.ObserveAsync(id, address, assets, cancellationToken)
                : Task.FromResult(ApplicationResult<GameUiObservation>.Failure(failure));
        }

        public Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationFailure? failure = Check(frame.InstanceId, frame.Endpoint);
            return failure is null ? owner._gameUi.ValidateInputAsync(frame, cancellationToken)
                : Task.FromResult(ApplicationResult<bool>.Failure(failure));
        }

        public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationFailure? failure = Check(tap.Frame.InstanceId, tap.Frame.Endpoint);
            return failure is null ? owner._gameUi.TapAsync(tap, cancellationToken)
                : Task.FromResult(ApplicationResult<AndroidCommandOutcome>.Failure(failure));
        }
    }
}
