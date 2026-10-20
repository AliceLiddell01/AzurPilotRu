using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Необязательный наблюдатель реальных screenshot capture одного host. Он получает события той же
/// production-команды и не может запросить дополнительный захват, изменить результат или удержать кадр:
/// <see cref="FrameReleasing"/> вызывается синхронно перед освобождением native frame, после всей
/// production-работы с ним. Реализация не бросает исключений; отказ наблюдателя на захват не влияет.
/// </summary>
public interface IAndroidScreenshotObserver
{
    /// <summary>Начало ADB-команды захвата; timestamp совпадает с provenance успешного кадра.</summary>
    void CaptureStarted(MuMuInstanceId instanceId, AndroidEndpoint endpoint, long captureStartedTimestamp);

    /// <summary>Захват не дал кадра; изображения нет.</summary>
    void CaptureFailed(MuMuInstanceId instanceId, AndroidEndpoint endpoint, long captureStartedTimestamp,
        ApplicationFailure failure);

    /// <summary>Кадр декодирован и проверен; владение передаётся вызывающему.</summary>
    void CaptureCompleted(AndroidScreenshotCaptureInfo info);

    /// <summary>Кадр ещё жив и будет освобождён сразу после возврата; ссылку на него сохранять запрещено.</summary>
    void FrameReleasing(NativeFrame frame, AndroidScreenshotCaptureInfo info);

    /// <summary>Маршрутизатор выбрал транспорт захвата или откатился на другой; причина машинная.</summary>
    void MethodSelected(Capture.ScreenshotSelection selection)
    {
    }
}
