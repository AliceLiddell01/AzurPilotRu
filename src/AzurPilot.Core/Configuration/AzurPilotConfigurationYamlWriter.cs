using System.Globalization;
using System.Text;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Владелец канонической записи конфигурации схемы v3 в человекочитаемый YAML.
/// </summary>
/// <remarks>
/// <para>
/// Запись — единственный путь, которым приложение создаёт <c>config.yaml</c>, и выполняется она только
/// явной миграцией (<see cref="AzurPilotConfigurationMigration"/>) или сохранением
/// (<see cref="AzurPilotConfigurationStore"/>): загрузка конфигурации ничего не пишет. Форма документа
/// фиксирована: те же ключи и тот же порядок секций, что и у эффективной схемы, поэтому файл читается
/// глазами и не зависит от настроек сериализатора.
/// </para>
/// <para>
/// Строковые значения, которые нельзя записать plain-скаляром YAML без изменения смысла (например
/// <c>mumu:1</c>), записываются в двойных кавычках. Совместимость записи и чтения не декларируется, а
/// доказывается: миграция и сохранение повторно читают записанный файл загрузчиком и сверяют результат
/// с ожидаемым snapshot.
/// </para>
/// <para>
/// Документ завершается переводом строки и записывается в UTF-8 без BOM: канонический файл остаётся
/// текстовым и переносимым, а ведущий BOM не добавляется, потому что не несёт смысла для YAML.
/// </para>
/// </remarks>
internal static class AzurPilotConfigurationYamlWriter
{
    private const char NewLine = '\n';

    /// <summary>Создаёт канонический YAML-документ эффективной схемы.</summary>
    /// <param name="configuration">Провалидированная конфигурация схемы v3.</param>
    /// <returns>Текст канонического YAML-документа.</returns>
    internal static string Write(AzurPilotConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        StringBuilder document = new();
        _ = document.Append("schemaVersion: ")
            .Append(configuration.SchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Append(NewLine);
        _ = document.Append("ui:").Append(NewLine);
        _ = document.Append("  mode: ")
            .Append(Scalar(OperatorUiModeNames.ToWireName(configuration.Ui.Mode)))
            .Append(NewLine);
        _ = document.Append("mumu:").Append(NewLine);
        _ = document.Append("  instance: ").Append(Scalar(configuration.MuMu.Instance)).Append(NewLine);
        _ = document.Append("diagnostics:").Append(NewLine);
        _ = document.Append("  minimumLevel: ")
            .Append(Scalar(configuration.Diagnostics.MinimumLevel.ToString()))
            .Append(NewLine);
        _ = document.Append("logging:").Append(NewLine);
        _ = document.Append("  viewer:").Append(NewLine);
        _ = document.Append("    enabled: ")
            .Append(configuration.Logging.Viewer.Enabled ? "true" : "false")
            .Append(NewLine);

        return document.ToString();
    }

    /// <summary>Записывает строковое значение схемы: plain-скаляром либо в двойных кавычках.</summary>
    /// <param name="value">Значение ключа.</param>
    /// <returns>Фрагмент YAML для значения.</returns>
    private static string Scalar(string value)
        => IsSafePlainScalar(value) ? value : Quote(value);

    /// <summary>Проверяет, читается ли значение как та же строка в plain-форме YAML.</summary>
    /// <param name="value">Значение ключа.</param>
    /// <returns><see langword="true"/>, если значение можно записать без кавычек.</returns>
    /// <remarks>
    /// Plain-форма сохраняет смысл только для простого буквенно-цифрового токена: всё остальное
    /// (<c>mumu:1</c>, пустая строка, булевы и null-литералы) записывается в кавычках, иначе YAML
    /// прочитал бы другое значение или другой тип.
    /// </remarks>
    private static bool IsSafePlainScalar(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return !value.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("null", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("on", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Записывает значение в двойных кавычках с экранированием.</summary>
    /// <param name="value">Значение ключа.</param>
    /// <returns>Значение в двойных кавычках.</returns>
    private static string Quote(string value)
        => string.Concat(
            "\"",
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal),
            "\"");
}
