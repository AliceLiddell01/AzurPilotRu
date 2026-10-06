using System.Globalization;
using System.Text.Json;

namespace AzurPilot.Tests;

/// <summary>
/// Читает закреплённые версии из <c>eng/versions.json</c> — единственного источника версий.
/// </summary>
/// <remarks>
/// Тест не дублирует номера версий: он проверяет, что native библиотека собрана ровно с тем OpenCV
/// и сообщает ровно ту версию ABI, которые закреплены в репозитории. Поиск корня репозитория идёт
/// от каталога тестовой сборки вверх до файла <c>eng/versions.json</c>, поэтому тест не зависит от
/// текущей рабочей директории.
/// </remarks>
internal static class PinnedVersions
{
    private const string RepositoryMarkerFileName = "versions.json";
    private const string RepositoryMarkerDirectoryName = "eng";

    /// <summary>Версия OpenCV, закреплённая в репозитории.</summary>
    internal static Version OpenCv { get; } = ReadOpenCvVersion();

    /// <summary>Версия ABI, закреплённая в репозитории (зеркало значения из заголовка ABI).</summary>
    internal static uint NativeAbi { get; } = ReadNativeAbiVersion();

    private static Version ReadOpenCvVersion()
    {
        using JsonDocument document = ReadVersionsDocument();
        string value = ReadStringProperty(document.RootElement, "opencv", "version");

        string[] components = value.Split('.');
        if (components.Length != 3)
        {
            throw new InvalidOperationException(
                $"Значение opencv.version в eng/versions.json: «{value}»; ожидается формат major.minor.patch.");
        }

        return new Version(ParseComponent(components[0], "opencv.version"), ParseComponent(components[1], "opencv.version"), ParseComponent(components[2], "opencv.version"));
    }

    private static uint ReadNativeAbiVersion()
    {
        using JsonDocument document = ReadVersionsDocument();
        JsonElement nativeAbi = ReadObjectProperty(document.RootElement, "nativeAbi");
        JsonElement version = ReadProperty(nativeAbi, "version", "nativeAbi.version");

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetUInt32(out uint value))
        {
            throw new InvalidOperationException(
                $"Значение nativeAbi.version в eng/versions.json: «{version}»; ожидается целое неотрицательное число.");
        }

        return value;
    }

    private static JsonDocument ReadVersionsDocument()
    {
        string path = Path.Combine(FindRepositoryRoot(), RepositoryMarkerDirectoryName, RepositoryMarkerFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Не найден файл закреплённых версий: {path}");
        }

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement ReadObjectProperty(JsonElement element, string name)
    {
        return ReadProperty(element, name, name);
    }

    private static JsonElement ReadProperty(JsonElement element, string name, string description)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidOperationException(
                $"В eng/versions.json отсутствует свойство «{description}»: контракт версий нарушен.");
        }

        return value;
    }

    private static string ReadStringProperty(JsonElement element, string objectName, string propertyName)
    {
        JsonElement nested = ReadObjectProperty(element, objectName);
        JsonElement value = ReadProperty(nested, propertyName, $"{objectName}.{propertyName}");

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Значение {objectName}.{propertyName} в eng/versions.json не является строкой.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int ParseComponent(string text, string description)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"Компонент «{text}» значения {description} не является целым числом.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string marker = Path.Combine(directory.FullName, RepositoryMarkerDirectoryName, RepositoryMarkerFileName);
            if (File.Exists(marker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Не найден корень репозитория: ни один каталог выше «{AppContext.BaseDirectory}» не содержит "
            + $"eng/{RepositoryMarkerFileName}.");
    }
}
