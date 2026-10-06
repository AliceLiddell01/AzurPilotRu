namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Коды выхода инструмента приёмки.
/// </summary>
/// <remarks>
/// Код выхода описывает исход прогона приёмки, а не исход lifecycle-операции: 0 выдаётся только когда
/// доказана вся матрица и восстановлено начальное состояние. Коды отказа приложения
/// (<c>AzurPilot.App.AzurPilotExitCode</c>) здесь не дублируются: инструмент не является приложением и
/// не проецирует свои отказы в его каталог.
/// </remarks>
internal static class AcceptanceExitCode
{
    /// <summary>Матрица и восстановление начального состояния доказаны.</summary>
    internal const int Success = 0;

    /// <summary>Приёмка не доказана: шаг матрицы, postcondition или восстановление состояния.</summary>
    internal const int NotProven = 1;

    /// <summary>Прогон отклонён до начала работы: не задан exact instance или неизвестный аргумент.</summary>
    internal const int UsageRejected = 2;
}
