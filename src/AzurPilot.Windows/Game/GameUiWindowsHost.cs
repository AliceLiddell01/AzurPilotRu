using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Game;

/// <summary>Использует существующий механизм захвата и владения кадром и тот же адаптер Android.</summary>
public sealed class GameUiWindowsHost(IAndroidScreenshotHost screenshots, IAndroidGameInputHost input,
    TimeProvider? timeProvider = null) : IGameUiHost
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        IReadOnlyList<string> assets, CancellationToken cancellationToken)
    {
        try
        {
            long captureStarted = _clock.GetTimestamp();
            ApplicationResult<CapturedAndroidScreenshot> result = await screenshots.CaptureScreenshotAsync(
                instanceId, endpoint, cancellationToken).ConfigureAwait(false);
            TimeSpan captureDuration = _clock.GetElapsedTime(captureStarted);
            if (result.IsFailure)
            {
                return ApplicationResult<GameUiObservation>.Failure(result.FailureInfo!);
            }

            using CapturedAndroidScreenshot capture = result.Value!;
            long matchStarted = _clock.GetTimestamp();
            GameFrameProvenance provenance = new(Guid.NewGuid(), capture.Info.InstanceId, capture.Info.Endpoint,
                capture.Info.CaptureStartedAtUtc, capture.Info.CaptureStartedTimestamp,
                checked((int)capture.Info.FrameInfo.Width), checked((int)capture.Info.FrameInfo.Height));
            GameAssetCatalog catalog = GameAssetCatalog.Default;
            GameUiObservation observation = new GameUiRecognizer(catalog).Observe(capture.Frame, provenance, assets);
            // ROI полосы принадлежит каталогу; измеряется только у однозначно распознанного владельца полосы.
            GameAsset? progressOwner = catalog.Assets.SingleOrDefault(a => a.ProgressRegion is not null
                && observation.Find(a.Id)?.Status == GameMatchStatus.Unique);
            if (progressOwner?.ProgressRegion is GameRect region)
            {
                GamePixelRect band = new GameGeometry(provenance.Width, provenance.Height).MapInside(region);
                RegionRgbStatistics metric = capture.Frame.ScanRegion(new(band.Left, band.Top, band.Right, band.Bottom));
                observation = observation with { ProgressLevel = Math.Round((metric.MeanRed + metric.MeanGreen + metric.MeanBlue) / 3, 3) };
            }

            return ApplicationResult<GameUiObservation>.Success(observation with
            {
                Cost = new(captureDuration, capture.Info.CommandDuration, _clock.GetElapsedTime(matchStarted)),
            });
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<GameUiObservation>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }
    }

    /// <inheritdoc />
    public Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
    {
        return input.ValidateGameInputAsync(frame, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
    {
        return input.TapGameAsync(tap, cancellationToken);
    }
}
