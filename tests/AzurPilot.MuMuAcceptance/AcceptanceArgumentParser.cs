using System.Globalization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;

namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Проверенные аргументы прогона приёмки.
/// </summary>
/// <remarks>
/// Exact instance обязателен: автоматический выбор для mutating acceptance не используется, потому что
/// при нескольких экземплярах он не определён, а при одном — выбирал бы «то, что нашлось», вместо явно
/// выбранного оператором экземпляра.
/// </remarks>
/// <param name="InstanceId">Явно выбранная identity экземпляра.</param>
/// <param name="Scenario">Выбранный сценарий прогона.</param>
/// <param name="Cycles">Явно заданное число повторений сценария; отсутствует у матрицы.</param>
internal sealed record AcceptanceOptions(MuMuInstanceId InstanceId, AcceptanceScenario Scenario, int? Cycles)
{
    /// <summary>Каноническая текстовая форма identity: <c>mumu:&lt;index&gt;</c>.</summary>
    internal string InstanceValue => InstanceId.ToString();

    /// <summary>Описывает режим прогона для отчёта.</summary>
    /// <returns>Строка вида <c>сценарий cold-start, повторений 3 (явно)</c> или <c>матрица</c>.</returns>
    internal string DescribeMode()
    {
        string scenario = "сценарий " + AcceptanceScenarioNames.Value(Scenario) + " — "
            + AcceptanceScenarioNames.Describe(Scenario);

        return Scenario == AcceptanceScenario.Matrix
            ? "матрица контракта (сценарий " + AcceptanceScenarioNames.Matrix + "), выполняется один раз"
            : scenario + "; повторений "
                + (Cycles ?? 0).ToString(CultureInfo.InvariantCulture) + " (задано явно)";
    }
}

/// <summary>
/// Результат разбора аргументов: либо проверенные аргументы, либо причина отказа.
/// </summary>
/// <remarks>
/// Отказ разбора — это отказ до начала работы: инструмент ничего не обнаруживает, не наблюдает и не
/// изменяет.
/// </remarks>
/// <param name="Options">Проверенные аргументы или <see langword="null"/>, если разбор отклонён.</param>
/// <param name="Error">Причина отказа разбора; пустая строка, если разбор успешен.</param>
internal sealed record AcceptanceArguments(AcceptanceOptions? Options, string Error)
{
    /// <summary>Признак того, что аргументы приняты.</summary>
    internal bool IsAccepted => Options is not null;

    /// <summary>Создаёт результат успешного разбора.</summary>
    /// <param name="options">Проверенные аргументы.</param>
    /// <returns>Результат разбора с аргументами.</returns>
    internal static AcceptanceArguments Accepted(AcceptanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AcceptanceArguments(options, string.Empty);
    }

    /// <summary>Создаёт результат отклонённого разбора.</summary>
    /// <param name="error">Причина отказа разбора.</param>
    /// <returns>Результат разбора без аргументов.</returns>
    internal static AcceptanceArguments Rejected(string error) => new(null, error);
}

/// <summary>
/// Разбирает аргументы инструмента приёмки.
/// </summary>
/// <remarks>
/// <para>
/// Обязательна ровно одна опция — <c>--instance mumu:&lt;index&gt;</c>. Дополнительно можно выбрать
/// сценарий (<c>--scenario</c>) и обязательно вместе с ним — число повторений (<c>--cycles</c>).
/// </para>
/// <para>
/// Разбор fail-closed: неизвестный аргумент, повторная опция, опция без значения, неизвестный сценарий,
/// неположительное или нечисловое число повторений, а также число повторений без сценария или у матрицы
/// отклоняются. Отказ разбора означает ненулевой код выхода до единой mutation.
/// </para>
/// <para>
/// Синтаксис значения instance не дублируется: он проверяется владельцем
/// <see cref="MuMuInstanceValue"/>, а <c>auto</c> отвергается явно как недопустимый для mutation.
/// </para>
/// </remarks>
internal static class AcceptanceArgumentParser
{
    /// <summary>Опция exact instance.</summary>
    internal const string InstanceOption = "--instance";

    /// <summary>Опция выбора сценария.</summary>
    internal const string ScenarioOption = "--scenario";

    /// <summary>Опция явного числа повторений сценария.</summary>
    internal const string CyclesOption = "--cycles";

    /// <summary>
    /// Верхняя граница числа повторений: защита от опечатки, из-за которой прогон занял бы часы.
    /// </summary>
    internal const int MaxCycles = 100;

    /// <summary>Подсказка по использованию инструмента.</summary>
    internal const string Usage =
        "Использование: AzurPilot.MuMuAcceptance " + InstanceOption + " mumu:<index> "
        + "[" + ScenarioOption + " " + AcceptanceScenarioNames.Matrix + "|"
        + AcceptanceScenarioNames.ColdStart + "|" + AcceptanceScenarioNames.StartAfterStop + "|"
        + AcceptanceScenarioNames.RestartFromRunning + "|" + AcceptanceScenarioNames.RestartFromStopped
        + " " + CyclesOption + " <n>] (например: " + InstanceOption + " mumu:1 " + ScenarioOption + " "
        + AcceptanceScenarioNames.RestartFromRunning + " " + CyclesOption + " 10).";

    /// <summary>Разбирает аргументы командной строки инструмента.</summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <returns>Проверенные аргументы либо причину отказа.</returns>
    internal static AcceptanceArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? requestedInstance = null;
        string? requestedScenario = null;
        string? requestedCycles = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            bool isInstance = string.Equals(argument, InstanceOption, StringComparison.Ordinal);
            bool isScenario = string.Equals(argument, ScenarioOption, StringComparison.Ordinal);
            bool isCycles = string.Equals(argument, CyclesOption, StringComparison.Ordinal);

            if (!isInstance && !isScenario && !isCycles)
            {
                return AcceptanceArguments.Rejected($"Неизвестный аргумент «{argument}».");
            }

            if ((isInstance && requestedInstance is not null)
                || (isScenario && requestedScenario is not null)
                || (isCycles && requestedCycles is not null))
            {
                return AcceptanceArguments.Rejected($"Опция {argument} задана более одного раза.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return AcceptanceArguments.Rejected(
                    isInstance
                        ? $"Опция {InstanceOption} требует значение mumu:<index>."
                        : $"Опция {argument} требует значение.");
            }

            index++;
            if (isInstance)
            {
                requestedInstance = args[index];
            }
            else if (isScenario)
            {
                requestedScenario = args[index];
            }
            else
            {
                requestedCycles = args[index];
            }
        }

        if (requestedInstance is null)
        {
            return AcceptanceArguments.Rejected(
                $"Не задан явно выбранный exact instance: требуется опция {InstanceOption} mumu:<index>. "
                + "Автоматический выбор для mutating acceptance не используется.");
        }

        AcceptanceArguments? scenarioError = ParseScenario(
            requestedScenario, requestedCycles, out AcceptanceScenario scenario, out int? cycles);
        if (scenarioError is not null)
        {
            return scenarioError;
        }

        if (MuMuInstanceValue.IsAuto(requestedInstance))
        {
            return AcceptanceArguments.Rejected(
                "Автоматический выбор «auto» для mutating acceptance запрещён: укажите exact instance "
                + "mumu:<index>.");
        }

        if (!MuMuInstanceValue.IsValid(requestedInstance))
        {
            return AcceptanceArguments.Rejected(
                $"Значение «{requestedInstance}» не соответствует синтаксису mumu.instance: ожидается "
                + "mumu:<index> без ведущих нулей.");
        }

        string providerIndex = requestedInstance[MuMuInstanceValue.ProviderPrefix.Length..];
        return AcceptanceArguments.Accepted(
            new AcceptanceOptions(MuMuInstanceId.FromIndex(providerIndex), scenario, cycles));
    }

    /// <summary>Разбирает опции сценария и явного числа повторений.</summary>
    /// <param name="requestedScenario">Значение опции сценария или <see langword="null"/>.</param>
    /// <param name="requestedCycles">Значение опции числа повторений или <see langword="null"/>.</param>
    /// <param name="scenario">Разобранный сценарий.</param>
    /// <param name="cycles">Разобранное число повторений; отсутствует у матрицы.</param>
    /// <returns>Отказ разбора или <see langword="null"/>, если опции корректны.</returns>
    private static AcceptanceArguments? ParseScenario(
        string? requestedScenario,
        string? requestedCycles,
        out AcceptanceScenario scenario,
        out int? cycles)
    {
        scenario = AcceptanceScenario.Matrix;
        cycles = null;

        if (requestedScenario is null)
        {
            if (requestedCycles is not null)
            {
                return AcceptanceArguments.Rejected(
                    $"Опция {CyclesOption} задана без опции {ScenarioOption}: число повторений относится "
                    + "к сценарию, а матрица выполняется один раз.");
            }

            return null;
        }

        if (!AcceptanceScenarioNames.TryParse(requestedScenario, out scenario))
        {
            return AcceptanceArguments.Rejected(
                $"Неизвестный сценарий «{requestedScenario}»: допустимы "
                + string.Join(", ", AcceptanceScenarioNames.All) + ".");
        }

        if (scenario == AcceptanceScenario.Matrix)
        {
            if (requestedCycles is not null)
            {
                return AcceptanceArguments.Rejected(
                    $"У сценария {AcceptanceScenarioNames.Matrix} число повторений не определено: он "
                    + "выполняется один раз, поэтому опция " + CyclesOption + " не принимается.");
            }

            return null;
        }

        if (requestedCycles is null)
        {
            return AcceptanceArguments.Rejected(
                $"Для сценария «{requestedScenario}» число повторений задаётся явно: требуется опция "
                + CyclesOption + " <n>.");
        }

        if (!int.TryParse(requestedCycles, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed < 1)
        {
            return AcceptanceArguments.Rejected(
                $"Значение «{requestedCycles}» не является числом повторений: ожидается целое число от 1.");
        }

        if (parsed > MaxCycles)
        {
            return AcceptanceArguments.Rejected(
                $"Число повторений {parsed} превышает допустимый предел " + MaxCycles
                + ": такое число означало бы прогон на часы.");
        }

        cycles = parsed;
        return null;
    }
}
