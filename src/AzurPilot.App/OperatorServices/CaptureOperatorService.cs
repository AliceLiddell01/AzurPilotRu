using AzurPilot.App.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.OperatorServices;

public sealed partial class AzurPilotOperatorService
{
    /// <summary>Production-владелец захвата; null только в тестовых хостах с подменённым захватом.</summary>
    internal ScreenshotCaptureRouter? ScreenshotRouter => _screenshots as ScreenshotCaptureRouter;

    /// <summary>Точный endpoint и факты окружения для измерения транспортов; ADB обнаруживается здесь же.</summary>
    internal ApplicationResult<CaptureMeasurementTarget> ResolveCaptureTarget(MuMuInstanceId instanceId)
    {
        ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
        if (target.IsFailure)
        {
            return ApplicationResult<CaptureMeasurementTarget>.Failure(target.FailureInfo!);
        }

        ResolvedAndroidTarget value = target.Value!;
        return ApplicationResult<CaptureMeasurementTarget>.Success(new(instanceId, value.Endpoint,
            value.Installation.Version, value.Instance.AndroidVersion));
    }

    /// <summary>Измерение под той же межпроцессной арендой exact identity, что и остальные mutation.</summary>
    internal Task<ApplicationResult<T>> WithCaptureLeaseAsync<T>(MuMuInstanceId instanceId,
        Func<Task<ApplicationResult<T>>> action, CancellationToken cancellationToken)
        => WithMutationLeaseAsync(instanceId, action, cancellationToken);
}
