using System.Globalization;
using AzurPilot.Core.Capture;

namespace AzurPilot.App.Agent;

/// <summary>Разбор аргументов команд транспортов снимков; проверка совпадает с описанием в discovery.</summary>
internal static class AgentCaptureArguments
{
    internal const int MaximumGridValues = 16;

    internal static readonly AgentArgument Methods = new("--methods", "screenshot-methods", false,
        "Транспорты для измерения; по умолчанию all.");
    internal static readonly AgentArgument Method = new("--method", "screenshot-method", true, "Ровно один транспорт.");
    internal static readonly AgentArgument IntervalMethod = new("--method", "screenshot-method", false,
        "Транспорт измерения; по умолчанию текущий выбор приложения (явная настройка либо калибровка auto, иначе ADB).");
    internal static readonly AgentArgument DebugOverhead = new("--debug-overhead", "boolean", false,
        "Повторить серию победителя с Debug Mode и сообщить накладные расходы ON/OFF; по умолчанию false.");
    internal static readonly AgentArgument Profile = new("--profile", "benchmark-profile", false,
        "Объём серии; по умолчанию short.");
    internal static readonly AgentArgument Samples = new("--samples", "sample-count", false,
        "Явное число снимков серии вместо значения профиля.");
    internal static readonly AgentArgument Calibrate = new("--calibrate", "boolean", false,
        "Сохранить выбор auto для отпечатка target при доказанном победителе; по умолчанию true.");
    internal static readonly AgentArgument Scene = new("--scene", "capture-scene", true, "Сцена измерения интервалов.");
    internal static readonly AgentArgument Session = new("--session", "debug-session", true,
        "Идентификатор сессии интервалов, связывающий фазы normal и combat.");
    internal static readonly AgentArgument Confirm = new("--confirm", "confirmation-code", false,
        "Код, выданный фазой normal; обязателен для combat и запрещён для normal. Передаётся только после явного подтверждения пользователя, что бой открыт вручную.");
    internal static readonly AgentArgument Grid = new("--grid", "interval-grid", false,
        "Собственная сетка интервалов сцены, мс.");
    internal static readonly AgentArgument ApplySession = new("--session", "debug-session", true,
        "Завершённая сессия capture-intervals, из которой берётся профиль.");
    internal static readonly AgentArgument IntervalProfile = new("--profile", "interval-profile", true,
        "Профиль интервалов; current восстанавливает значения, действовавшие при измерении.");
    internal static readonly AgentArgument ApplyConfirm = new("--confirm", "confirmation-code", false,
        "Код из preview той же сессии и профиля. Без него выполняется только preview; передаётся только после явного согласия пользователя на запись.");

    internal static bool IsValidMethods(string value)
        => value == "all" || (value.Split(',') is { Length: > 0 and <= 9 } parts
            && parts.All(part => ScreenshotMethodNames.TryParse(part, out _))
            && parts.Distinct(StringComparer.Ordinal).Count() == parts.Length);

    internal static IReadOnlyList<ScreenshotMethod> ParseMethods(string? value)
    {
        if (value is null or "all")
        {
            return ScreenshotMethodNames.All;
        }

        return [.. value.Split(',').Select(part => ScreenshotMethodNames.TryParse(part, out ScreenshotMethod method)
            ? method : throw new ArgumentException("Неизвестный транспорт.", nameof(value)))];
    }

    internal static bool IsValidGrid(string value)
    {
        string[] parts = value.Split(',');
        return parts.Length is > 0 and <= MaximumGridValues && parts.All(part =>
            double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double parsed)
            && parsed is >= 1 and <= 1000);
    }

    internal static IReadOnlyList<double> ParseGrid(string value)
        => [.. value.Split(',').Select(part => double.Parse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))];
}
