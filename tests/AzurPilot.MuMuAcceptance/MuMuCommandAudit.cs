using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;

namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Аудит внешних процессов, запущенных за прогон приёмки.
/// </summary>
/// <remarks>
/// <para>
/// Приёмке запрещены удаление и создание экземпляров, изменение настроек эмулятора, очистка диска,
/// установка и удаление APK, ADB mutation, запуск игры, ввод, массовое закрытие экземпляров и завершение
/// чужих процессов. Вместо декларации этот запрет проверяется по факту: каждая граница запуска процесса
/// записывается, и после прогона проверяется, что запускалась только control surface обнаруженной
/// установки и только те её команды, которые входят в production-контракт lifecycle.
/// </para>
/// <para>
/// Любая другая программа (ADB, оболочка, средство завершения процессов) или другая подкоманда
/// означала бы нарушение запрета и делала бы приёмку недоказанной.
/// </para>
/// </remarks>
internal sealed class MuMuCommandAudit
{
    private readonly List<MuMuProcessRequest> _requests = [];

    /// <summary>Число записанных запусков процессов.</summary>
    internal int Count => _requests.Count;

    /// <summary>
    /// Считает запуски control surface с заданной операцией.
    /// </summary>
    /// <remarks>
    /// Счётчик нужен приёмке, чтобы фиксировать фактическое число отправленных команд <c>launch</c>:
    /// производственный контракт перехода <c>Stopped → Running</c> отправляет ровно один launch, а
    /// повтор добавляет ровно один. Поэтому «сколько launch отправлено» — это факт о поведении
    /// production-кода, а не догадка по времени.
    /// </remarks>
    /// <param name="operation">Операция control, например <c>launch</c>.</param>
    /// <returns>Число записанных запусков с этой операцией.</returns>
    internal int CountControlOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        int count = 0;
        foreach (MuMuProcessRequest request in _requests)
        {
            if (request.Arguments.Count < 2
                || !string.Equals(
                    request.Arguments[0],
                    MuMuManagerCommandBuilder.ControlSubcommand,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(request.Arguments[^1], operation, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Записывает запрос на запуск процесса.</summary>
    /// <param name="request">Описание запуска, полученное границей процесса.</param>
    internal void Record(MuMuProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);
    }

    /// <summary>Ищет нарушение запретов приёмки среди выполненных запусков.</summary>
    /// <param name="controlExecutablePath">Путь control surface обнаруженной установки.</param>
    /// <param name="requested">Exact instance, выбранный для приёмки.</param>
    /// <returns>Описание нарушения или <see langword="null"/>, если запреты не нарушены.</returns>
    internal string? FindViolation(string controlExecutablePath, MuMuInstanceId requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlExecutablePath);

        foreach (MuMuProcessRequest request in _requests)
        {
            if (!string.Equals(request.ExecutablePath, controlExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return "запускалась программа, отличная от control surface обнаруженной установки";
            }

            if (request.Arguments.Count == 0)
            {
                return "control surface запускалась без подкоманды";
            }

            string subcommand = request.Arguments[0];
            if (string.Equals(subcommand, MuMuManagerCommandBuilder.VersionSubcommand, StringComparison.Ordinal))
            {
                if (request.Arguments.Count != 1)
                {
                    return "подкоманда version вызвана с посторонними аргументами";
                }

                continue;
            }

            if (string.Equals(subcommand, MuMuManagerCommandBuilder.InfoSubcommand, StringComparison.Ordinal))
            {
                string? target = ReadTarget(request.Arguments);
                if (target is null)
                {
                    return "info вызвана без адресации --vmindex";
                }

                if (!IsRequestedOrAll(target, requested))
                {
                    return "info запрошена не для запрошенного экземпляра";
                }

                continue;
            }

            if (string.Equals(subcommand, MuMuManagerCommandBuilder.ControlSubcommand, StringComparison.Ordinal))
            {
                string? target = ReadTarget(request.Arguments);
                if (!string.Equals(target, requested.Index, StringComparison.Ordinal))
                {
                    return "control адресована не запрошенному экземпляру";
                }

                if (!IsAllowedControlOperation(request.Arguments[^1]))
                {
                    return "control использовала операцию вне production-пути lifecycle (допустимы launch и shutdown)";
                }

                continue;
            }

            return "использована подкоманда вне production-контракта control surface";
        }

        return null;
    }

    /// <summary>Описывает bounded сводку выполненных запусков для отчёта.</summary>
    /// <returns>Строка вида <c>запусков: 7; подкоманды: control, info, version; control: launch, shutdown</c>.</returns>
    internal string Describe()
    {
        List<string> subcommands = [];
        List<string> operations = [];
        foreach (MuMuProcessRequest request in _requests)
        {
            if (request.Arguments.Count == 0)
            {
                continue;
            }

            string subcommand = request.Arguments[0];
            if (!subcommands.Contains(subcommand, StringComparer.Ordinal))
            {
                subcommands.Add(subcommand);
            }

            if (string.Equals(subcommand, MuMuManagerCommandBuilder.ControlSubcommand, StringComparison.Ordinal)
                && request.Arguments.Count > 1)
            {
                string operation = request.Arguments[^1];
                if (!operations.Contains(operation, StringComparer.Ordinal))
                {
                    operations.Add(operation);
                }
            }
        }

        subcommands.Sort(StringComparer.Ordinal);
        operations.Sort(StringComparer.Ordinal);

        return "запусков control surface: " + Count
            + "; подкоманды: " + string.Join(", ", subcommands)
            + "; control-операции: " + (operations.Count == 0 ? "нет" : string.Join(", ", operations));
    }

    /// <summary>Читает значение аргумента адресации <c>--vmindex</c>.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <returns>Значение адресации или <see langword="null"/>, если адресация не найдена.</returns>
    private static string? ReadTarget(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], MuMuManagerCommandBuilder.VmIndexArgument, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    /// <summary>Проверяет, адресована ли команда запрошенному экземпляру или всему перечислению.</summary>
    /// <param name="target">Значение аргумента адресации.</param>
    /// <param name="requested">Exact instance приёмки.</param>
    /// <returns><see langword="true"/>, если адресация допустима.</returns>
    private static bool IsRequestedOrAll(string target, MuMuInstanceId requested)
        => string.Equals(target, requested.Index, StringComparison.Ordinal)
            || string.Equals(target, MuMuManagerCommandBuilder.AllInstancesArgumentValue, StringComparison.Ordinal);

    /// <summary>Проверяет, входит ли операция control в production-путь lifecycle.</summary>
    /// <param name="operation">Значение последнего аргумента команды control.</param>
    /// <returns><see langword="true"/>, если операция допустима.</returns>
    private static bool IsAllowedControlOperation(string operation)
        => string.Equals(operation, MuMuManagerCommandBuilder.LaunchOperation, StringComparison.Ordinal)
            || string.Equals(operation, MuMuManagerCommandBuilder.ShutdownOperation, StringComparison.Ordinal);
}

/// <summary>
/// Граница запуска процесса, которая записывает каждый запуск и передаёт его production-реализации.
/// </summary>
/// <remarks>
/// Запись выполняется до делегирования, поэтому в аудит попадает и запуск, завершившийся отказом:
/// отказ не является основанием считать команду невыполненной.
/// </remarks>
internal sealed class AuditingProcessRunner : IMuMuProcessRunner
{
    private readonly IMuMuProcessRunner _inner;
    private readonly MuMuCommandAudit _audit;

    /// <summary>Создаёт записывающую границу поверх production-реализации.</summary>
    /// <param name="inner">Production-граница запуска процесса.</param>
    /// <param name="audit">Аудит, в который попадают запуски.</param>
    internal AuditingProcessRunner(IMuMuProcessRunner inner, MuMuCommandAudit audit)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audit);

        _inner = inner;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<ApplicationResult<MuMuProcessOutcome>> RunAsync(
        MuMuProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _audit.Record(request);
        return await _inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
