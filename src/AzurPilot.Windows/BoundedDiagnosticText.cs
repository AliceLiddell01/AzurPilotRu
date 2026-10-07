using System.Text;

namespace AzurPilot.Windows;

/// <summary>
/// Приводит диагностический текст Windows-адаптеров к bounded однострочной форме.
/// </summary>
/// <remarks>
/// <para>
/// Вывод внешних утилит и значения полей их ответов приходят извне и не ограничены по длине, поэтому
/// прежде чем такой текст попадёт в evidence наблюдения или в bounded вывод команды, он приводится к
/// одной строке и ограничивается по длине.
/// </para>
/// <para>
/// Ограничение применяется к диагностическому тексту, а не к разбору ответа: parser всегда работает с
/// полным захваченным выводом, а усечение здесь затрагивает только то, что сообщается наружу.
/// </para>
/// <para>
/// Владелец ограничения — платформенная boundary, а не отдельная возможность: одна форма bounded текста
/// обслуживает все Windows-адаптеры, поэтому второй bounded-text не заводится.
/// </para>
/// </remarks>
public static class BoundedDiagnosticText
{
    /// <summary>Максимальная длина bounded текста.</summary>
    /// <remarks>
    /// Значение совпадает по смыслу с ограничением evidence Core-контракта: bounded текст — это
    /// диагностический факт, а не полезная нагрузка. Длина результата
    /// <see cref="Bounded(string?)"/> не превышает это значение.
    /// </remarks>
    public const int MaxLength = 256;

    private const string TruncationMarker = "...";

    /// <summary>Приводит текст к однострочной bounded форме.</summary>
    /// <param name="value">Исходный текст или <see langword="null"/>.</param>
    /// <returns>
    /// Текст без управляющих символов и без повторяющихся пробелов, длина которого не превышает
    /// <see cref="MaxLength"/>.
    /// </returns>
    public static string Bounded(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string singleLine = Collapse(value);
        if (singleLine.Length <= MaxLength)
        {
            return singleLine;
        }

        return string.Concat(singleLine.AsSpan(0, MaxLength - TruncationMarker.Length), TruncationMarker);
    }

    private static string Collapse(string value)
    {
        StringBuilder builder = new(value.Length);
        bool pendingSeparator = false;

        foreach (char symbol in value)
        {
            if (char.IsWhiteSpace(symbol) || char.IsControl(symbol))
            {
                // Разделитель ставится только между значимыми символами, поэтому ведущие и хвостовые
                // пробелы исчезают, а переносы строк не превращаются в пустые строки.
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                _ = builder.Append(' ');
                pendingSeparator = false;
            }

            _ = builder.Append(symbol);
        }

        return builder.ToString();
    }
}
