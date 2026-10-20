using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Владелец транспорта измерения с идемпотентным освобождением и счётом причин отказов.</summary>
internal sealed class ScreenshotTransportLease : IAsyncDisposable
{
    private readonly ScreenshotTransport? _transport;
    private int _disposed;

    private ScreenshotTransportLease(ScreenshotTransport? transport, ApplicationFailure? failure)
    {
        _transport = transport;
        Failure = failure;
    }

    internal ApplicationFailure? Failure { get; }

    internal ApplicationFailure? LastFailure { get; private set; }

    internal bool Broken => _transport?.Broken ?? true;

    internal TimeSpan Setup => _transport?.Setup ?? TimeSpan.Zero;

    internal IReadOnlyDictionary<string, string> Facts => _transport?.Facts ?? new Dictionary<string, string>();

    internal static async Task<ScreenshotTransportLease> OpenAsync(ScreenshotCaptureRouter router, MuMuInstanceId instanceId,
        AndroidEndpoint endpoint, ScreenshotMethod method, CancellationToken cancellationToken)
    {
        ApplicationResult<ScreenshotTransport> opened = await router.OpenTransportAsync(instanceId, endpoint, method,
            cancellationToken).ConfigureAwait(false);
        return opened.IsSuccess ? new(opened.Value, null) : new(null, opened.FailureInfo);
    }

    /// <summary>Один захват; отказ учитывается в <paramref name="reasons"/> и возвращается как null.</summary>
    internal async Task<CapturedAndroidScreenshot?> CaptureOrNullAsync(CancellationToken cancellationToken,
        Dictionary<string, int>? reasons = null)
    {
        if (_transport is null || Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        ApplicationResult<CapturedAndroidScreenshot> result = await _transport.CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        LastFailure = result.FailureInfo;
        if (reasons is not null)
        {
            string reason = result.FailureInfo!.Details?.GetValueOrDefault("reason") ?? result.FailureInfo.Code;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
