namespace AzurPilot.Core.MuMu;

/// <summary>
/// Bounded evidence lifecycle-операции MuMu.
/// </summary>
/// <remarks>
/// Evidence собирается только из ограниченных данных: имён операций, кодов выхода и bounded evidence
/// наблюдения. Полный вывод control utility и произвольный payload в evidence не попадают, а слишком
/// длинное значение обрезается, чтобы structured log и details отказа оставались ограниченными.
/// </remarks>
internal static class MuMuEvidence
{
    /// <summary>Максимальная длина evidence, попадающего в итог операции и в structured log.</summary>
    internal const int MaxLength = 256;

    /// <summary>Признак отсутствия mutation: операция завершилась идемпотентным успехом.</summary>
    internal const string NoMutation = "none";

    /// <summary>Собирает bounded evidence достигнутого postcondition.</summary>
    /// <param name="mutations">Bounded описание выполненных mutation.</param>
    /// <param name="terminal">Доказанное наблюдение терминального состояния.</param>
    /// <returns>Ограниченная строка evidence.</returns>
    internal static string Compose(string mutations, MuMuInstanceState terminal)
        => Truncate(
            "mutations="
            + mutations
            + "; observed="
            + MuMuNames.StateName(terminal.State)
            + "; observation="
            + terminal.Evidence);

    /// <summary>Ограничивает длину evidence.</summary>
    /// <param name="value">Evidence, собранный из bounded данных.</param>
    /// <returns>Значение, длина которого не превышает <see cref="MaxLength"/>.</returns>
    internal static string Truncate(string value)
        => value.Length <= MaxLength ? value : value[..MaxLength] + "...";
}
