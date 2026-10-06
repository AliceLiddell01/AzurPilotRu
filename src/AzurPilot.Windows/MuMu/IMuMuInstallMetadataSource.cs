namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Сведения о движке Android, установленном в составе MuMu.
/// </summary>
/// <remarks>
/// Движки — evidence установки: по ним видно, какие версии Android вообще доступны на машине. Они не
/// являются идентификатором экземпляра и не определяют выбор экземпляра.
/// </remarks>
public sealed record MuMuAndroidEngine
{
    /// <summary>Ключ движка в install metadata.</summary>
    public required string EngineKey { get; init; }

    /// <summary>Версия Android движка или <see langword="null"/>, если она не сообщена.</summary>
    public string? AndroidVersion { get; init; }
}

/// <summary>
/// Разобранные install metadata установки MuMu.
/// </summary>
/// <remarks>
/// Значение описывает установку целиком, а не отдельный экземпляр. Каталог установки здесь — runtime
/// data, полученная из файла, а не константа проекта.
/// </remarks>
public sealed record MuMuInstallMetadata
{
    /// <summary>Путь файла install metadata, из которого прочитаны сведения.</summary>
    public required string FilePath { get; init; }

    /// <summary>Версия схемы install metadata или <see langword="null"/>, если она не сообщена.</summary>
    public string? ConfigVersion { get; init; }

    /// <summary>Каталог установки, сообщённый install metadata.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>Имя продукта или <see langword="null"/>.</summary>
    public string? ProductName { get; init; }

    /// <summary>Идентификатор продукта или <see langword="null"/>.</summary>
    public string? ProductId { get; init; }

    /// <summary>Версия продукта или <see langword="null"/>.</summary>
    public string? Version { get; init; }

    /// <summary>Движки Android, сообщённые install metadata.</summary>
    public required IReadOnlyList<MuMuAndroidEngine> AndroidEngines { get; init; }
}

/// <summary>
/// Документ install metadata, прочитанный с диска и ещё не разобранный.
/// </summary>
public sealed record MuMuInstallMetadataDocument
{
    /// <summary>Абсолютный путь к файлу install metadata.</summary>
    public required string FilePath { get; init; }

    /// <summary>Содержимое файла.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Узкая граница чтения install metadata установок.
/// </summary>
/// <remarks>
/// Граница возвращает документы как данные и не решает, относятся ли они к MuMu: решение принимает
/// discovery. Это позволяет проверять разбор и сопоставление на production-логике, подменяя только
/// источник.
/// </remarks>
public interface IMuMuInstallMetadataSource
{
    /// <summary>Читает документы install metadata всех продуктов в каталоге данных приложений.</summary>
    /// <returns>Прочитанные документы.</returns>
    IReadOnlyList<MuMuInstallMetadataDocument> ReadDocuments();
}
