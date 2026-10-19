namespace AzurPilot.Core.Game;

/// <summary>
/// Событие уже принятого решения игрового flow для необязательной диагностики. Timestamp — монотонные часы
/// того же TimeProvider, что и flow; FrameId ссылается на <see cref="GameFrameProvenance.FrameId"/>.
/// Событие только описывает production-решение и не участвует в нём.
/// </summary>
/// <param name="Kind">Вид события, например <c>tap_planned</c> или <c>phase_entered</c>.</param>
/// <param name="Timestamp">Момент события по монотонным часам flow.</param>
public sealed record GameUiTraceEvent(string Kind, long Timestamp)
{
    /// <summary>Кадр, к которому относится решение.</summary>
    public Guid? FrameId { get; init; }
    /// <summary>Переход каталога.</summary>
    public string? ActionId { get; init; }
    /// <summary>Фаза телеметрии или правило состояния.</summary>
    public string? PhaseId { get; init; }
    /// <summary>Ассет, к которому относится решение.</summary>
    public string? AssetId { get; init; }
    /// <summary>Причина отказа, исход или источник кадра.</summary>
    public string? Reason { get; init; }
    /// <summary>Классифицированное состояние либо итог.</summary>
    public string? State { get; init; }
    /// <summary>Физическая область решения (безопасная область ввода).</summary>
    public GamePixelRect? Region { get; init; }
    /// <summary>Физическая точка ввода.</summary>
    public GameInputPoint? Point { get; init; }
    /// <summary>Числовое значение события (длительность ожидания, доля полосы, возраст кадра).</summary>
    public double? Value { get; init; }
    /// <summary>Ограниченные роли ассетов действия (source, required, excluded, postconditions).</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary>
/// Необязательный получатель событий решений игрового flow. Реализация не бросает исключений, не блокирует
/// flow и не влияет на решения; без получателя flow не создаёт событий.
/// </summary>
public interface IGameUiTrace
{
    /// <summary>Принимает одно событие в момент принятия решения.</summary>
    void Record(GameUiTraceEvent traceEvent);
}

internal static class GameUiTraceExtensions
{
    // Диагностика не может изменить исход flow: отказ получателя поглощается здесь.
    internal static void Emit(this IGameUiTrace? trace, Func<GameUiTraceEvent> create)
    {
        if (trace is null)
        {
            return;
        }

        try
        {
            trace.Record(create());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }
}
