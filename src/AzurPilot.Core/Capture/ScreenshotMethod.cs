using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace AzurPilot.Core.Capture;

/// <summary>Транспорт снимка экрана Android-экземпляра MuMu.</summary>
/// <remarks>Внешние имена совпадают с исходным проектом и принадлежат <see cref="ScreenshotMethodNames"/>.</remarks>
public enum ScreenshotMethod
{
    /// <summary><c>adb exec-out screencap -p</c>: PNG через stdout bundled ADB.</summary>
    Adb,

    /// <summary><c>screencap</c> без сжатия через <c>nc</c> устройства и target-scoped <c>adb reverse</c>.</summary>
    AdbNc,

    /// <summary>JSON-RPC <c>takeScreenshot</c> сервера uiautomator2 (JPEG).</summary>
    Uiautomator2,

    /// <summary>Бинарный aScreenCap: LZ4-сжатый BMP через stdout ADB.</summary>
    AScreenCap,

    /// <summary>aScreenCap через <c>nc</c> устройства и target-scoped <c>adb reverse</c>.</summary>
    AScreenCapNc,

    /// <summary>HTTP <c>/preview</c> DroidCast_raw (PNG).</summary>
    DroidCast,

    /// <summary>HTTP <c>/screenshot</c> DroidCast_raw (сырой RGB565).</summary>
    DroidCastRaw,

    /// <summary>H.264 видеопоток scrcpy-server с декодированием на стороне хоста.</summary>
    Scrcpy,

    /// <summary>Общая память рендерера MuMu через <c>external_renderer_ipc.dll</c> установки.</summary>
    NemuIpc,
}

/// <summary>Единственный владелец внешних имён транспортов снимков и значения выбора <c>auto</c>.</summary>
public static class ScreenshotMethodNames
{
    /// <summary>Значение настройки: метод выбирается автоматически по измерениям на точном target.</summary>
    public const string Auto = "auto";

    private static readonly (ScreenshotMethod Method, string Name)[] Pairs =
    [
        (ScreenshotMethod.Adb, "ADB"),
        (ScreenshotMethod.AdbNc, "ADB_nc"),
        (ScreenshotMethod.Uiautomator2, "uiautomator2"),
        (ScreenshotMethod.AScreenCap, "aScreenCap"),
        (ScreenshotMethod.AScreenCapNc, "aScreenCap_nc"),
        (ScreenshotMethod.DroidCast, "DroidCast"),
        (ScreenshotMethod.DroidCastRaw, "DroidCast_raw"),
        (ScreenshotMethod.Scrcpy, "scrcpy"),
        (ScreenshotMethod.NemuIpc, "nemu_ipc"),
    ];

    private static readonly FrozenDictionary<string, ScreenshotMethod> ByName =
        Pairs.ToFrozenDictionary(pair => pair.Name, pair => pair.Method, StringComparer.Ordinal);

    /// <summary>Все методы в каноническом порядке.</summary>
    public static IReadOnlyList<ScreenshotMethod> All { get; } = [.. Pairs.Select(pair => pair.Method)];

    /// <summary>Внешнее имя метода.</summary>
    public static string ToName(ScreenshotMethod method)
    {
        foreach ((ScreenshotMethod candidate, string name) in Pairs)
        {
            if (candidate == method)
            {
                return name;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(method), method, "Неизвестный метод снимка экрана.");
    }

    /// <summary>Разбирает точное (регистрозависимое) внешнее имя метода.</summary>
    public static bool TryParse(string? value, out ScreenshotMethod method)
    {
        if (value is not null && ByName.TryGetValue(value, out method))
        {
            return true;
        }

        method = default;
        return false;
    }

    /// <summary>Разбирает значение настройки: <c>auto</c> или точное имя метода.</summary>
    public static bool TryParseSetting(string? value, [NotNullWhen(true)] out ScreenshotMethodSetting? setting)
    {
        if (string.Equals(value, Auto, StringComparison.Ordinal))
        {
            setting = ScreenshotMethodSetting.Automatic;
            return true;
        }

        if (TryParse(value, out ScreenshotMethod method))
        {
            setting = ScreenshotMethodSetting.Explicit(method);
            return true;
        }

        setting = null;
        return false;
    }

    /// <summary>Перечень допустимых значений настройки для диагностики.</summary>
    public static string DescribeAllowedSettings()
        => Auto + ", " + string.Join(", ", Pairs.Select(pair => pair.Name));
}

/// <summary>Значение настройки метода: автоматический выбор либо явный метод.</summary>
public sealed record ScreenshotMethodSetting
{
    private ScreenshotMethodSetting(ScreenshotMethod? method)
    {
        Method = method;
    }

    /// <summary>Автоматический выбор.</summary>
    public static ScreenshotMethodSetting Automatic { get; } = new((ScreenshotMethod?)null);

    /// <summary>Явно заданный метод или null для <c>auto</c>.</summary>
    public ScreenshotMethod? Method { get; }

    /// <summary>Признак автоматического выбора.</summary>
    public bool IsAutomatic => Method is null;

    /// <summary>Явный выбор метода.</summary>
    public static ScreenshotMethodSetting Explicit(ScreenshotMethod method) => new(method);

    /// <inheritdoc />
    public override string ToString() => Method is ScreenshotMethod method
        ? ScreenshotMethodNames.ToName(method)
        : ScreenshotMethodNames.Auto;
}
