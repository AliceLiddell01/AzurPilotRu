using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Capture;

/// <summary>Точный target захвата: identity, endpoint, bundled ADB и установка MuMu одной цепочки выбора.</summary>
/// <param name="InstanceId">Identity экземпляра.</param>
/// <param name="Endpoint">Точный endpoint.</param>
/// <param name="AdbExecutablePath">Bundled ADB обнаруженной установки.</param>
/// <param name="Installation">Установка MuMu; нужна nemu_ipc.</param>
public sealed record ScreenshotTarget(MuMuInstanceId InstanceId, AndroidEndpoint Endpoint, string AdbExecutablePath,
    MuMuInstallation? Installation);

/// <summary>Общие зависимости сессий одного target.</summary>
internal sealed class ScreenshotSessionContext(ScreenshotTarget target, IWindowsProcessRunner runner,
    TimeProvider clock, AndroidWindowsHost android, string? ledgerDirectory = null)
{
    private CaptureDeviceFacts? _facts;

    internal ScreenshotTarget Target => target;
    internal TimeProvider Clock => clock;
    internal AndroidWindowsHost Android => android;
    internal CaptureAdb Adb { get; } = new(runner, target.AdbExecutablePath, target.Endpoint);
    internal CaptureHelperLedger Ledger { get; } = new(target.Endpoint, ledgerDirectory);

    internal async Task<CaptureDeviceFacts> FactsAsync(CancellationToken cancellationToken)
        => _facts ??= await Adb.QueryFactsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Освобождает ресурсы устройства, оставшиеся от завершившихся процессов AzurPilot.</summary>
    internal async Task CleanOrphansAsync(CancellationToken cancellationToken)
    {
        foreach (CaptureHelperResource orphan in Ledger.TakeOrphans())
        {
            await ReleaseAsync(orphan, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ReleaseAsync(CaptureHelperResource resource, CancellationToken cancellationToken)
    {
        switch (resource.Kind)
        {
            case "helper":
                _ = await Adb.StopHelperAsync(resource.Value, resource.Marker, cancellationToken).ConfigureAwait(false);
                break;
            case "forward":
                await Adb.RemoveForwardAsync(resource.Value, cancellationToken).ConfigureAwait(false);
                break;
            case "reverse":
                await Adb.RemoveReverseAsync(resource.Value, cancellationToken).ConfigureAwait(false);
                break;
            default:
                break;
        }

        Ledger.Remove(resource);
    }
}

/// <summary>
/// Открытая сессия одного транспорта на одном target. Сессия однопоточна: маршрутизатор не вызывает
/// захват параллельно. Освобождение идемпотентно и убирает все ресурсы устройства, созданные сессией.
/// </summary>
internal abstract class ScreenshotSession : IAsyncDisposable
{
    private readonly List<CaptureHelperResource> _resources = [];
    private int _disposed;

    protected ScreenshotSession(ScreenshotMethod method, ScreenshotSessionContext context)
    {
        Method = method;
        Context = context;
    }

    internal ScreenshotMethod Method { get; }

    protected ScreenshotSessionContext Context { get; }

    /// <summary>Длительность подготовки (зависимости, helper, соединение), не входящая в захваты.</summary>
    internal TimeSpan Setup { get; set; }

    /// <summary>Ограниченные факты сессии для отчёта: версии зависимостей, выбранный файл, порты не раскрываются.</summary>
    internal Dictionary<string, string> Facts { get; } = new(StringComparer.Ordinal);

    /// <summary>Сессия признана непригодной (тайм-аут провайдера, обрыв потока) и подлежит закрытию.</summary>
    internal bool Broken { get; private protected set; }

    protected TimeProvider Clock => Context.Clock;

    /// <summary>Подготавливает сессию; бросает <see cref="CaptureBackendException"/>.</summary>
    internal abstract Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Один захват нового кадра; бросает <see cref="CaptureBackendException"/>.</summary>
    protected abstract Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken);

    internal virtual async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return ApplicationResult<CapturedAndroidScreenshot>.Success(
                await CaptureCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (CaptureBackendException exception)
        {
            Broken |= exception.SessionBroken;
            return ApplicationResult<CapturedAndroidScreenshot>.Failure(ScreenshotCaptureFailures.From(exception, Method,
                Context.Target.InstanceId, Context.Target.Endpoint));
        }
        catch (NativeCaptureSourceException exception)
        {
            return ApplicationResult<CapturedAndroidScreenshot>.Failure(ScreenshotCaptureFailures.From(
                new CaptureBackendException(exception.IsDataFailure ? "frame_data_invalid" : "native_source_failed",
                    inner: exception)
                {
                    SessionBroken = !exception.IsDataFailure,
                },
                Method, Context.Target.InstanceId, Context.Target.Endpoint));
        }
    }

    /// <summary>Регистрирует ресурс устройства до его использования: журнал позволит освободить его после сбоя.</summary>
    protected void Track(CaptureHelperResource resource)
    {
        _resources.Add(resource);
        Context.Ledger.Add(resource);
    }

    protected async Task<int> StartTrackedHelperAsync(string classPath, string command, string marker,
        CancellationToken cancellationToken)
    {
        int pid = await Context.Adb.StartHelperAsync(classPath, command, cancellationToken).ConfigureAwait(false);
        Track(CaptureHelperLedger.Create("helper", pid, marker));
        return pid;
    }

    protected async Task<int> ForwardTrackedAsync(string remote, CancellationToken cancellationToken)
    {
        int port = await Context.Adb.ForwardAsync(remote, cancellationToken).ConfigureAwait(false);
        Track(CaptureHelperLedger.Create("forward", port));
        return port;
    }

    protected async Task<int> ReverseTrackedAsync(int hostPort, CancellationToken cancellationToken)
    {
        int port = await Context.Adb.ReverseAsync(hostPort, cancellationToken).ConfigureAwait(false);
        Track(CaptureHelperLedger.Create("reverse", port));
        return port;
    }

    /// <summary>
    /// Строит результат захвата с проверкой геометрии RGB8 и разбивкой длительностей. <paramref name="started"/> —
    /// нижняя граница существования содержимого (provenance); длительности считаются от
    /// <paramref name="requested"/>, если он задан, иначе от того же момента.
    /// </summary>
    protected CapturedAndroidScreenshot Complete(NativeFrame frame, CaptureMoment started, long acquired, long payloadBytes,
        ScreenshotStreamFrame? stream = null, long? requested = null)
    {
        long origin = requested ?? started.Timestamp;
        try
        {
            NativeFrameInfo info = frame.GetInfo();
            if (!ScreenshotFrameRules.IsValidRgb8(info))
            {
                throw new CaptureBackendException("frame_metadata_invalid");
            }

            long completed = Clock.GetTimestamp();
            return new CapturedAndroidScreenshot(frame, new AndroidScreenshotCaptureInfo(
                Context.Target.InstanceId, Context.Target.Endpoint, Clock.GetUtcNow(), Method,
                new ScreenshotCaptureTiming(Clock.GetElapsedTime(origin, acquired),
                    Clock.GetElapsedTime(acquired, completed), Clock.GetElapsedTime(origin, completed)), info)
            {
                CaptureStartedAtUtc = started.UtcNow,
                CaptureStartedTimestamp = started.Timestamp,
                PayloadByteCount = payloadBytes,
                Stream = stream,
            });
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    protected CaptureMoment Now() => new(Clock.GetTimestamp(), Clock.GetUtcNow());

    /// <summary>Освобождает ресурсы транспорта; ресурсы устройства — в обратном порядке создания.</summary>
    protected virtual ValueTask CloseTransportAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            // Освобождение не зависит от токена вызывающего: отменённая команда не должна оставлять helper.
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(20));
            for (int index = _resources.Count - 1; index >= 0; index--)
            {
                try
                {
                    await Context.ReleaseAsync(_resources[index], cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or CaptureBackendException)
                {
                    // Ресурс остаётся в журнале и будет освобождён при следующем открытии транспорта.
                }
            }

            _resources.Clear();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Момент начала захвата на монотонных и UTC часах.</summary>
internal readonly record struct CaptureMoment(long Timestamp, DateTimeOffset UtcNow);

/// <summary>Правила проверенной геометрии RGB8, общие для всех транспортов.</summary>
internal static class ScreenshotFrameRules
{
    internal static bool IsValidRgb8(NativeFrameInfo info)
    {
        ulong rowBytes = (ulong)info.Width * 3;
        ulong frameBytes = (ulong)info.StrideBytes * info.Height;
        return info.Width > 0
            && info.Height > 0
            && info.PixelFormat == AzurPilotNativeBridge.PixelFormatRgb8
            && info.StrideBytes >= rowBytes
            && info.ByteLength == frameBytes;
    }
}
