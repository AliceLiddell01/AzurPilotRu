using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец соответствия «имя уровня логирования → <see cref="LogLevel"/>».
/// </summary>
/// <remarks>
/// <para>
/// Перечень допустимых значений берётся из самого типа <see cref="LogLevel"/>: добавление уровня в
/// систему логирования не требует правок в Core, а вторая копия списка не появляется. Имена сравниваются с
/// ordinal-семантикой, поэтому <c>information</c> уровнем не является.
/// </para>
/// <para>
/// Владелец общий для всех форматов конфигурации: строгий JSON-конвертер legacy-документов и строгий
/// YAML-ридер вызывают его, а не повторяют перечень имён.
/// </para>
/// </remarks>
internal static class LogLevelNames
{
    private static readonly string[] Names = Enum.GetNames<LogLevel>();

    private static readonly FrozenDictionary<string, LogLevel> LevelsByName = CreateLevelsByName();

    /// <summary>Возвращает все имена уровней логирования в порядке объявления типа.</summary>
    /// <returns>Имена, пригодные для сообщения об отказе.</returns>
    internal static IReadOnlyList<string> All => Names;

    /// <summary>Проверяет, является ли текст точным именем уровня логирования.</summary>
    /// <param name="text">Текст значения <c>minimumLevel</c>.</param>
    /// <param name="level">Уровень, соответствующий имени.</param>
    /// <returns><see langword="true"/>, если имя совпадает с именем <see cref="LogLevel"/> точно.</returns>
    internal static bool TryParse(string text, out LogLevel level)
        => LevelsByName.TryGetValue(text, out level);

    private static FrozenDictionary<string, LogLevel> CreateLevelsByName()
    {
        Dictionary<string, LogLevel> levels = new(StringComparer.Ordinal);
        foreach (string name in Names)
        {
            levels.Add(name, Enum.Parse<LogLevel>(name));
        }

        return levels.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
