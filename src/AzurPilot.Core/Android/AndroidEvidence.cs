namespace AzurPilot.Core.Android;

/// <summary>
/// Bounded однострочный evidence Android-операции.
/// </summary>
/// <remarks>
/// <para>
/// Evidence собирается только из ограниченных данных: имён состояний и операций, кодов выхода, счётчиков
/// и bounded значений, сообщённых устройством. Полный вывод ADB и произвольный payload в evidence не
/// попадают.
/// </para>
/// <para>
/// Evidence всегда однострочен: переводы строк заменяются пробелом, а слишком длинное значение
/// обрезается, поэтому одна операция не может добавить несколько строк в structured log и в bounded
/// details отказа.
/// </para>
/// </remarks>
internal static class AndroidEvidence
{
    /// <summary>Максимальная длина evidence, попадающего в результат операции и в structured log.</summary>
    internal const int MaxLength = 256;

    /// <summary>Признак отсутствия mutation: операция завершилась без изменения состояния.</summary>
    internal const string NoMutation = "none";

    /// <summary>Собирает bounded однострочный evidence из частей наблюдения.</summary>
    /// <param name="parts">Части evidence в порядке их следования.</param>
    /// <returns>Ограниченная однострочная строка evidence.</returns>
    internal static string Compose(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        List<string> present = new(parts.Length);

        foreach (string? part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                present.Add(part);
            }
        }

        return Truncate(string.Join(';', present));
    }

    /// <summary>Ограничивает длину evidence и сводит его в одну строку.</summary>
    /// <param name="value">Evidence, собранный из bounded данных.</param>
    /// <returns>
    /// Однострочное значение, длина которого не превышает <see cref="MaxLength"/>: переводы строк
    /// заменяются пробелом.
    /// </returns>
    internal static string Truncate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string single = value.ReplaceLineEndings(" ");
        return single.Length <= MaxLength ? single : single[..MaxLength] + "...";
    }
}
