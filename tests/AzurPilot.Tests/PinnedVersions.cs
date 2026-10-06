using System.Globalization;
using System.Text.Json;

namespace AzurPilot.Tests;

/// <summary>Читает ожидания interop из нормативного заголовка ABI и manifest артефакта OpenCV.</summary>
internal static class PinnedVersions
{
    private const string AbiHeaderRelativePath = "native/include/azurpilot_native_abi.h";
    private const string OpenCvManifestRelativePath = "native/opencv.json";

    /// <summary>Корень checkout, найденный от тестовой сборки независимо от рабочей директории.</summary>
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Версия OpenCV из владельца native зависимости.</summary>
    internal static Version OpenCv { get; } = ReadOpenCvVersion();

    /// <summary>Нормативная версия ABI из заголовка границы.</summary>
    internal static uint NativeAbi { get; } = ReadNativeAbiVersion();

    private static Version ReadOpenCvVersion()
    {
        string path = Path.Combine(RepositoryRoot, OpenCvManifestRelativePath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.String
            || !Version.TryParse(version.GetString(), out Version? value)
            || value.Build < 0
            || value.Revision != -1)
        {
            throw new InvalidOperationException(
                $"Значение version в {OpenCvManifestRelativePath} должно иметь формат major.minor.patch.");
        }

        return value;
    }

    private static uint ReadNativeAbiVersion()
    {
        string path = Path.Combine(RepositoryRoot, AbiHeaderRelativePath);
        foreach (string line in File.ReadLines(path))
        {
            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3
                && string.Equals(tokens[0], "#define", StringComparison.Ordinal)
                && string.Equals(tokens[1], "AZURPILOT_NATIVE_ABI_VERSION", StringComparison.Ordinal)
                && uint.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint version))
            {
                return version;
            }
        }

        throw new InvalidOperationException(
            $"В {AbiHeaderRelativePath} отсутствует числовой AZURPILOT_NATIVE_ABI_VERSION.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, AbiHeaderRelativePath))
                && File.Exists(Path.Combine(directory.FullName, OpenCvManifestRelativePath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Не найден корень checkout выше «{AppContext.BaseDirectory}»: ожидаются "
            + $"{AbiHeaderRelativePath} и {OpenCvManifestRelativePath}.");
    }
}
