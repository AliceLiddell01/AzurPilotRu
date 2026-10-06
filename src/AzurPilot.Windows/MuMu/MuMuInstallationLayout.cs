namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Раскладка файлов установки MuMu, из которой выводится точка входа control surface.
/// </summary>
/// <remarks>
/// <para>
/// Раскладка — часть формы control surface поддерживаемого семейства: точка входа лежит в подкаталоге
/// <c>nx_main</c> и называется <c>MuMuManager.exe</c>. Имя каталога и имя файла принадлежат только этому
/// типу, поэтому discovery не дублирует их.
/// </para>
/// <para>
/// Путь собирается от корня установки, полученного из runtime-источников. Абсолютный путь конкретной
/// машины константой проекта не является и в исходниках не появляется.
/// </para>
/// </remarks>
public static class MuMuInstallationLayout
{
    /// <summary>Имя подкаталога установки, содержащего точку входа control surface.</summary>
    public const string ControlExecutableDirectoryName = "nx_main";

    /// <summary>Имя исполняемого файла control surface.</summary>
    public const string ControlExecutableFileName = "MuMuManager.exe";

    /// <summary>Возвращает абсолютный путь к точке входа control surface для корня установки.</summary>
    /// <param name="installRoot">Абсолютный путь корня установки.</param>
    /// <returns>Абсолютный путь к исполняемому файлу control surface.</returns>
    /// <exception cref="ArgumentException"><paramref name="installRoot"/> пуст.</exception>
    public static string GetControlExecutablePath(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return Path.Combine(installRoot, ControlExecutableDirectoryName, ControlExecutableFileName);
    }
}
