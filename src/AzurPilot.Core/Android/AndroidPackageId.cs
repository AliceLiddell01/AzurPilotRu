namespace AzurPilot.Core.Android;

/// <summary>
/// Валидируемое значение идентификатора Android-пакета.
/// </summary>
/// <remarks>
/// <para>
/// Тип — тонкая проверка входного значения, а не каталог известных пакетов: product identity живёт у
/// своего владельца (<c>AzurLaneProduct</c>), а здесь проверяется только пригодность строки.
/// </para>
/// <para>
/// Значение неизменяемо и сравнивается по значению. Оно должно быть непустым и не содержать пробельных
/// символов: пробельный или пустой идентификатор сделал бы форму команды ADB неоднозначной. Второе
/// ограничение намеренно минимально — грамматика имён пакетов принадлежит платформе Android, а не этому
/// типу, поэтому догадка о допустимых символах здесь не закрепляется.
/// </para>
/// <para>
/// Пустое значение — ошибка программирования вызывающей стороны, а не ожидаемый отказ Android.
/// </para>
/// </remarks>
public readonly record struct AndroidPackageId
{
    /// <summary>Создаёт идентификатор Android-пакета.</summary>
    /// <param name="value">Идентификатор пакета, например <c>com.example.app</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> пуст, состоит из пробелов или содержит пробельный символ.
    /// </exception>
    public AndroidPackageId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (ContainsWhitespace(value))
        {
            throw new ArgumentException(
                "Идентификатор Android-пакета не может содержать пробельные символы.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Значение идентификатора Android-пакета.</summary>
    /// <value>Непустая строка без пробельных символов.</value>
    public string Value { get; }

    /// <summary>Создаёт идентификатор Android-пакета.</summary>
    /// <param name="value">Идентификатор пакета, например <c>com.example.app</c>.</param>
    /// <returns>Проверенное значение идентификатора пакета.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> пуст, состоит из пробелов или содержит пробельный символ.
    /// </exception>
    public static AndroidPackageId FromValue(string value) => new(value);

    /// <summary>Возвращает значение идентификатора пакета.</summary>
    /// <returns>То же значение, что и <see cref="Value"/>.</returns>
    public override string ToString() => Value;

    private static bool ContainsWhitespace(string value)
    {
        foreach (char symbol in value)
        {
            if (char.IsWhiteSpace(symbol))
            {
                return true;
            }
        }

        return false;
    }
}
