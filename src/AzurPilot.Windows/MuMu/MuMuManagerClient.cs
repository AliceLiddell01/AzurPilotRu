using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Клиент control surface <c>MuMuManager</c>: строит точные аргументы, запускает процесс через узкую
/// границу и разбирает ответ в Windows-local DTO.
/// </summary>
/// <remarks>
/// <para>
/// Клиент не решает, что означает полученное состояние, и не подтверждает postcondition: он сообщает
/// то, что ответил провайдер. Код выхода процесса передаётся parser-у как evidence и сам по себе
/// успехом не считается.
/// </para>
/// <para>
/// Любая форма ответа, которую parser не распознал целиком, а также усечённый вывод и недостижимый
/// запуск дают application-level отказ: клиент не подставляет значения по умолчанию и не считает
/// операцию успешной «по аналогии».
/// </para>
/// <para>
/// Клиент не выполняет mutation сам по себе: он выполняет ровно ту операцию, которую запросил
/// вызывающий, и ничего не добавляет к ней — ни повторных попыток, ни завершения процессов.
/// </para>
/// </remarks>
public sealed class MuMuManagerClient
{
    /// <summary>Дедлайн одной команды control surface по умолчанию.</summary>
    /// <remarks>
    /// Значение выбрано с запасом относительно наблюдавшихся на реальной установке длительностей
    /// запуска и перезапуска экземпляра: команда запуска возвращает управление после завершения
    /// запуска, поэтому дедлайн не равен времени ответа на команду.
    /// </remarks>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(120);

    private readonly IMuMuProcessRunner _runner;
    private readonly MuMuControlSurface _controlSurface;
    private readonly TimeSpan _commandTimeout;

    /// <summary>Создаёт клиент с дедлайном команды по умолчанию.</summary>
    /// <param name="runner">Граница запуска процесса.</param>
    /// <param name="controlSurface">Точка входа control surface.</param>
    public MuMuManagerClient(IMuMuProcessRunner runner, MuMuControlSurface controlSurface)
        : this(runner, controlSurface, DefaultCommandTimeout)
    {
    }

    /// <summary>Создаёт клиент с явным дедлайном команды.</summary>
    /// <param name="runner">Граница запуска процесса.</param>
    /// <param name="controlSurface">Точка входа control surface.</param>
    /// <param name="commandTimeout">Дедлайн одной команды control surface.</param>
    /// <exception cref="ArgumentNullException">Один из аргументов равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Путь к control surface пуст или дедлайн не положителен.</exception>
    public MuMuManagerClient(
        IMuMuProcessRunner runner,
        MuMuControlSurface controlSurface,
        TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(controlSurface);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSurface.ExecutablePath);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "Дедлайн команды control surface должен быть положительным.");
        }

        _runner = runner;
        _controlSurface = controlSurface;
        _commandTimeout = commandTimeout;
    }

    /// <summary>Точка входа control surface, которой пользуется клиент.</summary>
    public MuMuControlSurface ControlSurface => _controlSurface;

    /// <summary>Запрашивает версию player.</summary>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Строка версии либо application-level отказ.</returns>
    public async Task<ApplicationResult<string>> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ApplicationResult<MuMuProcessOutcome> outcome =
            await RunAsync(MuMuManagerCommandBuilder.BuildVersionArguments(), cancellationToken)
                .ConfigureAwait(false);

        if (outcome.IsFailure && outcome.FailureInfo is ApplicationFailure versionFailure)
        {
            return ApplicationResult<string>.Failure(versionFailure);
        }

        MuMuProcessOutcome processOutcome = outcome.Value!;

        return MuMuManagerResponseParser.ParseVersion(processOutcome.StandardOutput, processOutcome.ExitCode);
    }

    /// <summary>Запрашивает сведения об одном экземпляре.</summary>
    /// <param name="id">Identity экземпляра.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Сведения об экземпляре либо application-level отказ.</returns>
    public async Task<ApplicationResult<MuMuInstanceQueryResult>> QueryInstanceAsync(
        MuMuInstanceId id,
        CancellationToken cancellationToken = default)
    {
        ApplicationResult<MuMuProcessOutcome> outcome =
            await RunAsync(MuMuManagerCommandBuilder.BuildInstanceInfoArguments(id), cancellationToken)
                .ConfigureAwait(false);

        if (outcome.IsFailure && outcome.FailureInfo is ApplicationFailure queryFailure)
        {
            return ApplicationResult<MuMuInstanceQueryResult>.Failure(queryFailure);
        }

        MuMuProcessOutcome processOutcome = outcome.Value!;

        return MuMuManagerResponseParser.ParseInstanceQuery(
            id,
            processOutcome.StandardOutput,
            processOutcome.ExitCode);
    }

    /// <summary>Перечисляет все экземпляры, включая остановленные.</summary>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Перечисление экземпляров либо application-level отказ.</returns>
    public async Task<ApplicationResult<MuMuInstanceEnumeration>> EnumerateInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        ApplicationResult<MuMuProcessOutcome> outcome =
            await RunAsync(MuMuManagerCommandBuilder.BuildAllInstancesInfoArguments(), cancellationToken)
                .ConfigureAwait(false);

        if (outcome.IsFailure && outcome.FailureInfo is ApplicationFailure enumerationFailure)
        {
            return ApplicationResult<MuMuInstanceEnumeration>.Failure(enumerationFailure);
        }

        MuMuProcessOutcome processOutcome = outcome.Value!;

        return MuMuManagerResponseParser.ParseInstanceEnumeration(
            processOutcome.StandardOutput,
            processOutcome.ExitCode);
    }

    /// <summary>Выполняет операцию изменения состояния экземпляра.</summary>
    /// <param name="id">Identity экземпляра.</param>
    /// <param name="command">Операция изменения состояния.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Ответ провайдера на операцию либо application-level отказ.</returns>
    public async Task<ApplicationResult<MuMuControlOutcome>> ExecuteControlAsync(
        MuMuInstanceId id,
        MuMuControlCommand command,
        CancellationToken cancellationToken = default)
    {
        ApplicationResult<MuMuProcessOutcome> outcome =
            await RunAsync(MuMuManagerCommandBuilder.BuildControlArguments(id, command), cancellationToken)
                .ConfigureAwait(false);

        if (outcome.IsFailure && outcome.FailureInfo is ApplicationFailure controlFailure)
        {
            return ApplicationResult<MuMuControlOutcome>.Failure(controlFailure);
        }

        MuMuProcessOutcome processOutcome = outcome.Value!;

        return MuMuManagerResponseParser.ParseControlResult(
            id,
            command,
            processOutcome.StandardOutput,
            processOutcome.ExitCode);
    }

    private async Task<ApplicationResult<MuMuProcessOutcome>> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        MuMuProcessRequest request = new()
        {
            ExecutablePath = _controlSurface.ExecutablePath,
            Arguments = arguments,
            Timeout = _commandTimeout,
        };

        ApplicationResult<MuMuProcessOutcome> result =
            await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && result.FailureInfo is ApplicationFailure runnerFailure)
        {
            return ApplicationResult<MuMuProcessOutcome>.Failure(runnerFailure);
        }

        MuMuProcessOutcome outcome = result.Value!;

        if (outcome.StandardOutputTruncated || outcome.StandardErrorTruncated)
        {
            return ApplicationResult<MuMuProcessOutcome>.Failure(
                MuMuPlatformFailureMapper.ForOutputTruncation(
                    _controlSurface.ExecutablePath,
                    outcome.StandardOutput.Length,
                    outcome.StandardError.Length));
        }

        return result;
    }
}
