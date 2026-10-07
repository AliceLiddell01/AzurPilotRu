using AzurPilot.Windows.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Раскладка установки MuMu, из которой выводится путь bundled ADB.
/// </summary>
/// <remarks>
/// <para>
/// Bundled ADB принадлежит установке: он лежит рядом с control utility, в подкаталоге <c>nx_main</c>
/// каталога установки, и называется <c>adb.exe</c>. Имя подкаталога принадлежит владельцу раскладки
/// MuMu и здесь не дублируется, а имя исполняемого файла — этому типу.
/// </para>
/// <para>
/// Цепочки поиска нет: ни путь из конфигурации, ни <c>ANDROID_SDK_ROOT</c>, ни <c>PATH</c>, ни «любой
/// найденный adb.exe» не рассматриваются. Подстановка похожего executable запрещена, поэтому путь
/// выводится ровно из корня обнаруженной установки.
/// </para>
/// <para>
/// Путь собирается от корня установки, полученного из runtime-источников. Абсолютный путь конкретной
/// машины константой проекта не является и в исходниках не появляется.
/// </para>
/// </remarks>
public static class AndroidInstallationLayout
{
    /// <summary>Имя исполняемого файла bundled ADB внутри подкаталога установки.</summary>
    public const string AdbExecutableFileName = "adb.exe";

    /// <summary>Возвращает абсолютный путь к bundled ADB для корня обнаруженной установки.</summary>
    /// <param name="installRoot">Абсолютный путь корня установки.</param>
    /// <returns>Абсолютный путь к исполняемому файлу bundled ADB.</returns>
    /// <exception cref="ArgumentException"><paramref name="installRoot"/> пуст.</exception>
    public static string GetAdbExecutablePath(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return Path.Combine(
            installRoot,
            MuMuInstallationLayout.ControlExecutableDirectoryName,
            AdbExecutableFileName);
    }
}
