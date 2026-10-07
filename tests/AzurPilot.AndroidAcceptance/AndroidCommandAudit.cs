using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Аудит внешних процессов, запущенных за прогон приёмки Android.
/// </summary>
/// <remarks>
/// <para>
/// Приёмке запрещены завершение сервера ADB (<c>kill-server</c>), установка и удаление APK, очистка
/// data/cache, сброс разрешений, изменение настроек эмулятора, создание и удаление экземпляров MuMu,
/// ввод, screenshot как критерий lifecycle, операции над другим пакетом и управление другим ADB target.
/// Вместо декларации запрет проверяется по факту: каждая граница запуска процесса записывается
/// (executable, полный список аргументов и признак усечения захваченного вывода), а после прогона
/// проверяется, что запускались только bundled ADB и control surface обнаруженной установки и только те
/// команды, которые входят в разрешённый набор.
/// </para>
/// <para>
/// Разрешённый набор ADB не переписывается здесь заново: формы аргументов строит их владелец
/// <see cref="AdbCommandBuilder"/>, а аудит сравнивает записанный запуск с этими формами. Так правило
/// формы остаётся у одного владельца, а аудит проверяет факт прогона.
/// </para>
/// <para>
/// Mutation MuMu в этом прогоне запрещена полностью: у control surface разрешены только чтения
/// <c>version</c> и <c>info</c>, поэтому подкоманда <c>control</c> считается нарушением, а не нормой.
/// </para>
/// </remarks>
internal sealed class AndroidCommandAudit
{
    /// <summary>Аргументы запрещённых действий приёмки: точное совпадение токена.</summary>
    private static readonly string[] ForbiddenTokens =
    [
        "kill-server",
        "install",
        "uninstall",
        "clear",
        "revoke",
        "grant",
        "input",
        "tap",
        "swipe",
        "text",
        "keyevent",
        "screencap",
        "screenshot",
        "push",
        "pull",
        "settings",
        "wm",
        "reboot",
        "root",
        "unroot",
        "tcpip",
        "forward",
        "reverse",
        "monkey",
        "create",
        "delete",
        "clone",
        "rename",
        "import",
        "export",
        "upgrade",
    ];

    /// <summary>Аргументы запрещённых действий приёмки: поиск по подстроке.</summary>
    private static readonly string[] ForbiddenSubstrings =
    [
        "kill-server",
        "screencap",
        "screenshot",
        "uninstall",
        "monkey",
        "pm clear",
        "clear data",
        "clear cache",
        "input tap",
        "input swipe",
        "input text",
        "input keyevent",
        "settings put",
        "wm size",
        "wm density",
    ];

    private readonly List<AuditEntry> _entries = [];

    /// <summary>Число записанных запусков процессов.</summary>
    internal int Count => _entries.Count;

    /// <summary>Записывает запрос на запуск процесса до его выполнения.</summary>
    /// <remarks>
    /// Запись выполняется до делегирования, поэтому в аудит попадает и запуск, завершившийся отказом:
    /// отказ не является основанием считать команду невыполненной.
    /// </remarks>
    /// <param name="request">Описание запуска, полученное границей процесса.</param>
    /// <returns>Запись аудита, которую граница дополняет исходом запуска.</returns>
    internal AuditEntry Begin(WindowsProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AuditEntry entry = new(request);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Дополняет запись аудита исходом запуска, включая признак усечения вывода.</summary>
    /// <remarks>
    /// Запись обязана принадлежать этому аудиту: дополнение чужой записи означало бы, что исход одного
    /// запуска приписан другому, поэтому это ошибка программирования, а не ожидаемый отказ.
    /// </remarks>
    /// <param name="entry">Запись, полученная от <see cref="Begin"/>.</param>
    /// <param name="outcome">Результат запуска или <see langword="null"/>, если запуск отказал.</param>
    /// <exception cref="InvalidOperationException">Запись не принадлежит этому аудиту.</exception>
    internal void Complete(AuditEntry entry, WindowsProcessOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_entries.Contains(entry))
        {
            throw new InvalidOperationException(
                "Запись аудита не принадлежит этому прогону: исход запуска не может быть приписан ей.");
        }

        if (outcome is null)
        {
            return;
        }

        entry.StandardOutputTruncated = outcome.StandardOutputTruncated;
        entry.StandardErrorTruncated = outcome.StandardErrorTruncated;
    }

    /// <summary>Ищет нарушение запретов приёмки среди выполненных запусков.</summary>
    /// <param name="controlExecutablePath">Путь control surface обнаруженной установки.</param>
    /// <param name="adbExecutablePath">Путь bundled ADB обнаруженной установки.</param>
    /// <param name="requestedInstance">Exact instance, выбранный для приёмки.</param>
    /// <param name="endpoint">Разрешённый exact ADB endpoint либо <see langword="null"/>, если он не разрешён.</param>
    /// <param name="package">Идентификатор пакета игры, которым адресуется mutation.</param>
    /// <returns>Описание нарушения или <see langword="null"/>, если запреты не нарушены.</returns>
    internal string? FindViolation(
        string controlExecutablePath,
        string adbExecutablePath,
        MuMuInstanceId requestedInstance,
        AndroidEndpoint? endpoint,
        AndroidPackageId package)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutablePath);

        foreach (AuditEntry entry in _entries)
        {
            string? forbidden = FindForbiddenArgument(entry.Request.Arguments);
            if (forbidden is not null)
            {
                return forbidden;
            }

            if (string.Equals(
                entry.Request.ExecutablePath, controlExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                string? controlViolation = ValidateControlSurface(entry.Request.Arguments, requestedInstance);
                if (controlViolation is not null)
                {
                    return controlViolation;
                }

                continue;
            }

            if (string.Equals(
                entry.Request.ExecutablePath, adbExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                string? adbViolation = ValidateAdb(entry.Request.Arguments, endpoint, package);
                if (adbViolation is not null)
                {
                    return adbViolation;
                }

                continue;
            }

            return "запускалась программа, отличная от bundled ADB и control surface обнаруженной установки";
        }

        return null;
    }

    /// <summary>Описывает bounded сводку выполненных запусков для отчёта.</summary>
    /// <remarks>
    /// <para>
    /// Сводка не содержит machine-specific путей: executable описывается ролью (bundled ADB или control
    /// surface), а число запусков — фактом прогона. Формы команд описываются отдельно
    /// <see cref="DescribeForms"/>: так ни счётчики, ни формы не теряются из-за ограничения длины.
    /// </para>
    /// <para>
    /// Роль запуска определяется исполняемым файлом, а не формой аргументов: подкоманда <c>version</c>
    /// существует и у bundled ADB, и у control surface, поэтому форма аргументов роль не различает.
    /// </para>
    /// </remarks>
    /// <param name="adbExecutablePath">
    /// Путь bundled ADB обнаруженной установки либо <see langword="null"/>, если он не обнаружен: до его
    /// обнаружения команд ADB не запускалось.
    /// </param>
    /// <returns>Строка с числом запусков, mutation игры и усечённых выводов.</returns>
    internal string Describe(string? adbExecutablePath)
    {
        int adbRuns = 0;
        int controlRuns = 0;
        int truncated = 0;
        int mutations = 0;

        foreach (AuditEntry entry in _entries)
        {
            if (entry.StandardOutputTruncated || entry.StandardErrorTruncated)
            {
                truncated++;
            }

            if (IsAdbRun(entry, adbExecutablePath))
            {
                adbRuns++;
                if (IsGameMutationForm(entry.Request.Arguments))
                {
                    mutations++;
                }

                continue;
            }

            controlRuns++;
        }

        return "запусков процессов: " + Count
            + "; bundled ADB: " + adbRuns
            + "; control surface: " + controlRuns
            + "; mutation игры: " + mutations
            + "; усечённых выводов: " + truncated;
    }

    /// <summary>Описывает bounded формы выполненных команд для отчёта.</summary>
    /// <remarks>
    /// Формы перечисляются с числом запусков: по счётчикам видно, сколько именно mutation игры было
    /// выполнено, и что других подкоманд, кроме перечисленных, не запускалось.
    /// </remarks>
    /// <param name="adbExecutablePath">
    /// Путь bundled ADB обнаруженной установки либо <see langword="null"/>, если он не обнаружен.
    /// </param>
    /// <returns>Строка с формами команд ADB и подкомандами control surface и их числом.</returns>
    internal string DescribeForms(string? adbExecutablePath)
    {
        SortedDictionary<string, int> adbForms = new(StringComparer.Ordinal);
        SortedDictionary<string, int> controlForms = new(StringComparer.Ordinal);

        foreach (AuditEntry entry in _entries)
        {
            SortedDictionary<string, int> forms = IsAdbRun(entry, adbExecutablePath)
                ? adbForms
                : controlForms;

            string form = DescribeForm(entry.Request.Arguments);
            forms[form] = forms.TryGetValue(form, out int runs) ? runs + 1 : 1;
        }

        return "bundled ADB: " + Join(adbForms) + "; control surface: " + Join(controlForms);
    }

    /// <summary>Определяет, относится ли записанный запуск к bundled ADB.</summary>
    /// <param name="entry">Запись аудита.</param>
    /// <param name="adbExecutablePath">Путь bundled ADB либо <see langword="null"/>, если он не обнаружен.</param>
    /// <returns><see langword="true"/>, если запускался обнаруженный bundled ADB.</returns>
    private static bool IsAdbRun(AuditEntry entry, string? adbExecutablePath)
        => adbExecutablePath is not null
            && string.Equals(entry.Request.ExecutablePath, adbExecutablePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ищет запрещённый аргумент в списке аргументов процесса.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <returns>Описание нарушения или <see langword="null"/>, если запрещённых аргументов нет.</returns>
    private static string? FindForbiddenArgument(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
        {
            foreach (string forbidden in ForbiddenTokens)
            {
                if (string.Equals(argument, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return "использован аргумент запрещённого приёмке действия: " + forbidden;
                }
            }
        }

        string joined = string.Join(' ', arguments);
        foreach (string forbidden in ForbiddenSubstrings)
        {
            if (joined.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return "использована команда запрещённого приёмке действия: " + forbidden;
            }
        }

        return null;
    }

    /// <summary>Проверяет запуск control surface на соответствие разрешённому набору чтений.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <param name="requestedInstance">Exact instance, выбранный для приёмки.</param>
    /// <returns>Описание нарушения или <see langword="null"/>, если запуск допустим.</returns>
    private static string? ValidateControlSurface(IReadOnlyList<string> arguments, MuMuInstanceId requestedInstance)
    {
        if (arguments.Count == 0)
        {
            return "control surface запускалась без подкоманды";
        }

        if (string.Equals(arguments[0], MuMuManagerCommandBuilder.VersionSubcommand, StringComparison.Ordinal))
        {
            return arguments.Count == 1
                ? null
                : "подкоманда version вызвана с посторонними аргументами";
        }

        if (!string.Equals(arguments[0], MuMuManagerCommandBuilder.InfoSubcommand, StringComparison.Ordinal))
        {
            return "control surface использована для операции вне разрешённого набора (разрешены только "
                + MuMuManagerCommandBuilder.VersionSubcommand + " и " + MuMuManagerCommandBuilder.InfoSubcommand
                + "): mutation MuMu в этой приёмке запрещена";
        }

        if (arguments.Count != 3
            || !string.Equals(arguments[1], MuMuManagerCommandBuilder.VmIndexArgument, StringComparison.Ordinal))
        {
            return "info вызвана не в форме точной адресации --vmindex";
        }

        string target = arguments[2];
        bool addressed = string.Equals(target, requestedInstance.Index, StringComparison.Ordinal)
            || string.Equals(
                target, MuMuManagerCommandBuilder.AllInstancesArgumentValue, StringComparison.Ordinal);

        return addressed ? null : "info запрошена не для запрошенного экземпляра";
    }

    /// <summary>Проверяет запуск bundled ADB на соответствие production-формам адресации target-а.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <param name="endpoint">Разрешённый exact ADB endpoint либо <see langword="null"/>.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <returns>Описание нарушения или <see langword="null"/>, если запуск допустим.</returns>
    private static string? ValidateAdb(
        IReadOnlyList<string> arguments,
        AndroidEndpoint? endpoint,
        AndroidPackageId package)
    {
        if (arguments.Count == 0)
        {
            return "bundled ADB запускался без аргументов";
        }

        if (Matches(arguments, AdbCommandBuilder.BuildVersionArguments()))
        {
            return null;
        }

        if (endpoint is not AndroidEndpoint target)
        {
            return "команда ADB адресована target-у, который не был разрешён для запрошенного экземпляра";
        }

        if (Matches(arguments, AdbCommandBuilder.BuildConnectArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildGetStateArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildBootCompletedArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildAndroidReleaseArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildSdkLevelArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildPackagePathArguments(target, package))
            || Matches(arguments, AdbCommandBuilder.BuildLauncherQueryArguments(target, package))
            || Matches(arguments, AdbCommandBuilder.BuildProcessListArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildWindowDumpArguments(target))
            || Matches(arguments, AdbCommandBuilder.BuildForceStopGameArguments(target, package))
            || IsStartGameForm(arguments, target, package))
        {
            return null;
        }

        return "команда ADB не соответствует ни одной форме production-контракта адресации точного target-а";
    }

    /// <summary>Проверяет форму запуска разрешённого launcher-компонента пакета игры.</summary>
    /// <remarks>
    /// Компонент разрешается в runtime, поэтому его значение сравнивается не с константой, а с пакетом
    /// игры: mutation запуска обязана адресовать только его.
    /// </remarks>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <param name="endpoint">Разрешённый exact ADB endpoint.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <returns><see langword="true"/>, если форма соответствует запуску компонента этого пакета.</returns>
    private static bool IsStartGameForm(
        IReadOnlyList<string> arguments,
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        if (arguments.Count != 7)
        {
            return false;
        }

        return string.Equals(arguments[0], AdbCommandBuilder.TargetArgument, StringComparison.Ordinal)
            && string.Equals(arguments[1], endpoint.ToString(), StringComparison.Ordinal)
            && string.Equals(arguments[2], AdbCommandBuilder.ShellSubcommand, StringComparison.Ordinal)
            && string.Equals(arguments[3], AdbCommandBuilder.ActivityManagerSubcommand, StringComparison.Ordinal)
            && string.Equals(arguments[4], AdbCommandBuilder.StartOperation, StringComparison.Ordinal)
            && string.Equals(arguments[5], AdbCommandBuilder.ComponentArgument, StringComparison.Ordinal)
            && arguments[6].StartsWith(package.ToString() + "/", StringComparison.Ordinal);
    }

    /// <summary>Сравнивает аргументы запуска с ожидаемой формой владельца формы аргументов.</summary>
    /// <param name="arguments">Записанные аргументы процесса.</param>
    /// <param name="expected">Ожидаемая форма аргументов.</param>
    /// <returns><see langword="true"/>, если формы совпадают поэлементно.</returns>
    private static bool Matches(IReadOnlyList<string> arguments, IReadOnlyList<string> expected)
    {
        if (arguments.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(arguments[index], expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Определяет, является ли запуск mutation игры.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <returns><see langword="true"/>, если запуск адресован запуску или остановке игры.</returns>
    private static bool IsGameMutationForm(IReadOnlyList<string> arguments)
        => arguments.Count >= 5
            && string.Equals(arguments[3], AdbCommandBuilder.ActivityManagerSubcommand, StringComparison.Ordinal)
            && (string.Equals(arguments[4], AdbCommandBuilder.StartOperation, StringComparison.Ordinal)
                || string.Equals(arguments[4], AdbCommandBuilder.ForceStopOperation, StringComparison.Ordinal));

    /// <summary>Описывает форму команды без machine-specific значений.</summary>
    /// <param name="arguments">Аргументы процесса.</param>
    /// <returns>Короткое имя формы команды.</returns>
    private static string DescribeForm(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return "без аргументов";
        }

        if (string.Equals(arguments[0], AdbCommandBuilder.VersionSubcommand, StringComparison.Ordinal))
        {
            return AdbCommandBuilder.VersionSubcommand;
        }

        if (string.Equals(arguments[0], AdbCommandBuilder.ConnectSubcommand, StringComparison.Ordinal))
        {
            return AdbCommandBuilder.ConnectSubcommand;
        }

        if (string.Equals(arguments[0], MuMuManagerCommandBuilder.VersionSubcommand, StringComparison.Ordinal))
        {
            return MuMuManagerCommandBuilder.VersionSubcommand;
        }

        if (string.Equals(arguments[0], MuMuManagerCommandBuilder.InfoSubcommand, StringComparison.Ordinal))
        {
            return MuMuManagerCommandBuilder.InfoSubcommand;
        }

        if (arguments.Count >= 4
            && string.Equals(arguments[0], AdbCommandBuilder.TargetArgument, StringComparison.Ordinal)
            && string.Equals(arguments[2], AdbCommandBuilder.ShellSubcommand, StringComparison.Ordinal))
        {
            // Свойство внутри shell описывается формой команды, а не его значением: перечень прочитанных
            // свойств не является частью доказательства отсутствия запрещённых действий.
            return string.Equals(arguments[3], AdbCommandBuilder.GetPropertySubcommand, StringComparison.Ordinal)
                ? "shell " + AdbCommandBuilder.GetPropertySubcommand
                : "shell " + string.Join(' ', arguments.Skip(3).Take(2));
        }

        return arguments.Count >= 3
            && string.Equals(arguments[2], AdbCommandBuilder.GetStateSubcommand, StringComparison.Ordinal)
                ? AdbCommandBuilder.GetStateSubcommand
                : "неизвестная форма";
    }

    private static string Join(SortedDictionary<string, int> forms)
        => forms.Count == 0
            ? "нет"
            : string.Join(", ", forms.Select(pair => pair.Key + "×" + pair.Value.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
/// Одна запись аудита: запуск процесса, его аргументы и исход запуска.
/// </summary>
/// <remarks>
/// Запись хранит ровно то, что доказывает отсутствие запрещённых действий: executable, полный список
/// аргументов и признак усечения захваченного вывода. Путь executable не печатается в отчёте — он
/// сравнивается в памяти и не переносится в evidence.
/// </remarks>
internal sealed class AuditEntry
{
    /// <summary>Создаёт запись аудита для запроса на запуск процесса.</summary>
    /// <param name="request">Описание запуска, полученное границей процесса.</param>
    internal AuditEntry(WindowsProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    /// <summary>Описание запуска: executable и полный список аргументов.</summary>
    internal WindowsProcessRequest Request { get; }

    /// <summary>Признак усечения захваченного stdout процесса.</summary>
    internal bool StandardOutputTruncated { get; set; }

    /// <summary>Признак усечения захваченного stderr процесса.</summary>
    internal bool StandardErrorTruncated { get; set; }
}

/// <summary>
/// Граница запуска процесса, которая записывает каждый запуск и передаёт его production-реализации.
/// </summary>
/// <remarks>
/// <para>
/// Обёртка строится поверх общей границы <see cref="IWindowsProcessRunner"/> тем же приёмом, что аудит
/// MuMu-приёмки: собственная реализация запуска процесса не заводится, а второй process runner в
/// репозитории не появляется.
/// </para>
/// <para>
/// Запись выполняется до делегирования, поэтому в аудит попадает и запуск, завершившийся отказом:
/// отказ не является основанием считать команду невыполненной.
/// </para>
/// </remarks>
internal sealed class AuditingProcessRunner : IWindowsProcessRunner
{
    private readonly IWindowsProcessRunner _inner;
    private readonly AndroidCommandAudit _audit;

    /// <summary>Создаёт записывающую границу поверх production-реализации.</summary>
    /// <param name="inner">Production-граница запуска процесса.</param>
    /// <param name="audit">Аудит, в который попадают запуски.</param>
    internal AuditingProcessRunner(IWindowsProcessRunner inner, AndroidCommandAudit audit)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audit);

        _inner = inner;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(
        WindowsProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AuditEntry entry = _audit.Begin(request);
        ApplicationResult<WindowsProcessOutcome> result =
            await _inner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        _audit.Complete(entry, result.IsSuccess ? result.Value : null);
        return result;
    }
}
