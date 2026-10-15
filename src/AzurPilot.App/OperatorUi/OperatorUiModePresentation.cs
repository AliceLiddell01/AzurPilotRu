using AzurPilot.Core.Configuration;

namespace AzurPilot.App.OperatorUi;

/// <summary>Локализованное представление режимов интерфейса и подсказки перехода между ними.</summary>
/// <remarks>
/// Названия режимов и путь перехода живут в одном владельце: сообщение о недоступном действии и
/// заголовок экрана не должны расходиться формулировками.
/// </remarks>
internal static class OperatorUiModePresentation
{
    /// <summary>Человекочитаемое название режима.</summary>
    /// <param name="mode">Режим.</param>
    /// <returns>Название в предложном падеже для заголовка экрана.</returns>
    public static string Label(OperatorUiMode mode) => mode switch
    {
        OperatorUiMode.Normal => "обычный",
        OperatorUiMode.Manual => "ручной",
        OperatorUiMode.Diagnostic => "диагностический",
        _ => "обычный",
    };

    /// <summary>Название режима в винительном падеже для сообщения о недоступности.</summary>
    /// <param name="mode">Требуемый режим.</param>
    /// <returns>Название режима.</returns>
    public static string AccusativeLabel(OperatorUiMode mode) => mode switch
    {
        OperatorUiMode.Normal => "обычный",
        OperatorUiMode.Manual => "ручной",
        OperatorUiMode.Diagnostic => "диагностический",
        _ => "обычный",
    };

    /// <summary>Как перейти в требуемый режим.</summary>
    /// <param name="mode">Требуемый режим.</param>
    /// <returns>Точная инструкция перехода.</returns>
    public static string SwitchHint(OperatorUiMode mode) =>
        $"Запустите приложение с --mode {Argument(mode)} или задайте ui.mode: {Argument(mode)} в config.yaml.";

    /// <summary>Аргумент командной строки и значение конфигурации для режима.</summary>
    /// <param name="mode">Режим.</param>
    /// <returns>Строковое значение режима.</returns>
    public static string Argument(OperatorUiMode mode) => OperatorUiModeNames.ToWireName(mode);

    /// <summary>Разбирает значение режима из аргумента запуска или конфигурации.</summary>
    /// <param name="text">Строковое значение.</param>
    /// <param name="mode">Разобранный режим.</param>
    /// <returns>Значение допустимо.</returns>
    /// <remarks>
    /// Принимаются строго три значения в нижнем регистре: неявное прощение опечаток сделало бы
    /// неверный режим запуска неотличимым от явно заданного.
    /// </remarks>
    public static bool TryParse(string? text, out OperatorUiMode mode) => OperatorUiModeNames.TryParse(text, out mode);
}
