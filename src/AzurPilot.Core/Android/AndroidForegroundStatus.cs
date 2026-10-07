namespace AzurPilot.Core.Android;

/// <summary>
/// Состояние foreground на точном endpoint-е.
/// </summary>
/// <remarks>
/// <see cref="Foreground"/> подтверждает только запрошенный компонент: то, что на экране находится
/// что-то другое, даёт <see cref="Other"/>, а не «пакет не установлен» и не «процесс не запущен».
/// </remarks>
public enum AndroidForegroundStatus
{
    /// <summary>Запрошенный компонент находится на переднем плане.</summary>
    Foreground = 0,

    /// <summary>На переднем плане находится другой компонент.</summary>
    Other = 1,

    /// <summary>Foreground не доказан: наблюдение не дало распознанного ответа.</summary>
    Unknown = 2,
}
