namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Узкая граница файловой системы, через которую discovery читает установку.
/// </summary>
/// <remarks>
/// Граница существует, чтобы production-логика discovery проверялась на подменяемом источнике, без
/// реально установленной MuMu и без зависимости от содержимого конкретной машины. Методы принимают
/// абсолютные пути, полученные из runtime-источников.
/// </remarks>
public interface IMuMuFileSystemProbe
{
    /// <summary>Проверяет существование файла.</summary>
    /// <param name="absolutePath">Абсолютный путь к файлу.</param>
    /// <returns><see langword="true"/>, если файл существует.</returns>
    bool FileExists(string absolutePath);

    /// <summary>Проверяет существование каталога.</summary>
    /// <param name="absolutePath">Абсолютный путь к каталогу.</param>
    /// <returns><see langword="true"/>, если каталог существует.</returns>
    bool DirectoryExists(string absolutePath);

    /// <summary>Перечисляет подкаталоги каталога.</summary>
    /// <param name="absolutePath">Абсолютный путь к каталогу.</param>
    /// <returns>Абсолютные пути подкаталогов.</returns>
    IReadOnlyList<string> EnumerateDirectories(string absolutePath);

    /// <summary>Читает текстовый файл целиком.</summary>
    /// <param name="absolutePath">Абсолютный путь к файлу.</param>
    /// <returns>Содержимое файла.</returns>
    string ReadAllText(string absolutePath);
}

/// <summary>
/// Production-реализация границы файловой системы поверх <see cref="File"/> и <see cref="Directory"/>.
/// </summary>
/// <remarks>
/// Реализация ничего не проецирует и не скрывает: исключения ввода-вывода и доступа поднимаются
/// вызывающей стороне, а в application-level отказ их переводит discovery.
/// </remarks>
public sealed class WindowsMuMuFileSystemProbe : IMuMuFileSystemProbe
{
    /// <summary>Проверяет существование файла.</summary>
    /// <param name="absolutePath">Абсолютный путь к файлу.</param>
    /// <returns><see langword="true"/>, если файл существует.</returns>
    public bool FileExists(string absolutePath) => File.Exists(absolutePath);

    /// <summary>Проверяет существование каталога.</summary>
    /// <param name="absolutePath">Абсолютный путь к каталогу.</param>
    /// <returns><see langword="true"/>, если каталог существует.</returns>
    public bool DirectoryExists(string absolutePath) => Directory.Exists(absolutePath);

    /// <summary>Перечисляет подкаталоги каталога.</summary>
    /// <param name="absolutePath">Абсолютный путь к каталогу.</param>
    /// <returns>Абсолютные пути подкаталогов.</returns>
    public IReadOnlyList<string> EnumerateDirectories(string absolutePath)
        => [.. Directory.EnumerateDirectories(absolutePath)];

    /// <summary>Читает текстовый файл целиком.</summary>
    /// <param name="absolutePath">Абсолютный путь к файлу.</param>
    /// <returns>Содержимое файла.</returns>
    public string ReadAllText(string absolutePath) => File.ReadAllText(absolutePath);
}
