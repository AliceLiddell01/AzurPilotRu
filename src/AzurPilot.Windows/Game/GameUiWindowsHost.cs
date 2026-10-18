using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Game;

/// <summary>Использует существующий механизм захвата и владения кадром и тот же адаптер Android.</summary>
public sealed class GameUiWindowsHost(IAndroidScreenshotHost screenshots, IAndroidGameInputHost input) : IGameUiHost
{
    /// <inheritdoc />
    public async Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        IReadOnlyList<string> assets, CancellationToken cancellationToken)
    {
        try
        {
            ApplicationResult<CapturedAndroidScreenshot> result = await screenshots.CaptureScreenshotAsync(
                instanceId, endpoint, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return ApplicationResult<GameUiObservation>.Failure(result.FailureInfo!);
            }

            using CapturedAndroidScreenshot capture = result.Value!;
            GameFrameProvenance provenance = new(Guid.NewGuid(), capture.Info.InstanceId, capture.Info.Endpoint,
                capture.Info.CaptureStartedAtUtc, capture.Info.CaptureStartedTimestamp,
                checked((int)capture.Info.FrameInfo.Width), checked((int)capture.Info.FrameInfo.Height));
            GameUiObservation observation = new GameUiRecognizer(GameAssetCatalog.Default).Observe(capture.Frame, provenance, assets);
            if (observation.Find("loading.download")?.Status == GameMatchStatus.Unique)
            {
                GamePixelRect band = new GameGeometry(provenance.Width, provenance.Height).Map(new(185, 671, 1100, 705));
                RegionRgbStatistics metric = capture.Frame.ScanRegion(new(band.Left, band.Top, band.Right, band.Bottom));
                observation = observation with { VisualActivity = Math.Round((metric.MeanRed + metric.MeanGreen + metric.MeanBlue) / 3, 3) };
            }

            return ApplicationResult<GameUiObservation>.Success(observation);
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
