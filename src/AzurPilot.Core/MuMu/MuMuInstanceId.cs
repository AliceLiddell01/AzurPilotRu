using AzurPilot.Core.Configuration;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Стабильная identity Android-экземпляра MuMu: provider-owned vmindex.
/// </summary>
/// <remarks>
/// <para>
/// Identity — это ровно значение <see cref="Index"/> (vmindex MuMu, например <c>1</c>), а не
/// отображаемое имя и не позиция в перечислении: поиск, сравнение и сериализация выполняются по
/// значению этого типа.
/// </para>
/// <para>
/// Каноническая текстовая форма — <c>mumu:&lt;Index&gt;</c>; её возвращает <see cref="ToString"/> и
/// используют структурированные логи и bounded details отказов. Префикс формы читается у
/// <see cref="MuMuInstanceValue.ProviderPrefix"/>: синтаксис значения конфигурации принадлежит
/// конфигурации, и второй владелец того же wire-токена не заводится.
/// </para>
/// <para>
/// Экземпляр создаётся фабрикой <see cref="FromIndex"/>; пустой или пробельный index — ошибка
/// программирования вызывающей стороны, а не ожидаемый отказ MuMu.
/// </para>
/// </remarks>
public readonly record struct MuMuInstanceId
{
    private const string CanonicalPrefix = MuMuInstanceValue.ProviderPrefix;

    /// <summary>Создаёт identity экземпляра по provider-owned vmindex.</summary>
    /// <param name="index">vmindex MuMu, например <c>1</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="index"/> пуст, состоит из пробелов или равен <see langword="null"/>.
    /// </exception>
    public MuMuInstanceId(string index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        Index = index;
    }

    /// <summary>Provider-owned vmindex MuMu, которым определяется identity экземпляра.</summary>
    public string Index { get; }

    /// <summary>Создаёт identity экземпляра по provider-owned vmindex.</summary>
    /// <param name="index">vmindex MuMu, например <c>1</c>.</param>
    /// <returns>Identity экземпляра MuMu.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="index"/> пуст, состоит из пробелов или равен <see langword="null"/>.
    /// </exception>
    public static MuMuInstanceId FromIndex(string index) => new(index);

    /// <summary>Возвращает каноническую текстовую форму identity.</summary>
    /// <returns>Строка вида <c>mumu:&lt;Index&gt;</c>.</returns>
    public override string ToString() => CanonicalPrefix + Index;
}
