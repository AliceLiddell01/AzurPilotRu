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

    /// <summary>Один bounded flow и одна межпроцессная аренда: MuMu → Android → процесс → UI.</summary>
    public Task<ApplicationResult<GameUiEnsureOutcome>> EnsureGameUiReadyAsync(MuMuInstanceId instanceId,
        GameActionProgress uiMutation, AndroidMutationProgress gameMutation, AndroidMutationProgress androidMutation,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? preparation, CancellationToken cancellationToken)
    {
        return WithMutationLeaseAsync(instanceId, async () =>
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3), _timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            CancellationToken token = linked.Token;
            ApplicationResult<AzurLaneGameLifecycleOutcome> started = await StartAzurLaneCoreAsync(instanceId, token,
                onPreparationCompleted: preparation, mutationProgress: gameMutation,
                preparationMutationProgress: androidMutation).ConfigureAwait(false);
            if (started.IsFailure)
            {
                return ApplicationResult<GameUiEnsureOutcome>.Failure(started.FailureInfo!);
            }

            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<GameUiEnsureOutcome>.Failure(target.FailureInfo!);
            }

            try
            {
                GameUiReadinessOutcome ready = await Readiness(instanceId, target.Value!.Endpoint)
                    .RunAsync(instanceId, target.Value.Endpoint, true, uiMutation, token).ConfigureAwait(false);
                return ApplicationResult<GameUiEnsureOutcome>.Success(new(started.Value, ready));
            }
            catch (AzurPilotNativeBoundaryException exception)
            {
                return ApplicationResult<GameUiEnsureOutcome>.Failure(NativeBoundaryFailureMapper.Map(exception));
            }
        }, cancellationToken);
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
