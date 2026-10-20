using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>Монотонные длительности одного захвата.</summary>
/// <param name="Acquisition">Получение данных кадра транспортом (команда, запрос, ожидание нового кадра потока).</param>
/// <param name="Decode">Декодирование и преобразование в RGB8 внутри DLL.</param>
/// <param name="EndToEnd">От начала захвата до проверенного RGB8-кадра.</param>
public sealed record ScreenshotCaptureTiming(TimeSpan Acquisition, TimeSpan Decode, TimeSpan EndToEnd);

/// <summary>Факты, существующие только у транспорта ADB PNG.</summary>
/// <param name="CommandDuration">Monotonic длительность ADB-команды, сообщённая process boundary.</param>
/// <param name="EncodedPngByteCount">Число полученных encoded PNG-байтов.</param>
/// <param name="ProcessExitCode">Наблюдённый код выхода ADB.</param>
public sealed record AdbPngCaptureFacts(TimeSpan CommandDuration, int EncodedPngByteCount, int ProcessExitCode);

/// <summary>Сведения о кадре видеопотока.</summary>
/// <param name="Sequence">Номер декодированного кадра внутри сессии потока; растёт строго монотонно.</param>
/// <param name="PresentationMicroseconds">PTS кадра на устройстве (часы устройства).</param>
/// <param name="ArrivalTimestamp">Момент получения кадра хостом по монотонным часам процесса.</param>
/// <param name="StreamSession">Идентификатор сессии потока; новый поток начинает новую последовательность.</param>
public sealed record ScreenshotStreamFrame(ulong Sequence, long PresentationMicroseconds, long ArrivalTimestamp,
    string StreamSession);

/// <summary>Неизменяемые наблюдённые сведения об успешном screenshot capture.</summary>
/// <param name="InstanceId">Identity выбранного экземпляра, переданная владельцем selection.</param>
/// <param name="Endpoint">Точный адрес capture; association принадлежит существующему resolver.</param>
/// <param name="CapturedAtUtc">UTC-момент завершения захвата, decode и проверки metadata.</param>
/// <param name="Method">Транспорт, фактически выполнивший захват.</param>
/// <param name="Timing">Монотонные длительности захвата.</param>
/// <param name="FrameInfo">Проверенные размеры и RGB8-формат native frame.</param>
public sealed record AndroidScreenshotCaptureInfo(
    MuMuInstanceId InstanceId,
    AndroidEndpoint Endpoint,
    DateTimeOffset CapturedAtUtc,
    ScreenshotMethod Method,
    ScreenshotCaptureTiming Timing,
    NativeFrameInfo FrameInfo)
{
    /// <summary>
    /// Нижняя граница момента, когда содержимое кадра существовало на устройстве. Для запросных транспортов —
    /// начало запроса; для потока — момент получения минус консервативная граница задержки конвейера.
    /// Свежесть не продлевается временем передачи и декодирования.
    /// </summary>
    public DateTimeOffset CaptureStartedAtUtc { get; init; }
    /// <summary>Та же граница по монотонным часам текущего процесса.</summary>
    public long CaptureStartedTimestamp { get; init; }
    /// <summary>Число байтов полезной нагрузки транспорта (0 — кадр получен без передачи байтов, например nemu_ipc).</summary>
    public long PayloadByteCount { get; init; }
    /// <summary>Факты ADB PNG; null для остальных транспортов.</summary>
    public AdbPngCaptureFacts? AdbPng { get; init; }
    /// <summary>Сведения кадра потока; null для запросных транспортов.</summary>
    public ScreenshotStreamFrame? Stream { get; init; }
}
/// <summary>Владелец native frame успешного capture; encoded PNG не хранится.</summary>
public sealed class CapturedAndroidScreenshot : IDisposable
{
    private readonly Lock _gate = new();
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

    /// <summary>Подключает наблюдателя освобождения, если он ещё не задан.</summary>
    internal void AttachObserver(IAndroidScreenshotObserver observer)
    {
        lock (_gate)
        {
            _observer ??= observer;
        }
    }

    /// <summary>
    /// Идемпотентно освобождает единственный принадлежащий capture frame. Необязательный наблюдатель
    /// видит кадр один раз непосредственно перед освобождением; его отказ освобождение не отменяет.
    /// </summary>
    public void Dispose()
    {
        // Блокировка сохраняет порядок: параллельный Dispose не освободит кадр до завершения наблюдателя.
        lock (_gate)
        {
            IAndroidScreenshotObserver? observer = _observer;
            _observer = null;
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
        }

        GC.SuppressFinalize(this);
    }
}
