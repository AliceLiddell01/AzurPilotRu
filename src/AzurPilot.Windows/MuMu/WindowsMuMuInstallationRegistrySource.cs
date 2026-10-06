using Microsoft.Win32;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Production-реализация границы чтения uninstall-записей реестра.
/// </summary>
/// <remarks>
/// <para>
/// Читаются три представления uninstall-раздела: 64-битный и 32-битный виды локальной машины и
/// 64-битный вид текущего пользователя. Представление выбирается явно, поэтому запись не зависит от
/// разрядности процесса и не требует отдельного имени перенаправленного раздела.
/// </para>
/// <para>
/// Реализация ничего не фильтрует: записи возвращаются как данные, а принадлежность семейству MuMu
/// определяет discovery. Исключения доступа поднимаются вызывающей стороне и переводятся в
/// application-level отказ там же.
/// </para>
/// </remarks>
public sealed class WindowsMuMuInstallationRegistrySource : IMuMuInstallationRegistrySource
{
    /// <summary>Путь uninstall-раздела относительно выбранного представления реестра.</summary>
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Имя значения с отображаемым именем установки.</summary>
    public const string DisplayNameValueName = "DisplayName";

    /// <summary>Имя значения с версией установки.</summary>
    public const string DisplayVersionValueName = "DisplayVersion";

    /// <summary>Имя значения с каталогом установки.</summary>
    public const string InstallLocationValueName = "InstallLocation";

    /// <summary>Имя значения с издателем.</summary>
    public const string PublisherValueName = "Publisher";

    private static readonly (RegistryHive Hive, RegistryView View)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
    ];

    /// <summary>Читает записи uninstall-разделов реестра.</summary>
    /// <returns>Записи установок.</returns>
    public IReadOnlyList<MuMuRegistryCandidate> ReadCandidates()
    {
        List<MuMuRegistryCandidate> candidates = [];

        foreach ((RegistryHive hive, RegistryView view) in Locations)
        {
            ReadLocation(hive, view, candidates);
        }

        return candidates;
    }

    private static void ReadLocation(RegistryHive hive, RegistryView view, List<MuMuRegistryCandidate> candidates)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);

        if (uninstallKey is null)
        {
            return;
        }

        foreach (string subKeyName in uninstallKey.GetSubKeyNames())
        {
            using RegistryKey? entryKey = uninstallKey.OpenSubKey(subKeyName);

            if (entryKey is null)
            {
                continue;
            }

            candidates.Add(new MuMuRegistryCandidate
            {
                RegistryKeyPath = $"{hive}\\{view}\\{UninstallKeyPath}\\{subKeyName}",
                DisplayName = ReadString(entryKey, DisplayNameValueName) ?? string.Empty,
                DisplayVersion = ReadString(entryKey, DisplayVersionValueName),
                InstallLocation = ReadString(entryKey, InstallLocationValueName),
                Publisher = ReadString(entryKey, PublisherValueName),
            });
        }
    }

    private static string? ReadString(RegistryKey key, string valueName)
        => key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
}
