using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Результат запроса сведений об одном экземпляре.
/// </summary>
/// <remarks>
/// «Экземпляр не найден» — ожидаемый исход запроса, а не отказ adapter: он возвращается значением с
/// заполненным <see cref="ProviderError"/> и пустым <see cref="Instance"/>. Неизвестная форма ответа
/// так не выглядит: она даёт application-level отказ и сюда не попадает. Любой отказ провайдера
/// возвращается значением вместе с его кодом и сообщением, а смысл доказан только у
/// <see cref="MuMuProviderCodes.PlayerIndexNotFound"/>: неизвестный код не трактуется как «не найдено».
/// </remarks>
public sealed record MuMuInstanceQueryResult
{
    /// <summary>Запрошенная identity экземпляра.</summary>
    public required MuMuInstanceId Id { get; init; }

    /// <summary>Сведения об экземпляре или <see langword="null"/>, если провайдер сообщил об ошибке.</summary>
    public MuMuInstanceInfo? Instance { get; init; }

    /// <summary>Ошибка провайдера или <see langword="null"/>, если экземпляр найден.</summary>
    public MuMuProviderError? ProviderError { get; init; }

    /// <summary>Признак того, что сведения об экземпляре получены.</summary>
    public bool IsFound => Instance is not null;

    /// <summary>Признак доказанного отказа «запрошенного номера экземпляра нет».</summary>
    public bool IsIndexNotFound => ProviderError is { Code: MuMuProviderCodes.PlayerIndexNotFound };
}

/// <summary>
/// Запись перечисления экземпляров, которую провайдер вернул как ошибку, а не как экземпляр.
/// </summary>
/// <remarks>
/// Такие записи встречаются, когда запрашивается несколько номеров сразу и часть из них не существует:
/// провайдер возвращает общий объект, где по каждому номеру лежит либо экземпляр, либо объект ошибки.
/// Запись сохраняется, чтобы номер не был молча потерян при выборе экземпляра.
/// </remarks>
public sealed record MuMuUnavailableEntry
{
    /// <summary>Identity записи — номер экземпляра из ответа провайдера.</summary>
    public required MuMuInstanceId Id { get; init; }

    /// <summary>Ошибка провайдера для этой записи.</summary>
    public required MuMuProviderError ProviderError { get; init; }
}

/// <summary>
/// Результат перечисления Android-экземпляров MuMu.
/// </summary>
/// <remarks>
/// Перечисление охватывает и остановленные экземпляры: на реальной установке остановленный экземпляр
/// остаётся в ответе перечисления, поэтому выбор экземпляра не может опираться на наличие процесса.
/// </remarks>
public sealed record MuMuInstanceEnumeration
{
    /// <summary>Экземпляры, форма записи которых распознана.</summary>
    public required IReadOnlyList<MuMuInstanceInfo> Instances { get; init; }

    /// <summary>Записи, которые провайдер вернул как ошибку.</summary>
    public required IReadOnlyList<MuMuUnavailableEntry> UnavailableEntries { get; init; }
}
