namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Сценарий прогона приёмки: либо полная матрица контракта, либо один проверяемый переход.
/// </summary>
/// <remarks>
/// <para>
/// Матрица — исходный режим инструмента: она проверяет весь контракт целиком и заканчивается тем же
/// состоянием, с которого началась. Сценарии нужны, чтобы проверять отдельно каждый путь перехода
/// <c>Stopped → Running</c>: провайдерская гонка относится к launch, а не только к перезапуску, поэтому
/// «случайно проверенный» путь не доказывает остальные.
/// </para>
/// <para>
/// Число повторений у матрицы не определено: она выполняется один раз. У сценария число повторений
/// задаётся явно и обязательно, чтобы «сколько раз проверяли» было фактом командной строки, а не
/// умолчанием инструмента.
/// </para>
/// </remarks>
internal enum AcceptanceScenario
{
    /// <summary>Полная матрица контракта приёмки (режим по умолчанию).</summary>
    Matrix,

    /// <summary>Холодный запуск: экземпляр уже остановлен до прогона, прогон только запускает его.</summary>
    ColdStart,

    /// <summary>Запуск сразу после недавней остановки — тот самый гоночный случай.</summary>
    StartAfterStop,

    /// <summary>Перезапуск экземпляра, который наблюдается запущенным.</summary>
    RestartFromRunning,

    /// <summary>Перезапуск экземпляра, который наблюдается остановленным.</summary>
    RestartFromStopped,
}

/// <summary>
/// Текстовые имена сценариев: единственный владелец значений опции <c>--scenario</c>.
/// </summary>
/// <remarks>
/// Разбор fail-closed: неизвестное значение отклоняется, а не подменяется ближайшим известным. Значения
/// сравниваются по ordinal, чтобы регистр и культура не влияли на выбор сценария.
/// </remarks>
internal static class AcceptanceScenarioNames
{
    /// <summary>Значение сценария полной матрицы.</summary>
    internal const string Matrix = "matrix";

    /// <summary>Значение холодного запуска.</summary>
    internal const string ColdStart = "cold-start";

    /// <summary>Значение запуска сразу после остановки.</summary>
    internal const string StartAfterStop = "start-after-stop";

    /// <summary>Значение перезапуска запущенного экземпляра.</summary>
    internal const string RestartFromRunning = "restart-from-running";

    /// <summary>Значение перезапуска остановленного экземпляра.</summary>
    internal const string RestartFromStopped = "restart-from-stopped";

    /// <summary>Список допустимых значений опции <c>--scenario</c> для подсказки.</summary>
    internal static readonly string[] All =
    [
        Matrix,
        ColdStart,
        StartAfterStop,
        RestartFromRunning,
        RestartFromStopped,
    ];

    /// <summary>Разбирает значение опции <c>--scenario</c>.</summary>
    /// <param name="value">Значение из командной строки.</param>
    /// <param name="scenario">Разобранный сценарий.</param>
    /// <returns><see langword="true"/>, если значение известно.</returns>
    internal static bool TryParse(string value, out AcceptanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(value, Matrix, StringComparison.Ordinal))
        {
            scenario = AcceptanceScenario.Matrix;
            return true;
        }

        if (string.Equals(value, ColdStart, StringComparison.Ordinal))
        {
            scenario = AcceptanceScenario.ColdStart;
            return true;
        }

        if (string.Equals(value, StartAfterStop, StringComparison.Ordinal))
        {
            scenario = AcceptanceScenario.StartAfterStop;
            return true;
        }

        if (string.Equals(value, RestartFromRunning, StringComparison.Ordinal))
        {
            scenario = AcceptanceScenario.RestartFromRunning;
            return true;
        }

        if (string.Equals(value, RestartFromStopped, StringComparison.Ordinal))
        {
            scenario = AcceptanceScenario.RestartFromStopped;
            return true;
        }

        scenario = AcceptanceScenario.Matrix;
        return false;
    }

    /// <summary>Возвращает значение опции <c>--scenario</c> для сценария.</summary>
    /// <param name="scenario">Сценарий.</param>
    /// <returns>Текстовое значение сценария.</returns>
    internal static string Value(AcceptanceScenario scenario) => scenario switch
    {
        AcceptanceScenario.Matrix => Matrix,
        AcceptanceScenario.ColdStart => ColdStart,
        AcceptanceScenario.StartAfterStop => StartAfterStop,
        AcceptanceScenario.RestartFromRunning => RestartFromRunning,
        AcceptanceScenario.RestartFromStopped => RestartFromStopped,
        _ => Matrix,
    };

    /// <summary>Описывает сценарий для отчёта.</summary>
    /// <param name="scenario">Сценарий.</param>
    /// <returns>Описание сценария на русском языке.</returns>
    internal static string Describe(AcceptanceScenario scenario) => scenario switch
    {
        AcceptanceScenario.Matrix => "полная матрица контракта",
        AcceptanceScenario.ColdStart => "холодный запуск из доказанного Stopped (остановка выполнена до прогона)",
        AcceptanceScenario.StartAfterStop => "запуск сразу после недавней остановки",
        AcceptanceScenario.RestartFromRunning => "перезапуск запущенного экземпляра",
        AcceptanceScenario.RestartFromStopped => "перезапуск остановленного экземпляра",
        _ => "неизвестный сценарий",
    };
}
