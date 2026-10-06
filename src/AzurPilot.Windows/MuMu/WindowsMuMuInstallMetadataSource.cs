namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Production-реализация границы чтения install metadata.
/// </summary>
/// <remarks>
/// <para>
/// Источник обходит подкаталоги продуктов в каталоге данных приложений пользователя и читает файл
/// install metadata каждого продукта. Имя каталога данных и имя файла принадлежат этому типу.
/// </para>
/// <para>
/// Каталог данных берётся из окружения как runtime data. Абсолютный путь конкретной машины константой
/// проекта не является и в исходниках не появляется.
/// </para>
/// <para>
/// Отдельный нечитаемый документ пропускается: он просто не является источником сведений, а корень
/// установки при этом может быть получен из uninstall-записи. Ошибка обхода каталога целиком
/// поднимается вызывающей стороне и переводится в application-level отказ в discovery.
/// </para>
/// </remarks>
public sealed class WindowsMuMuInstallMetadataSource : IMuMuInstallMetadataSource
{
    /// <summary>Имя каталога продуктов в каталоге данных приложений пользователя.</summary>
    public const string ProductDataDirectoryName = "Netease";

    /// <summary>Имя файла install metadata.</summary>
    public const string InstallMetadataFileName = "install_config.json";

    private readonly IMuMuFileSystemProbe _fileSystemProbe;
    private readonly string _productDataDirectory;

    /// <summary>Создаёт источник, использующий каталог данных приложений текущего пользователя.</summary>
    /// <param name="fileSystemProbe">Граница файловой системы.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystemProbe"/> равен <see langword="null"/>.</exception>
    public WindowsMuMuInstallMetadataSource(IMuMuFileSystemProbe fileSystemProbe)
        : this(
            fileSystemProbe,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ProductDataDirectoryName))
    {
    }

    /// <summary>Создаёт источник с явно заданным каталогом продуктов.</summary>
    /// <param name="fileSystemProbe">Граница файловой системы.</param>
    /// <param name="productDataDirectory">Абсолютный путь каталога продуктов.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystemProbe"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="productDataDirectory"/> пуст.</exception>
    public WindowsMuMuInstallMetadataSource(IMuMuFileSystemProbe fileSystemProbe, string productDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystemProbe);
        ArgumentException.ThrowIfNullOrWhiteSpace(productDataDirectory);

        _fileSystemProbe = fileSystemProbe;
        _productDataDirectory = productDataDirectory;
    }

    /// <summary>Читает документы install metadata всех продуктов в каталоге данных приложений.</summary>
    /// <returns>Прочитанные документы.</returns>
    public IReadOnlyList<MuMuInstallMetadataDocument> ReadDocuments()
    {
        if (!_fileSystemProbe.DirectoryExists(_productDataDirectory))
        {
            return [];
        }

        List<MuMuInstallMetadataDocument> documents = [];

        foreach (string productDirectory in _fileSystemProbe.EnumerateDirectories(_productDataDirectory))
        {
            string metadataPath = Path.Combine(productDirectory, InstallMetadataFileName);

            if (!_fileSystemProbe.FileExists(metadataPath))
            {
                continue;
            }

            string content;

            try
            {
                content = _fileSystemProbe.ReadAllText(metadataPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            documents.Add(new MuMuInstallMetadataDocument { FilePath = metadataPath, Content = content });
        }

        return documents;
    }
}
