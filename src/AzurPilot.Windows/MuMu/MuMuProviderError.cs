namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Ошибка, которую вернул сам провайдер MuMu в теле JSON-ответа.
/// </summary>
/// <remarks>
/// Отказ провайдера — ожидаемый исход операции, а не исключение: он сообщается значением, рядом с
/// которым остаётся код и текст провайдера. Код провайдера не является application-level кодом отказа.
/// </remarks>
public sealed record MuMuProviderError
{
    /// <summary>Числовой код ошибки провайдера (например, отрицательный код «экземпляр не найден»).</summary>
    public required int Code { get; init; }

    /// <summary>Текст ошибки провайдера, ограниченный длиной ответа.</summary>
    public required string Message { get; init; }
}
