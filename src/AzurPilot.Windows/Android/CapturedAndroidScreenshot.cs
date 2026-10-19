using AzurPilot.Core.Android;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>Неизменяемые наблюдённые сведения об успешном screenshot capture.</summary>
/// <param name="InstanceId">Identity выбранного экземпляра, переданная владельцем selection.</param>
/// <param name="Endpoint">Точный адрес capture; association принадлежит существующему resolver.</param>
/// <param name="CapturedAtUtc">UTC-момент завершения успешной команды, decode и проверки metadata.</param>
/// <param name="CommandDuration">Monotonic длительность только ADB-команды, сообщённая process boundary.</param>
/// <param name="EncodedPngByteCount">Число полученных encoded PNG-байтов.</param>
/// <param name="ProcessExitCode">Наблюдённый код выхода ADB.</param>
/// <param name="FrameInfo">Проверенные размеры и RGB8-формат native frame.</param>
public sealed record AndroidScreenshotCaptureInfo(
    MuMuInstanceId InstanceId,
    AndroidEndpoint Endpoint,
    DateTimeOffset CapturedAtUtc,
    TimeSpan CommandDuration,
    int EncodedPngByteCount,
    int ProcessExitCode,
    NativeFrameInfo FrameInfo)
{
    /// <summary>Начало захвата: свежесть не продлевается временем передачи и декодирования PNG.</summary>
    public DateTimeOffset CaptureStartedAtUtc { get; init; }
    /// <summary>Момент начала захвата по монотонным часам текущего процесса.</summary>
    public long CaptureStartedTimestamp { get; init; }
}

/// <summary>Владелец native frame успешного capture; encoded PNG не хранится.</summary>
public sealed class CapturedAndroidScreenshot : IDisposable
{
    private IAndroidScreenshotObserver? _observer;

    internal CapturedAndroidScreenshot(NativeFrame frame, AndroidScreenshotCaptureInfo info,
        IAndroidScreenshotObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(info);
        Frame = frame;
        Info = info;
        _observer = observer;
    }

    /// <summary>Native-owned frame; использование и освобождение защищены его SafeHandle.</summary>
    public NativeFrame Frame { get; }

    /// <summary>Неизменяемые metadata и provenance успешного capture.</summary>
    public AndroidScreenshotCaptureInfo Info { get; }

    /// <summary>
    /// Идемпотентно освобождает единственный принадлежащий capture frame. Необязательный наблюдатель
    /// видит кадр один раз непосредственно перед освобождением; его отказ освобождение не отменяет.
    /// </summary>
    public void Dispose()
    {
        IAndroidScreenshotObserver? observer = Interlocked.Exchange(ref _observer, null);
        try
        {
            observer?.FrameReleasing(Frame, Info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Диагностика не может удержать кадр или изменить исход production-операции.
        }
        finally
        {
            Frame.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
