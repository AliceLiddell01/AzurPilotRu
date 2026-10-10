using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>Однократный read-only PNG capture на уже выбранном exact Android target.</summary>
/// <remarks>
/// Bundled ADB должен быть подтверждён через существующий IAndroidHost до вызова. Surface не
/// разрешает endpoint, не подключает transport и не меняет состояние MuMu или игры. Identity и
/// endpoint передаются вызывающей стороной из одной доказанной цепочки выбора экземпляра.
/// </remarks>
public interface IAndroidScreenshotHost
{
    /// <summary>Захватывает PNG и декодирует его в native-owned RGB8 frame.</summary>
    /// <param name="instanceId">Identity уже выбранного экземпляра.</param>
    /// <param name="endpoint">Точный endpoint этого экземпляра.</param>
    /// <param name="cancellationToken">Запрос отмены ограниченной команды.</param>
    /// <returns>Frame с provenance либо bounded отказ; encoded PNG наружу не передаётся.</returns>
    Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
        MuMuInstanceId instanceId,
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken);
}
