namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Запись установки, прочитанная из uninstall-раздела реестра.
/// </summary>
/// <remarks>
/// Запись описывает только то, что сообщает сама Windows; решение о принадлежности семейству MuMu
/// принимает discovery. Значения версии и пути остаются runtime data и константами проекта не являются.
/// </remarks>
public sealed record MuMuRegistryCandidate
{
    /// <summary>Путь ключа реестра, из которого прочитана запись.</summary>
    public required string RegistryKeyPath { get; init; }

    /// <summary>Отображаемое имя установки.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Версия установки, сообщённая uninstall-записью, или <see langword="null"/>.</summary>
    public string? DisplayVersion { get; init; }

    /// <summary>Каталог установки, сообщённый uninstall-записью, или <see langword="null"/>.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>Издатель, сообщённый uninstall-записью, или <see langword="null"/>.</summary>
    public string? Publisher { get; init; }
}

/// <summary>
/// Узкая граница чтения uninstall-записей реестра.
/// </summary>
/// <remarks>
/// Граница возвращает записи как данные и не решает, какие из них относятся к MuMu. Это позволяет
/// проверять фильтрацию семейства на production-логике discovery, подменяя только источник.
/// </remarks>
public interface IMuMuInstallationRegistrySource
{
    /// <summary>Читает записи uninstall-разделов реестра.</summary>
    /// <returns>Записи установок.</returns>
    IReadOnlyList<MuMuRegistryCandidate> ReadCandidates();
}
