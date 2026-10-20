using AzurPilot.Core.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Строка матрицы совместимости транспорта.</summary>
/// <param name="Method">Транспорт.</param>
/// <param name="Delivery">request — кадр на запрос; stream — поток с номерами кадров; in-process — вызов DLL.</param>
/// <param name="Format">Формат данных до native RGB8.</param>
/// <param name="Requirements">Условия работы на target.</param>
/// <param name="Limitations">Известные ограничения и причины UNSUPPORTED.</param>
/// <param name="Dependencies">Идентификаторы закреплённых зависимостей manifest.</param>
internal sealed record CaptureCompatibilityEntry(ScreenshotMethod Method, string Delivery, string Format,
    string Requirements, string Limitations, IReadOnlyList<string> Dependencies);

/// <summary>Машинная матрица совместимости девяти транспортов; фактическая проверка — capture-benchmark.</summary>
internal static class CaptureCompatibility
{
    private static readonly string[] AScreenCap = [.. new[] { 5, 8, 9 }
        .SelectMany(build => new[] { "x86_64", "arm64-v8a", "x86", "armeabi-v7a" }.Select(abi => $"ascreencap-{build}-{abi}"))];

    internal static readonly IReadOnlyList<CaptureCompatibilityEntry> All =
    [
        new(ScreenshotMethod.Adb, "request", "PNG (screencap -p через exec-out)", "Подключённый ADB transport.",
            "Самый медленный на 4K из-за PNG; эталон корректности и bootstrap auto.", []),
        new(ScreenshotMethod.AdbNc, "request", "raw screencap (RGBA/RGB565)", "nc либо busybox nc на устройстве; adb reverse.",
            "Нет nc — UNSUPPORTED. Слушатель только на 127.0.0.1, принимается одно соединение от loopback.", []),
        new(ScreenshotMethod.Uiautomator2, "request", "JPEG (base64 JSON-RPC)", "u2.jar 0.4.0 через app_process; adb forward.",
            "JPEG с потерями: корректность проверяется по сетке с допуском.", ["uiautomator2-server"]),
        new(ScreenshotMethod.AScreenCap, "request", "BMZ1 (LZ4 BMP)", "Android SDK 21–28, ABI x86/x86_64/arm.",
            "Android 10+ (SDK ≥ 29) — UNSUPPORTED: бинарник отвергается системой.", AScreenCap),
        new(ScreenshotMethod.AScreenCapNc, "request", "BMZ1 (LZ4 BMP)", "Как aScreenCap и nc на устройстве.",
            "Как aScreenCap.", AScreenCap),
        new(ScreenshotMethod.DroidCast, "request", "PNG (HTTP /preview)", "DroidCast_raw 1.1 через app_process; adb forward.",
            "PNG-кодирование на устройстве.", ["droidcast-raw"]),
        new(ScreenshotMethod.DroidCastRaw, "request", "RGB565 (HTTP /screenshot)", "DroidCast_raw 1.1 через app_process; adb forward.",
            "RGB565: 5–6 бит на канал, корректность проверяется с допуском.", ["droidcast-raw"]),
        new(ScreenshotMethod.Scrcpy, "stream", "H.264 (scrcpy 5.0.1) → Media Foundation", "scrcpy-server 5.0.1; adb forward; H.264 MFT Windows.",
            "Кадр свежий только при приходе после запроса с учётом границы конвейера; повтор кадра потока не считается новым.",
            ["scrcpy-server"]),
        new(ScreenshotMethod.NemuIpc, "in-process", "RGBA с вертикальным отражением", "MuMu 12/nx: external_renderer_ipc.dll из обнаруженной установки.",
            "Только MuMu; DLL не распространяется проектом; вызов ограничен тайм-аутом на отдельном потоке.", []),
    ];
}
