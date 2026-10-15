using System.Collections.Frozen;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец строковой формы <see cref="OperatorUiMode"/> в каноническом YAML.
/// </summary>
/// <remarks>
/// <para>
/// Ключ <c>ui.mode</c> записывается строками <c>normal</c>, <c>manual</c> и <c>diagnostic</c>: это
/// wire-форма схемы, а не имя члена перечисления. Соответствие выводится из самого перечисления —
/// <see cref="Enum.GetName"/> в нижнем регистре ASCII, — поэтому второй перечень режимов не заводится и
/// добавление режима не требует правок здесь.
/// </para>
/// <para>
/// Преобразование выполняется без culture-зависимых операций: схема конфигурации не должна меняться от
/// региональных настроек процесса.
/// </para>
/// <para>
/// Владелец общий для чтения и записи: строгий YAML-ридер проверяет значение через
/// <see cref="TryParse"/>, а каноническая запись получает строку через <see cref="ToWireName"/>, поэтому
/// записанный файл гарантированно читается тем же кодом.
/// </para>
/// </remarks>
public static class OperatorUiModeNames
{
    private static readonly OperatorUiMode[] Modes = Enum.GetValues<OperatorUiMode>();

    private static readonly string[] WireNames = [.. Modes.Select(ToWireName)];

    private static readonly FrozenDictionary<string, OperatorUiMode> ByWireName =
        Modes.ToFrozenDictionary(ToWireName, mode => mode, StringComparer.Ordinal);

    /// <summary>Возвращает все допустимые строки ключа <c>ui.mode</c> в порядке объявления перечисления.</summary>
    /// <returns>Wire-имена режимов, пригодные для сообщения об отказе.</returns>
    public static IReadOnlyList<string> All => WireNames;

    /// <summary>Проверяет, является ли строка допустимым значением ключа <c>ui.mode</c>.</summary>
    /// <param name="wireName">Строка значения из YAML-документа.</param>
    /// <param name="mode">Режим, соответствующий строке.</param>
    /// <returns>
    /// <see langword="true"/>, если строка точно совпадает с wire-именем режима в нижнем регистре.
    /// </returns>
    public static bool TryParse(string? wireName, out OperatorUiMode mode)
    {
        mode = default;
        return wireName is not null && ByWireName.TryGetValue(wireName, out mode);
    }

    /// <summary>Возвращает wire-имя режима для канонической записи.</summary>
    /// <param name="mode">Режим операторского интерфейса.</param>
    /// <returns>Имя режима в нижнем регистре ASCII.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Значение не имеет имени в перечислении: такое значение не может прийти из успешно загруженной
    /// конфигурации, поэтому это ошибка программирования, а не ожидаемый отказ.
    /// </exception>
    public static string ToWireName(OperatorUiMode mode)
    {
        string name = Enum.GetName(mode)
            ?? throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Режим операторского интерфейса не имеет имени в перечислении.");

        char[] characters = name.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            if (character is >= 'A' and <= 'Z')
            {
                characters[index] = (char)(character + ('a' - 'A'));
            }
        }

        return new string(characters);
    }
}
