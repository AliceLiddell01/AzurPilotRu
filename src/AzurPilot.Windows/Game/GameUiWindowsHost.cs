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
            // Полоса принадлежит каталогу; измеряется только у однозначно распознанного владельца полосы.
            GameAsset? progressOwner = catalog.Assets.SingleOrDefault(a => a.Progress is not null
                && observation.Find(a.Id)?.Status == GameMatchStatus.Unique);
            if (progressOwner?.Progress is GameProgressBar bar)
            {
                observation = observation with
                {
                    ProgressLevel = MeasureFill(capture.Frame, new GameGeometry(provenance.Width, provenance.Height), bar),
                };
            }

            return ApplicationResult<GameUiObservation>.Success(observation with
            {
                Cost = new(captureDuration, capture.Info.Timing.Acquisition, _clock.GetElapsedTime(matchStarted))
                {
                    CaptureMethod = Core.Capture.ScreenshotMethodNames.ToName(capture.Info.Method),
                },
            });
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<GameUiObservation>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }
    }

    /// <summary>
    /// Доля заполненной полосы: число заполненных сегментов слева, кратное ширине сегмента. Допустима только форма
    /// «заполненные — не более одного граничного — пустые»; иная картина (фон, перекрытие, шум) даёт null.
    /// </summary>
    internal static double? MeasureFill(NativeFrame frame, GameGeometry geometry, GameProgressBar bar)
    {
        GameRect region = bar.Region;
        int width = (region.Right - region.Left) / bar.Segments;
        int filled = 0;
        bool boundary = false, empty = false;
        for (int i = 0; i < bar.Segments; i++)
        {
            GamePixelRect band = geometry.MapInside(new(region.Left + (i * width), region.Top, region.Left + ((i + 1) * width), region.Bottom));
            RegionRgbStatistics segment = frame.ScanRegion(new(band.Left, band.Top, band.Right, band.Bottom));
            if (segment.MeanBlue >= bar.FilledMinimumBlue && segment.MeanBlue - segment.MeanRed >= bar.FilledMinimumBlueOverRed)
            {
                if (boundary || empty)
                {
                    return null;
                }

                filled++;
            }
            else if (segment.MeanBlue <= bar.EmptyMaximumBlue)
            {
                empty = true;
            }
            else if (boundary || empty)
            {
                return null;
            }
            else
            {
                boundary = true;
            }
        }

        return (double)filled / bar.Segments;
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
