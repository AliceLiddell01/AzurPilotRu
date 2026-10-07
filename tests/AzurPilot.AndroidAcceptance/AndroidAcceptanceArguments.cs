using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Проверенные аргументы прогона приёмки Android.
/// </summary>
/// <remarks>
/// Exact instance обязателен: автоматический выбор для mutating acceptance не используется, потому что
/// при нескольких экземплярах он не определён, а при одном выбирал бы «то, что нашлось», вместо явно
/// выбранного оператором экземпляра. Тот же exact instance адресует и MuMu-предусловие, и exact ADB
/// endpoint, поэтому второй параметр адресации инструменту не нужен.
/// </remarks>
/// <param name="InstanceId">Явно выбранная identity экземпляра MuMu.</param>
internal sealed record AcceptanceOptions(MuMuInstanceId InstanceId)
{
    /// <summary>Каноническая текстовая форма identity: <c>mumu:&lt;index&gt;</c>.</summary>
    internal string InstanceValue => InstanceId.ToString();

    /// <summary>Описывает режим прогона для отчёта.</summary>
    /// <returns>Описание единственного режима инструмента.</returns>
    internal static string DescribeMode()
        => "матрица контракта: stop → start → restart с postcondition каждого перехода и восстановлением "
            + "начального состояния игры";
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
/// Разбирает аргументы инструмента приёмки Android.
/// </summary>
/// <remarks>
/// <para>
/// Обязательна ровно одна опция — <c>--instance mumu:&lt;index&gt;</c>: приёмка мутирует состояние игры
/// внутри конкретного экземпляра, поэтому «какой-нибудь экземпляр» здесь недопустим.
/// </para>
/// <para>
/// Разбор fail-closed: неизвестный аргумент, повторная опция и опция без значения отклоняются. Отказ
/// разбора означает ненулевой код выхода до единой mutation.
/// </para>
/// <para>
/// Синтаксис значения instance не дублируется: он проверяется владельцем <see cref="MuMuInstanceValue"/>,
/// а <c>auto</c> отвергается явно как недопустимый для mutation.
/// </para>
/// </remarks>
internal static class AndroidAcceptanceArgumentParser
{
    /// <summary>Опция exact instance.</summary>
    internal const string InstanceOption = "--instance";

    /// <summary>Подсказка по использованию инструмента.</summary>
    internal const string Usage =
        "Использование: AzurPilot.AndroidAcceptance " + InstanceOption + " mumu:<index> "
        + "(например: " + InstanceOption + " mumu:1).";

    /// <summary>Разбирает аргументы командной строки инструмента.</summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <returns>Проверенные аргументы либо причину отказа.</returns>
    internal static AcceptanceArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? requestedInstance = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (!string.Equals(argument, InstanceOption, StringComparison.Ordinal))
            {
                return AcceptanceArguments.Rejected($"Неизвестный аргумент «{argument}».");
            }

            if (requestedInstance is not null)
            {
                return AcceptanceArguments.Rejected($"Опция {argument} задана более одного раза.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return AcceptanceArguments.Rejected($"Опция {InstanceOption} требует значение mumu:<index>.");
            }

            index++;
            requestedInstance = args[index];
        }

        if (requestedInstance is null)
        {
            return AcceptanceArguments.Rejected(
                $"Не задан явно выбранный exact instance: требуется опция {InstanceOption} mumu:<index>. "
                + "Автоматический выбор для mutating acceptance не используется.");
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
        return AcceptanceArguments.Accepted(new AcceptanceOptions(MuMuInstanceId.FromIndex(providerIndex)));
    }
}
