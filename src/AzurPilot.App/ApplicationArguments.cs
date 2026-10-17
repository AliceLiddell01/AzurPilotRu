using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App;

/// <summary>Человекочитаемый отказ разбора аргументов запуска.</summary>
/// <param name="Message">Причина отказа.</param>
public sealed record ApplicationArgumentFailure(string Message)
{
    /// <summary>Строки отчёта для stderr.</summary>
    /// <returns>Строки, описывающие отказ и корректный запуск.</returns>
    public IReadOnlyList<string> ReportLines() =>
    [
        "Запуск отклонён: " + Message,
        "Использование: AzurPilot.App [--mode " + string.Join("|", OperatorUiModeNames.All) + "] "
            + "[--isolated-ui <directory> [--live-environment-target mumu:<index>]] "
            + "[--log-viewer --log-viewer-pipe <name> [--log-viewer-level <level>] [--log-viewer-details]]",
    ];
}

/// <summary>Разобранные аргументы запуска приложения.</summary>
/// <param name="Mode">Явно заданный режим интерфейса; <see langword="null"/> — режим берётся из конфигурации.</param>
/// <param name="LogViewer">Запрошен режим отдельной лог-консоли.</param>
/// <param name="LogViewerPipe">Имя канала отдельной лог-консоли.</param>
/// <param name="LogViewerLevel">Минимальный уровень отдельной лог-консоли.</param>
/// <param name="Failure">Ожидаемый отказ разбора аргументов; <see langword="null"/> при успехе.</param>
/// <param name="Help">Показать справку без запуска host.</param>
/// <param name="LogViewerDetails">Показывать correlation и structured properties в отдельном окне просмотра журнала.</param>
/// <param name="IsolatedUiRoot">Явный каталог сессии интерфейса для разработки; внешние изменения состояния и окно просмотра журнала отключены.</param>
/// <param name="LiveEnvironmentTarget">Запрос подготовки и восстановления точного экземпляра после проверки исходного состояния.</param>
public sealed record ApplicationArguments(
    OperatorUiMode? Mode,
    bool LogViewer,
    string? LogViewerPipe,
    LogLevel? LogViewerLevel,
    ApplicationArgumentFailure? Failure,
    bool Help = false,
    bool LogViewerDetails = false,
    string? IsolatedUiRoot = null,
    MuMuInstanceId? LiveEnvironmentTarget = null)
{
    /// <summary>Признак успешного разбора аргументов.</summary>
    public bool IsSuccess => Failure is null;

    /// <summary>Разбирает аргументы командной строки.</summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <returns>Разобранные аргументы либо ожидаемый отказ разбора.</returns>
    /// <remarks>
    /// <para>
    /// Продуктовый CLI минимален и необязателен: без аргументов приложение работает как раньше.
    /// Изолированный запуск для разработки использует отдельный каталог и запрещает внешние изменения состояния.
    /// Без этого явного параметра владельцем runtime-пути остаётся <see cref="AzurPilotConfigurationPath"/>.
    /// </para>
    /// <para>
    /// Неизвестный аргумент, отсутствующее значение и недопустимое значение — это отказ запуска.
    /// Молчаливое игнорирование сделало бы опечатку неотличимой от явно выбранного режима.
    /// </para>
    /// </remarks>
    public static ApplicationArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is ["--help"] or ["-h"])
        {
            return new ApplicationArguments(null, false, null, null, null, Help: true);
        }

        OperatorUiMode? mode = null;
        bool logViewer = false;
        bool viewerDetails = false;
        string? pipe = null;
        LogLevel? level = null;
        string? isolatedUiRoot = null;
        MuMuInstanceId? liveEnvironmentTarget = null;
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!seen.Add(argument))
            {
                return Failed(new ApplicationArgumentFailure($"Параметр {argument} задан повторно."));
            }

            switch (argument)
            {
                case "--live-environment-target":
                    if (!TryReadValue(args, ref index, argument, out string target, out ApplicationArgumentFailure? targetFailure))
                    {
                        return Failed(targetFailure!);
                    }

                    if (!MuMuInstanceValue.IsValid(target) || target == MuMuInstanceValue.AutoValue)
                    {
                        return Failed(new ApplicationArgumentFailure("Live UI требует канонический идентификатор точного экземпляра MuMu."));
                    }

                    liveEnvironmentTarget = MuMuInstanceId.FromIndex(target[MuMuInstanceValue.ProviderPrefix.Length..]);
                    break;
                case "--isolated-ui":
                    if (!TryReadValue(args, ref index, argument, out string root, out ApplicationArgumentFailure? rootFailure))
                    {
                        return Failed(rootFailure!);
                    }

                    if (!Path.IsPathFullyQualified(root))
                    {
                        return Failed(new ApplicationArgumentFailure("--isolated-ui требует абсолютный каталог конфигурации."));
                    }

                    isolatedUiRoot = Path.GetFullPath(root);
                    break;
                case "--help" or "-h":
                    return Failed(new ApplicationArgumentFailure(
                        $"{argument} не сочетается с другими параметрами."));
                case "--mode":
                    if (!TryReadValue(args, ref index, argument, out string modeValue, out ApplicationArgumentFailure? modeFailure))
                    {
                        return Failed(modeFailure!);
                    }

                    if (!OperatorUiModePresentation.TryParse(modeValue, out OperatorUiMode parsed))
                    {
                        return Failed(new ApplicationArgumentFailure(
                            $"--mode «{modeValue}» не поддерживается: допустимы "
                            + string.Join(", ", OperatorUiModeNames.All) + "."));
                    }

                    mode = parsed;
                    break;
                case "--log-viewer":
                    logViewer = true;
                    break;
                case "--log-viewer-details":
                    viewerDetails = true;
                    break;
                case "--log-viewer-pipe":
                    if (!TryReadValue(args, ref index, argument, out string pipeValue, out ApplicationArgumentFailure? pipeFailure))
                    {
                        return Failed(pipeFailure!);
                    }

                    pipe = pipeValue;
                    break;
                case "--log-viewer-level":
                    if (!TryReadValue(args, ref index, argument, out string levelValue, out ApplicationArgumentFailure? levelFailure))
                    {
                        return Failed(levelFailure!);
                    }

                    if (!Enum.TryParse(levelValue, ignoreCase: false, out LogLevel parsedLevel)
                        || !Enum.IsDefined(parsedLevel) || levelValue != parsedLevel.ToString())
                    {
                        return Failed(new ApplicationArgumentFailure(
                            $"--log-viewer-level «{levelValue}» не поддерживается: укажите имя из "
                            + string.Join(", ", Enum.GetNames<LogLevel>()) + "."));
                    }

                    level = parsedLevel;
                    break;
                default:
                    return Failed(new ApplicationArgumentFailure(
                        $"Аргумент «{argument}» не поддерживается. Доступны: --mode, --isolated-ui, --live-environment-target, --log-viewer, "
                        + "--log-viewer-pipe, --log-viewer-level, --log-viewer-details, --help."));
            }
        }

        if (logViewer && string.IsNullOrWhiteSpace(pipe))
        {
            return Failed(new ApplicationArgumentFailure(
                "Режим --log-viewer требует имя канала: --log-viewer-pipe <name>."));
        }

        if (logViewer && mode is not null)
        {
            return Failed(new ApplicationArgumentFailure("--mode не применяется к отдельной лог-консоли."));
        }

        if (liveEnvironmentTarget is not null && isolatedUiRoot is null)
        {
            return Failed(new ApplicationArgumentFailure("--live-environment-target требует --isolated-ui и отдельный YAML."));
        }

        if (logViewer && isolatedUiRoot is not null)
        {
            return Failed(new ApplicationArgumentFailure("--isolated-ui не сочетается с --log-viewer."));
        }

        if (!logViewer && (pipe is not null || level is not null || viewerDetails))
        {
            return Failed(new ApplicationArgumentFailure(
                "--log-viewer-pipe, --log-viewer-level и --log-viewer-details применимы только вместе с --log-viewer."));
        }

        return new ApplicationArguments(mode, logViewer, pipe, level, null, LogViewerDetails: viewerDetails,
            IsolatedUiRoot: isolatedUiRoot, LiveEnvironmentTarget: liveEnvironmentTarget);
    }

    /// <summary>Код выхода процесса для отказа разбора аргументов.</summary>
    /// <returns>Стабильный ненулевой код выхода.</returns>
    public static int ExitCodeFor() => AzurPilotExitCode.InvalidArgument;

    private static ApplicationArguments Failed(ApplicationArgumentFailure failure) =>
        new(null, false, null, null, failure);

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string argument,
        out string value,
        out ApplicationArgumentFailure? failure)
    {
        failure = null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            failure = new ApplicationArgumentFailure($"Аргумент {argument} требует значение.");
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
