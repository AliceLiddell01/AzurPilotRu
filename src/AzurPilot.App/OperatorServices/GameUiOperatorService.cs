using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Game;

namespace AzurPilot.App.OperatorServices;

/// <summary>Ограниченный итог безопасного открытия Dock и подтверждённого возврата.</summary>
public sealed record GameDockRoundtrip(GameActionOutcome Open, GameActionOutcome? Close);

public sealed partial class AzurPilotOperatorService
{
    /// <summary>Свежий снимок и поиск известных ассетов, без игрового ввода.</summary>
    public async Task<ApplicationResult<GameUiObservation>> ObserveGameUiAsync(MuMuInstanceId instanceId, CancellationToken cancellationToken)
    {
        ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
        if (target.IsFailure)
        {
            return ApplicationResult<GameUiObservation>.Failure(target.FailureInfo!);
        }

        try
        {
            string[] assetIds = [.. GameAssetCatalog.Default.Assets.Select(a => a.Id)];
            return await _gameUi.ObserveAsync(instanceId, target.Value!.Endpoint, assetIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<GameUiObservation>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }
    }

    /// <summary>Доказывает viewport текущего кадра без ввода.</summary>
    public async Task<ApplicationResult<bool>> ObserveGameInputAsync(MuMuInstanceId instanceId, CancellationToken cancellationToken)
    {
        ApplicationResult<GameUiObservation> observed = await ObserveGameUiAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return observed.IsFailure ? ApplicationResult<bool>.Failure(observed.FailureInfo!)
            : await _gameUi.ValidateInputAsync(observed.Value!.Frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Целый обратимый сценарий под общей межпроцессной арендой exact identity.</summary>
    public Task<ApplicationResult<GameDockRoundtrip>> RunGameDockRoundtripAsync(MuMuInstanceId instanceId,
        GameActionProgress progress, CancellationToken cancellationToken)
    {
        return WithMutationLeaseAsync(instanceId, async () =>
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<GameDockRoundtrip>.Failure(target.FailureInfo!);
            }

            try
            {
                GameUiActionRunner runner = new(_gameUi, _timeProvider);
                GameActionOutcome open = await runner.RunAsync(instanceId, target.Value!.Endpoint,
                    GameAssetCatalog.Default.GetAction("dock.open"), progress, cancellationToken).ConfigureAwait(false);
                if (!open.ChangeProven)
                {
                    return ApplicationResult<GameDockRoundtrip>.Success(new(open, null));
                }

                GameActionOutcome close = await runner.RunAsync(instanceId, target.Value.Endpoint,
                    GameAssetCatalog.Default.GetAction("dock.close"), progress, cancellationToken).ConfigureAwait(false);
                return ApplicationResult<GameDockRoundtrip>.Success(new(open, close));
            }
            catch (AzurPilotNativeBoundaryException exception)
            {
                return ApplicationResult<GameDockRoundtrip>.Failure(NativeBoundaryFailureMapper.Map(exception));
            }
        }, cancellationToken);
    }
}
