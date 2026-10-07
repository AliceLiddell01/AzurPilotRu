namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Источник версии установки.
/// </summary>
public enum MuMuVersionSource
{
    /// <summary>Версия получена из install metadata установки.</summary>
    InstallMetadata = 0,

    /// <summary>Версия получена из uninstall-записи реестра.</summary>
    UninstallRegistry = 1,
}

/// <summary>
/// Разрешённая установка MuMu: корень, точка входа control surface и evidence поддержки.
/// </summary>
/// <remarks>
/// <para>
/// Значение описывает установку, а не экземпляр. Абсолютные пути здесь — runtime data конкретной
/// машины: они не являются константами проекта и не попадают в исходники, тесты и документацию.
/// </para>
/// <para>
/// Evidence поддержки — версия вместе с источником, версия схемы install metadata, идентификатор
/// продукта, движки Android и путь точки входа. Номер версии сам по себе не является разрешением на
/// поддержку: форму control surface подтверждает <see cref="MuMuControlSurfaceProbe"/>.
/// </para>
/// </remarks>
public sealed record MuMuDiscoveredInstallation
{
    /// <summary>Абсолютный путь корня установки.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>Точка входа control surface установки.</summary>
    public required MuMuControlSurface ControlSurface { get; init; }

    /// <summary>Версия установки.</summary>
    public required string Version { get; init; }

    /// <summary>Источник, из которого получена версия.</summary>
    public required MuMuVersionSource VersionSource { get; init; }

    /// <summary>Имя продукта или <see langword="null"/>.</summary>
    public string? ProductName { get; init; }

    /// <summary>Идентификатор продукта или <see langword="null"/>.</summary>
    public string? ProductId { get; init; }

    /// <summary>Версия схемы install metadata или <see langword="null"/>.</summary>
    public string? InstallMetadataConfigVersion { get; init; }

    /// <summary>Путь файла install metadata или <see langword="null"/>, если версия взята из реестра.</summary>
    public string? InstallMetadataFilePath { get; init; }

    /// <summary>Путь uninstall-ключа реестра или <see langword="null"/>.</summary>
    public string? UninstallRegistryKeyPath { get; init; }

    /// <summary>Движки Android, установленные в составе продукта.</summary>
    public required IReadOnlyList<MuMuAndroidEngine> AndroidEngines { get; init; }
}

/// <summary>
/// Кандидат установки, который не удалось разрешить, вместе с причиной.
/// </summary>
/// <remarks>
/// Отклонённый кандидат сохраняется как диагностический факт: молчаливое отбрасывание корня скрыло бы
/// реальную установку от оператора.
/// </remarks>
public sealed record MuMuRejectedInstallationCandidate
{
    /// <summary>Нормализованный корень установки, который не разрешён.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>Причина отказа из набора <see cref="MuMuInstallationRejectionReasons"/>.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Документ install metadata, который не удалось разобрать.
/// </summary>
public sealed record MuMuRejectedInstallMetadataDocument
{
    /// <summary>Путь документа.</summary>
    public required string FilePath { get; init; }

    /// <summary>Причина отказа разбора.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Причины, по которым кандидат установки не становится разрешённой установкой.
/// </summary>
/// <remarks>
/// Причина — диагностический факт внутри структурированного результата discovery, а не код отказа.
/// </remarks>
public static class MuMuInstallationRejectionReasons
{
    /// <summary>Каталог установки, сообщённый источником, не является абсолютным путём.</summary>
    public const string InstallRootNotAbsolute = "install_root_not_absolute";

    /// <summary>Каталог установки отсутствует на диске.</summary>
    public const string InstallRootMissing = "install_root_missing";

    /// <summary>В каталоге установки нет точки входа control surface поддерживаемой раскладки.</summary>
    public const string ControlExecutableMissing = "control_executable_missing";

    /// <summary>Корню установки соответствуют несколько документов install metadata.</summary>
    public const string InstallMetadataAmbiguous = "install_metadata_ambiguous";

    /// <summary>Версию установки не удалось получить ни из install metadata, ни из реестра.</summary>
    public const string VersionUnavailable = "version_unavailable";
}

/// <summary>
/// Итог обнаружения установок MuMu.
/// </summary>
/// <remarks>
/// Отсутствие установки и несколько установок — ожидаемые результаты обнаружения, а не отказ
/// операции: отказом сообщается только невозможность выполнить само обнаружение.
/// </remarks>
public sealed record MuMuInstallationDiscoveryResult
{
    /// <summary>Итоговый статус обнаружения.</summary>
    public required MuMuInstallationDiscoveryStatus Status { get; init; }

    /// <summary>Разрешённые установки в порядке обнаружения.</summary>
    public required IReadOnlyList<MuMuDiscoveredInstallation> Installations { get; init; }

    /// <summary>Кандидаты, которые не удалось разрешить.</summary>
    public required IReadOnlyList<MuMuRejectedInstallationCandidate> RejectedCandidates { get; init; }

    /// <summary>Документы install metadata, которые не удалось разобрать.</summary>
    public required IReadOnlyList<MuMuRejectedInstallMetadataDocument> RejectedMetadataDocuments { get; init; }
}

/// <summary>
/// Статус обнаружения установок MuMu.
/// </summary>
public enum MuMuInstallationDiscoveryStatus
{
    /// <summary>Ни одной установки не обнаружено.</summary>
    NotFound = 0,

    /// <summary>Обнаружена ровно одна установка.</summary>
    Single = 1,

    /// <summary>Обнаружено несколько установок: доказуемого выбора нет.</summary>
    Ambiguous = 2,
}
