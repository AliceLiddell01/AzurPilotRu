using System.ComponentModel;
using System.Runtime.InteropServices;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.Windows;

/// <summary>Проверяет отдельный YAML до запуска изолированной сессии и перед сохранением.</summary>
public static partial class WindowsConfigurationIsolation
{
    private const string SeparateYamlRequired = "Изолированная UI-сессия требует существующий отдельный YAML.";

    /// <summary>Проверяет отдельный YAML и возвращает ожидаемые отказы как application result.</summary>
    /// <param name="configurationPath">Путь отдельной конфигурации.</param>
    /// <returns>Успех либо отказ изоляции с кодом <see cref="ApplicationFailure.InvalidArgument"/>.</returns>
    public static ApplicationResult TryValidate(string? configurationPath)
        => TryValidateAgainst(configurationPath, AzurPilotConfigurationPath.GetDefaultRuntimePath(),
            AzurPilotConfigurationPath.GetLegacyJsonRuntimePath());

    internal static ApplicationResult TryValidateAgainst(
        string? configurationPath,
        string userConfigurationPath,
        string userLegacyJsonPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return InvalidConfiguration(SeparateYamlRequired);
        }

        try
        {
            ValidateAgainst(configurationPath, userConfigurationPath, userLegacyJsonPath);
            return ApplicationResult.Success();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Win32Exception or UnauthorizedAccessException)
        {
            return InvalidConfiguration(exception.Message);
        }
    }

    internal static void ValidateAgainst(
        string configurationPath,
        string userConfigurationPath,
        string userLegacyJsonPath)
    {
        if (!Path.IsPathFullyQualified(configurationPath))
        {
            throw new ArgumentException(SeparateYamlRequired, nameof(configurationPath));
        }

        string userDirectory = Resolve(Path.GetDirectoryName(Path.GetFullPath(userConfigurationPath))!);
        string directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        foreach (string path in new[] { directory, configurationPath, Path.Combine(directory, Path.GetFileName(userLegacyJsonPath)) })
        {
            string resolved = Resolve(path);
            if (resolved.Equals(userDirectory, StringComparison.OrdinalIgnoreCase)
                || resolved.StartsWith(userDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Изолированная конфигурация не может находиться в пользовательском runtime-каталоге.", nameof(configurationPath));
            }
        }

        if (!File.Exists(configurationPath))
        {
            throw new ArgumentException(SeparateYamlRequired, nameof(configurationPath));
        }

        // Hard link не меняет окончательный путь, поэтому отдельно сравниваем идентичность файлов.
        using SafeFileHandle isolated = Open(configurationPath);
        FileIdentity candidate = Identity(isolated);
        foreach (string path in new[] { userConfigurationPath, userLegacyJsonPath })
        {
            if (File.Exists(path))
            {
                using SafeFileHandle user = Open(path);
                if (candidate == Identity(user))
                {
                    throw new ArgumentException("Изолированный YAML ссылается на пользовательский файл конфигурации.", nameof(configurationPath));
                }
            }
        }
    }

    private static ApplicationResult InvalidConfiguration(string message)
        => ApplicationResult.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.InvalidArgument,
            Message = message,
        });

    private static string Resolve(string path)
    {
        path = Path.GetFullPath(path);
        using SafeFileHandle handle = Open(path);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return error is 2 or 3 && Path.GetDirectoryName(path) is string parent
                ? Path.Combine(Resolve(parent), Path.GetFileName(path))
                : throw new Win32Exception(error, "Не удалось проверить изоляцию пути конфигурации.");
        }

        uint required = GetFinalPathNameByHandleW(handle, [], 0, 0);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        char[] buffer = new char[required];
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        return count == 0 || count >= buffer.Length
            ? throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось разрешить путь конфигурации.")
            : Path.TrimEndingDirectorySeparator(new string(buffer, 0, (int)count));
    }

    private static SafeFileHandle Open(string path)
    {
        return CreateFileW(path, 0, 7, 0, 3, 0x02000000, 0); // OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS; ссылки разрешает Windows.
    }

    private static unsafe FileIdentity Identity(SafeFileHandle handle)
    {
        return !GetFileInformationByHandleEx(handle, 18, out FileIdentity identity, (uint)sizeof(FileIdentity))
            ? throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось проверить идентичность файла конфигурации.")
            : identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong IdLow, ulong IdHigh);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint size, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint size);
}
