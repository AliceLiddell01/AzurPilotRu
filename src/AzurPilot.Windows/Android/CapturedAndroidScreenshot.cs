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
    /// <summary>Начало capture: свежесть не продлевается временем передачи и decode PNG.</summary>
    public DateTimeOffset CaptureStartedAtUtc { get; init; }
    /// <summary>Monotonic момент начала capture в текущем процессе.</summary>
    public long CaptureStartedTimestamp { get; init; }
}

/// <summary>Владелец native frame успешного capture; encoded PNG не хранится.</summary>
public sealed class CapturedAndroidScreenshot : IDisposable
{
    internal CapturedAndroidScreenshot(NativeFrame frame, AndroidScreenshotCaptureInfo info)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(info);
        Frame = frame;
        Info = info;
    }

    /// <summary>Native-owned frame; использование и освобождение защищены его SafeHandle.</summary>
    public NativeFrame Frame { get; }

    /// <summary>Неизменяемые metadata и provenance успешного capture.</summary>
    public AndroidScreenshotCaptureInfo Info { get; }

    /// <summary>Идемпотентно освобождает единственный принадлежащий capture frame.</summary>
    public void Dispose()
    {
        Frame.Dispose();
        GC.SuppressFinalize(this);
    }
}
