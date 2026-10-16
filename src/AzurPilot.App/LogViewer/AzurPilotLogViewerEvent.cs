using Microsoft.Extensions.Logging;

namespace AzurPilot.App.LogViewer;

/// <summary>Одна структурированная запись журнала, передаваемая отдельной консоли.</summary>
/// <remarks>
/// <para>
/// Запись — единственная единица протокола канала: хост приложения публикует её, отдельный процесс
/// viewer-а только отображает. Полей ровно столько, сколько перечислено в контракте: секретов и
/// произвольных payload в событии нет.
/// </para>
/// <para>
/// Тип неизменяем: <see cref="Properties"/> — только последовательность пар, а не словарь, поэтому
/// отправитель не может изменить уже опубликованное событие через совместно используемую коллекцию.
/// </para>
/// </remarks>
/// <param name="TimestampUtc">Момент события в UTC.</param>
/// <param name="Level">Уровень логирования записи.</param>
/// <param name="Category">Категория логирования: полное имя владельца логгера.</param>
/// <param name="Message">Отформатированное сообщение записи.</param>
/// <param name="EventId">Стабильный идентификатор события либо <see langword="null"/>, если его нет.</param>
/// <param name="CorrelationId">
/// Correlation identifier операции: structured property project-owned события либо trace identifier
/// текущей activity.
/// </param>
/// <param name="Properties">
/// Прочие structured properties записи либо <see langword="null"/>, если их нет.
/// </param>
public sealed record AzurPilotLogViewerEvent(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? EventId = null,
    string? CorrelationId = null,
    IReadOnlyList<KeyValuePair<string, string>>? Properties = null);
