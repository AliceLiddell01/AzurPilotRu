namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец синтаксиса значения <c>mumu.instance</c> схемы v2.
/// </summary>
/// <remarks>
/// <para>
/// Допустимы ровно два вида значения: литерал <c>auto</c> — экземпляр выбирает провайдер, и
/// провайдерская форма <c>mumu:&lt;digits&gt;</c> — явный номер Android-экземпляра. Любое другое
/// значение (пустая строка, display name, <c>1</c>, <c>mumu:</c>, <c>mumu:01</c>, <c>mumu:-1</c>,
/// нестроковый тип) частью схемы v2 не является и отвергается загрузкой как
/// <see cref="Failures.ApplicationFailure.ConfigurationInvalid"/>.
/// </para>
/// <para>
/// Правило грамматики существует ровно здесь: строгий JSON-конвертер значения и любая будущая
/// проверка вызывают эти методы, а не повторяют перечень допустимых форм. Литерал <c>auto</c>
/// сравнивается с ordinal-семантикой, поэтому <c>Auto</c> и <c>AUTO</c> схемой не являются.
/// </para>
/// <para>
/// <c>&lt;digits&gt;</c> — неотрицательное целое без ведущих нулей, кроме самого <c>0</c>:
/// <c>mumu:0</c> и <c>mumu:12</c> допустимы, а <c>mumu:</c>, <c>mumu:01</c>, <c>mumu:-1</c>,
/// <c>mumu:+1</c> и <c>mumu:1.0</c> — нет. Верхняя граница номера экземпляра здесь не задаётся: она
/// принадлежит потребителю значения, а не схеме.
/// </para>
/// </remarks>
public static class MuMuInstanceValue
{
    /// <summary>Литерал, означающий, что экземпляр выбирает провайдер.</summary>
    public const string AutoValue = "auto";

    /// <summary>Префикс провайдерской формы значения: <c>mumu:</c>.</summary>
    public const string ProviderPrefix = "mumu:";

    /// <summary>Проверяет, выбрано ли значение провайдером, то есть точно ли оно равно <c>auto</c>.</summary>
    /// <param name="value">Значение свойства <c>mumu.instance</c>.</param>
    /// <returns><see langword="true"/>, если значение совпадает с <c>auto</c> с учётом регистра.</returns>
    public static bool IsAuto(string? value)
        => string.Equals(value, AutoValue, StringComparison.Ordinal);

    /// <summary>Проверяет, соответствует ли значение синтаксису схемы v2.</summary>
    /// <param name="value">Значение свойства <c>mumu.instance</c>.</param>
    /// <returns>
    /// <see langword="true"/>, если значение — это литерал <c>auto</c> либо провайдерская форма
    /// <c>mumu:&lt;digits&gt;</c> с неотрицательным целым без ведущих нулей.
    /// </returns>
    public static bool IsValid(string? value)
    {
        if (IsAuto(value))
        {
            return true;
        }

        if (value is null || !value.StartsWith(ProviderPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(ProviderPrefix.Length);
        if (digits.IsEmpty)
        {
            return false;
        }

        // Ведущий нуль допустим только у самого "0": "mumu:0" — валидное значение, "mumu:01" — нет.
        if (digits.Length > 1 && digits[0] == '0')
        {
            return false;
        }

        foreach (char digit in digits)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }
        }

        return true;
    }
}
