using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Lifecycle игры Azur Lane Global/EN на точном endpoint-е: запуск, остановка и перезапуск с
/// идемпотентностью и доказанным postcondition.
/// </summary>
/// <remarks>
/// <para>
/// Операция считается успешной только тогда, когда postcondition доказан наблюдением: запуск — игра
/// находится на переднем плане, остановка — игра не запущена. Код выхода команды ADB доказательством не
/// является, а фиксированная задержка не используется как доказательство никогда: время идёт через
/// стандартный <see cref="TimeProvider"/>, а его числа принадлежат <see cref="AndroidLifecycleTimings"/>.
/// </para>
/// <para>
/// Postcondition доказывается пакетом игры, а не launcher-компонентом: передний план подтверждается
/// наблюдённым пакетом (<see cref="AndroidForegroundStatus.Foreground"/>), а компонент наблюдения — это
/// evidence, а не адрес mutation. После запуска launcher-компонента на переднем плане может оказаться
/// другая activity того же пакета, поэтому сравнение по компоненту не доказало бы postcondition запуска
/// никогда, и в orchestration его нет.
/// </para>
/// <para>
/// Идемпотентность: если игра уже доказанно находится в требуемом состоянии, операция завершается
/// успехом без mutation. Перезапуск исходно остановленной игры определён как запуск: остановка уже
/// доказана наблюдением, поэтому отдельного restart-recipe нет.
/// </para>
/// <para>
/// Передний план игры не является утверждением о готовности UI: здесь не проверяются splash, assets,
/// login, главное меню и всплывающие окна, и screenshot/vision-проверок в lifecycle нет. Доказано ровно
/// то, что игра находится на переднем плане.
/// </para>
/// <para>
/// Одновременные lifecycle-операции над одной парой «endpoint + пакет» сериализуются process-local
/// координацией <see cref="AndroidGameMutationGate"/>: аренда удерживается от начального наблюдения до
/// доказанного postcondition. Read-only наблюдения состояния координацию не запрашивают.
/// </para>
/// <para>
/// Отказы: недоказанное состояние игры даёт <see cref="ApplicationFailure.AzurLaneStateUnknown"/>,
/// отсутствие пакета — <see cref="ApplicationFailure.AzurLanePackageMissing"/>, неразрешимый
/// launcher-компонент — <see cref="ApplicationFailure.AzurLaneLauncherUnresolved"/> или
/// <see cref="ApplicationFailure.AzurLaneLauncherAmbiguous"/>. Достигнутый deadline без требуемого
/// состояния даёт <see cref="ApplicationFailure.AzurLaneLifecycleTimeout"/>, а выполненная mutation с
/// ненулевым кодом выхода без требуемого состояния —
/// <see cref="ApplicationFailure.AzurLaneLifecyclePostconditionNotMet"/>. Отмена на любой фазе даёт
/// <see cref="ApplicationFailure.OperationCancelled"/> с фазой в details, а ожидаемые отказы host-а
/// пробрасываются без изменений.
/// </para>
/// </remarks>
public sealed class AzurLaneGameLifecycleService
{
    private const string GatePhase = "gate";

    private const string RestartOperation = "restart";

    private readonly IAndroidHost _host;
    private readonly AzurLaneGameStateService _stateService;
    private readonly AndroidGameMutationGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly AndroidLifecycleTimings _timings;
    private readonly ILogger<AzurLaneGameLifecycleService> _logger;

    /// <summary>Создаёт orchestration lifecycle игры Azur Lane.</summary>
    /// <remarks>
    /// Зависимости обязательны: скрытых значений по умолчанию нет, поэтому orchestration никогда не
    /// уходит на реальные часы, на чужую координацию или на чужие числа времени незаметно для вызывающей
    /// стороны.
    /// </remarks>
    /// <param name="host">Host-side поверхность Android.</param>
    /// <param name="stateService">Наблюдение состояния игры: единственный владелец вывода состояния.</param>
    /// <param name="gate">
    /// Process-local координация mutation; в application host регистрируется singleton-ом, чтобы гарантия
    /// действовала для всего процесса.
    /// </param>
    /// <param name="timeProvider">Источник времени для deadline, задержки и elapsed.</param>
    /// <param name="timings">Владелец интервала опроса и deadline-значений.</param>
    /// <param name="logger">Логгер orchestration из существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    public AzurLaneGameLifecycleService(
        IAndroidHost host,
        AzurLaneGameStateService stateService,
        AndroidGameMutationGate gate,
        TimeProvider timeProvider,
        AndroidLifecycleTimings timings,
        ILogger<AzurLaneGameLifecycleService> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(stateService);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _stateService = stateService;
        _gate = gate;
        _timeProvider = timeProvider;
        _timings = timings;
        _logger = logger;
    }

    /// <summary>Запускает игру и доказывает, что она находится на переднем плане.</summary>
    /// <remarks>
    /// <para>
    /// Если игра уже доказанно на переднем плане, операция завершается успехом без mutation. Иначе
    /// разрешается launcher-компонент пакета и выполняется ровно одна package-scoped mutation запуска,
    /// после чего bounded наблюдение доказывает состояние <see cref="AzurLaneGameState.Foreground"/>.
    /// </para>
    /// <para>
    /// Состояние <see cref="AzurLaneGameState.Background"/> переводится в передний план тем же путём:
    /// задача игры уже существует, а запуск разрешённого компонента поднимает её на передний план,
    /// поэтому отдельного пути для фона нет.
    /// </para>
    /// <para>
    /// Успешная команда <c>am start</c>, её нулевой код выхода и появление процесса сами по себе
    /// postcondition не заменяют: успех объявляется только после доказанного переднего плана. Повторов
    /// запуска здесь нет — правило повтора принадлежит MuMu host lifecycle и сюда не переносится.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StartAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        string operation = AndroidNames.StartOperation;
        AndroidPackageId package = AzurLaneGameStateService.Package;
        LogRequested(operation, endpoint, package);

        IDisposable lease;
        try
        {
            lease = await _gate.AcquireAsync(endpoint, package, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GateCancelled(operation, endpoint, package);
        }

        try
        {
            ApplicationResult<AzurLaneGameObservation> initial = _stateService.ObserveAsync(endpoint);
            if (initial.IsFailure)
            {
                return Failed(operation, endpoint, package, initial.FailureInfo!, AzurLaneGameState.Unknown);
            }

            return await RunStartAsync(
                endpoint, package, operation, initial.Value!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Останавливает игру и доказывает, что она не запущена.</summary>
    /// <remarks>
    /// <para>
    /// Если игра уже доказанно остановлена, операция завершается успехом без mutation. Иначе выполняется
    /// ровно одна mutation принудительной остановки exact package, после чего bounded наблюдение
    /// доказывает отсутствие процесса игры и отсутствие игры на переднем плане.
    /// </para>
    /// <para>
    /// Data, cache, permissions, account и files игры не чистятся: lifecycle меняет только состояние
    /// процесса.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StopAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        string operation = AndroidNames.StopOperation;
        AndroidPackageId package = AzurLaneGameStateService.Package;
        LogRequested(operation, endpoint, package);

        IDisposable lease;
        try
        {
            lease = await _gate.AcquireAsync(endpoint, package, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GateCancelled(operation, endpoint, package);
        }

        try
        {
            ApplicationResult<AzurLaneGameObservation> initial = _stateService.ObserveAsync(endpoint);
            if (initial.IsFailure)
            {
                return Failed(operation, endpoint, package, initial.FailureInfo!, AzurLaneGameState.Unknown);
            }

            return await RunStopAsync(
                endpoint, package, operation, initial.Value!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Перезапускает игру и доказывает, что она находится на переднем плане.</summary>
    /// <remarks>
    /// <para>
    /// Postcondition перезапуска — доказанный передний план игры. Исходно запущенная игра
    /// перезапускается композицией: ровно одна mutation остановки → доказанная остановка → запуск →
    /// доказанный передний план. Каждая фаза доказывается наблюдением, а не кодом выхода команды.
    /// </para>
    /// <para>
    /// Для исходно остановленной игры перезапуск определён как <see cref="StartAsync"/>: остановка уже
    /// доказана наблюдением, поэтому mutation остановки не выполняется. Отдельного restart-recipe нет, и
    /// провайдерский verb перезапуска не запрашивается.
    /// </para>
    /// <para>
    /// Обе фазы живут в одном бюджете <see cref="AndroidLifecycleTimings.GameRestartDeadline"/>: фаза
    /// запуска получает остаток бюджета, а не новый, поэтому перезапуск не продлевает его остановкой.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RestartAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        string operation = RestartOperation;
        AndroidPackageId package = AzurLaneGameStateService.Package;
        LogRequested(operation, endpoint, package);

        IDisposable lease;
        try
        {
            lease = await _gate.AcquireAsync(endpoint, package, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GateCancelled(operation, endpoint, package);
        }

        try
        {
            long started = _timeProvider.GetTimestamp();
            ApplicationResult<AzurLaneGameObservation> initial = _stateService.ObserveAsync(endpoint);
            if (initial.IsFailure)
            {
                return Failed(operation, endpoint, package, initial.FailureInfo!, AzurLaneGameState.Unknown);
            }

            AzurLaneGameObservation start = initial.Value!;
            switch (start.State)
            {
                case AzurLaneGameState.NotInstalled:
                    return Failed(
                        operation,
                        endpoint,
                        package,
                        AndroidFailures.PackageMissing(endpoint, package),
                        start.State);
                case AzurLaneGameState.Unknown:
                    return UnknownState(operation, endpoint, package, start, started);
                case AzurLaneGameState.Stopped:
                    // Исходно остановленная игра: перезапуск определён как запуск.
                    return await RunStartAsync(
                        endpoint, package, operation, start, cancellationToken).ConfigureAwait(false);
                case AzurLaneGameState.Background:
                case AzurLaneGameState.Foreground:
                    break;
                default:
                    return UnknownState(operation, endpoint, package, start, started);
            }

            // Фаза запуска адресует mutation разрешённому launcher-компоненту, поэтому компонент
            // разрешается до mutation остановки: неразрешимый или неоднозначный компонент — fail-closed
            // отказ, и состояние игры при этом не меняется.
            ApplicationResult<AndroidComponent> launcher = ResolveLauncher(endpoint, package);
            if (launcher.IsFailure)
            {
                return Failed(operation, endpoint, package, launcher.FailureInfo!, start.State);
            }

            ApplicationResult<AzurLaneStopPhase> stopped = await ExecuteStopAsync(
                endpoint, package, operation, start, cancellationToken).ConfigureAwait(false);
            if (stopped.IsFailure)
            {
                return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(stopped.FailureInfo!);
            }

            // Фаза запуска живёт в остатке бюджета перезапуска, а не в новом бюджете.
            TimeSpan remaining = _timings.GameRestartDeadline - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(
                    Timeout(
                        operation,
                        endpoint,
                        package,
                        stopped.Value!.Observation.State,
                        AzurLaneGameState.Foreground,
                        started,
                        mutationExitCode: 0));
            }

            return await RunStartAsync(
                endpoint,
                package,
                operation,
                stopped.Value!.Observation,
                launcher.Value!,
                remaining,
                cancellationToken,
                mutationEvidenceBeforeStart: stopped.Value!.MutationEvidence,
                operationInitialState: start.State).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Запускает игру в бюджете запуска, разрешая launcher-компонент сама.</summary>
    /// <remarks>
    /// Единый контракт запуска: и обычный <see cref="StartAsync"/>, и перезапуск исходно остановленной
    /// игры приходят сюда, поэтому условия отказа и доказательство postcondition не дублируются.
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="initial">Наблюдение, с которого началась операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RunStartAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        AzurLaneGameObservation initial,
        CancellationToken cancellationToken)
    {
        long started = _timeProvider.GetTimestamp();
        switch (initial.State)
        {
            case AzurLaneGameState.NotInstalled:
                return Failed(
                    operation,
                    endpoint,
                    package,
                    AndroidFailures.PackageMissing(endpoint, package),
                    initial.State);
            case AzurLaneGameState.Unknown:
                return UnknownState(operation, endpoint, package, initial, started);
            case AzurLaneGameState.Foreground:
                return Succeeded(
                    operation,
                    endpoint,
                    package,
                    initial.State,
                    initial,
                    AndroidEvidence.NoMutation,
                    launcher: null,
                    started);
            case AzurLaneGameState.Stopped:
            case AzurLaneGameState.Background:
                break;
            default:
                return UnknownState(operation, endpoint, package, initial, started);
        }

        ApplicationResult<AndroidComponent> launcher = ResolveLauncher(endpoint, package);
        if (launcher.IsFailure)
        {
            return Failed(operation, endpoint, package, launcher.FailureInfo!, initial.State);
        }

        return await RunStartAsync(
            endpoint,
            package,
            operation,
            initial,
            launcher.Value!,
            _timings.GameStartDeadline,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет package-scoped mutation запуска с уже разрешённым launcher-компонентом и доказывает
    /// передний план игры.
    /// </summary>
    /// <remarks>
    /// Компонент здесь — адрес mutation, а не доказательство: postcondition подтверждается наблюдённым
    /// пакетом переднего плана. Mutation выполняется ровно одна, повторов нет.
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="initial">Наблюдение, с которого началась операция.</param>
    /// <param name="launcher">Разрешённый launcher-компонент: адрес mutation запуска.</param>
    /// <param name="deadline">Бюджет фазы запуска.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <param name="mutationEvidenceBeforeStart">
    /// Bounded evidence mutation, выполненных предыдущей фазой той же операции; у одиночного запуска —
    /// <see cref="AndroidEvidence.NoMutation"/>, то есть «mutation не выполнялось»: это значение не
    /// становится префиксом перечня выполненных mutation.
    /// </param>
    /// <param name="operationInitialState">
    /// Состояние игры на входе операции; <see langword="null"/> означает, что вход операции — это
    /// переданное наблюдение.
    /// </param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RunStartAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        AzurLaneGameObservation initial,
        AndroidComponent launcher,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        string mutationEvidenceBeforeStart = AndroidEvidence.NoMutation,
        AzurLaneGameState? operationInitialState = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        AzurLaneGameState initialState = initial.State;
        AzurLaneGameState reportedInitialState = operationInitialState ?? initialState;
        long started = _timeProvider.GetTimestamp();
        if (initialState == AzurLaneGameState.Foreground)
        {
            // Требуемое состояние уже доказано: mutation не выполняется.
            return Succeeded(
                operation,
                endpoint,
                package,
                reportedInitialState,
                initial,
                mutationEvidenceBeforeStart,
                launcher,
                started);
        }

        ApplicationResult<AndroidCommandOutcome> mutation = RequestGameMutation(
            endpoint, package, AndroidGameMutation.Start, operation, cancellationToken);
        if (mutation.IsFailure)
        {
            return Failed(operation, endpoint, package, mutation.FailureInfo!, initialState);
        }

        int exitCode = mutation.Value!.ExitCode;
        ApplicationResult<AzurLaneGameObservation> final = await WaitForForegroundAsync(
            endpoint,
            package,
            operation,
            exitCode,
            started,
            deadline,
            cancellationToken).ConfigureAwait(false);
        if (final.IsFailure)
        {
            return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(final.FailureInfo!);
        }

        // Итог описывает все mutation операции в порядке выполнения: mutation остановки фазы перезапуска
        // идёт перед mutation запуска, а не подменяется ею. Заглушка отсутствия mutation префиксом не
        // становится: у одиночного запуска перечень состоит ровно из выполненной mutation запуска.
        return Succeeded(
            operation,
            endpoint,
            package,
            reportedInitialState,
            final.Value!,
            ComposeMutationEvidence(
                mutationEvidenceBeforeStart,
                AndroidNames.Mutation(AndroidGameMutation.Start, exitCode)),
            launcher,
            started);
    }

    /// <summary>Собирает перечень фактически выполненных mutation в порядке выполнения.</summary>
    /// <remarks>
    /// <para>
    /// Перечень содержит ровно выполненные mutation: <see cref="AndroidEvidence.NoMutation"/> — это
    /// признак того, что mutation не выполнялось, а не элемент перечня, поэтому он не становится
    /// префиксом или placeholder-ом. Если выполненных mutation нет, перечень состоит из одного
    /// <see cref="AndroidEvidence.NoMutation"/>; иначе заглушка отбрасывается и остаются только
    /// выполненные mutation в порядке выполнения.
    /// </para>
    /// <para>
    /// Порядок фаз сохраняется: evidence предыдущей фазы идёт первым, потому что оно описывает mutation,
    /// выполненные раньше.
    /// </para>
    /// </remarks>
    /// <param name="evidenceBefore">Bounded evidence mutation, выполненных предыдущей фазой операции.</param>
    /// <param name="evidencePerformed">Bounded evidence mutation, выполненной текущей фазой операции.</param>
    /// <returns>Bounded перечень фактически выполненных mutation в порядке выполнения.</returns>
    private static string ComposeMutationEvidence(string evidenceBefore, string evidencePerformed)
    {
        if (string.Equals(evidenceBefore, AndroidEvidence.NoMutation, StringComparison.Ordinal))
        {
            return evidencePerformed;
        }

        if (string.Equals(evidencePerformed, AndroidEvidence.NoMutation, StringComparison.Ordinal))
        {
            return evidenceBefore;
        }

        return evidenceBefore + "," + evidencePerformed;
    }

    /// <summary>Останавливает игру и доказывает, что она не запущена.</summary>
    /// <remarks>
    /// <para>
    /// Если игра уже доказанно остановлена, операция завершается успехом без mutation. Иначе выполняется
    /// ровно одна mutation принудительной остановки exact package, после чего bounded наблюдение
    /// доказывает отсутствие процесса игры и отсутствие игры на переднем плане.
    /// </para>
    /// <para>
    /// Data, cache, permissions, account и files игры не чистятся: lifecycle меняет только состояние
    /// процесса.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="initial">Наблюдение, с которого началась операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RunStopAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        AzurLaneGameObservation initial,
        CancellationToken cancellationToken)
    {
        long started = _timeProvider.GetTimestamp();
        ApplicationResult<AzurLaneStopPhase> stopped = await ExecuteStopAsync(
            endpoint, package, operation, initial, cancellationToken).ConfigureAwait(false);
        if (stopped.IsFailure)
        {
            return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(stopped.FailureInfo!);
        }

        AzurLaneStopPhase phase = stopped.Value!;
        return Succeeded(
            operation,
            endpoint,
            package,
            initial.State,
            phase.Observation,
            phase.MutationEvidence,
            launcher: null,
            started);
    }

    /// <summary>
    /// Доводит игру до доказанной остановки и возвращает наблюдение, которым она доказана.
    /// </summary>
    /// <remarks>
    /// Единственный владелец фазы остановки: и <see cref="StopAsync"/>, и фаза остановки перезапуска
    /// проходят через него, поэтому mutation остановки, окно наблюдения и условия отказа не дублируются.
    /// Mutation выполняется ровно одна, повторов нет.
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="initial">Наблюдение, с которого началась операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Фаза остановки с доказанным наблюдением либо ожидаемый отказ.</returns>
    private async Task<ApplicationResult<AzurLaneStopPhase>> ExecuteStopAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        AzurLaneGameObservation initial,
        CancellationToken cancellationToken)
    {
        AzurLaneGameState initialState = initial.State;
        switch (initialState)
        {
            case AzurLaneGameState.NotInstalled:
                return ApplicationResult<AzurLaneStopPhase>.Failure(
                    AndroidFailures.PackageMissing(endpoint, package));
            case AzurLaneGameState.Unknown:
                return ApplicationResult<AzurLaneStopPhase>.Failure(
                    AndroidFailures.StateUnknown(
                        endpoint, package, AndroidNames.PollingPhase, initial.Evidence));
            case AzurLaneGameState.Stopped:
                return ApplicationResult<AzurLaneStopPhase>.Success(
                    new AzurLaneStopPhase(initial, AndroidEvidence.NoMutation));
            case AzurLaneGameState.Background:
            case AzurLaneGameState.Foreground:
                break;
            default:
                return ApplicationResult<AzurLaneStopPhase>.Failure(
                    AndroidFailures.StateUnknown(
                        endpoint, package, AndroidNames.PollingPhase, initial.Evidence));
        }

        long started = _timeProvider.GetTimestamp();
        ApplicationResult<AndroidCommandOutcome> mutation = RequestGameMutation(
            endpoint, package, AndroidGameMutation.ForceStop, operation, cancellationToken);
        if (mutation.IsFailure)
        {
            return ApplicationResult<AzurLaneStopPhase>.Failure(mutation.FailureInfo!);
        }

        int exitCode = mutation.Value!.ExitCode;
        ApplicationResult<AzurLaneGameObservation> final = await WaitForStoppedAsync(
            endpoint,
            package,
            operation,
            exitCode,
            started,
            _timings.GameStopDeadline,
            cancellationToken).ConfigureAwait(false);
        if (final.IsFailure)
        {
            return ApplicationResult<AzurLaneStopPhase>.Failure(final.FailureInfo!);
        }

        return ApplicationResult<AzurLaneStopPhase>.Success(
            new AzurLaneStopPhase(
                final.Value!,
                AndroidNames.Mutation(AndroidGameMutation.ForceStop, exitCode)));
    }

    /// <summary>Разрешает launcher-компонент пакета игры.</summary>
    /// <remarks>
    /// Разрешение не догадывается: отсутствие компонента, неудачный запрос и неоднозначность — разные
    /// отказы, потому что каждый требует своего действия оператора. Первый попавшийся компонент при
    /// неоднозначности не выбирается.
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, у которого разрешается launcher.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <returns>Разрешённый компонент либо ожидаемый отказ.</returns>
    private ApplicationResult<AndroidComponent> ResolveLauncher(AndroidEndpoint endpoint, AndroidPackageId package)
    {
        ApplicationResult<AndroidLauncherResolution> resolution = _host.ResolveLauncher(endpoint, package);
        if (resolution.IsFailure)
        {
            ApplicationFailure failure = resolution.FailureInfo!;
            LogLauncherUnresolved(endpoint, package, failure.Code, matchingComponentCount: 0);
            return ApplicationResult<AndroidComponent>.Failure(failure);
        }

        AndroidLauncherResolution value = resolution.Value!;
        if (value.Status == AndroidLauncherResolutionStatus.Resolved
            && value.Component is AndroidComponent component)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                AndroidLog.LauncherResolved(
                    _logger, endpoint.ToString(), package.ToString(), component.Flattened);
            }

            return ApplicationResult<AndroidComponent>.Success(component);
        }

        // Отсутствие компонента, неоднозначность и неудачный запрос одинаково не разрешают адресовать
        // mutation: первый означает, что запускать нечего, второй — что адресат не один, третий — что
        // адресат не доказан.
        LogLauncherUnresolved(
            endpoint,
            package,
            AndroidNames.LauncherStatusName(value.Status),
            value.MatchingComponentCount);

        ApplicationFailure launcherFailure =
            value.Status == AndroidLauncherResolutionStatus.Ambiguous
                ? AndroidFailures.LauncherAmbiguous(
                    endpoint, package, value.MatchingComponentCount, AndroidNames.LauncherPhase)
                : AndroidFailures.LauncherUnresolved(
                    endpoint, package, value.Status, AndroidNames.LauncherPhase);

        return ApplicationResult<AndroidComponent>.Failure(launcherFailure);
    }

    private void LogLauncherUnresolved(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string status,
        int matchingComponentCount)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AndroidLog.LauncherUnresolved(
                _logger, endpoint.ToString(), package.ToString(), status, matchingComponentCount);
        }
    }

    private ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AndroidGameMutation mutation,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplicationResult<AndroidCommandOutcome> result = _host.RequestGameMutation(
            endpoint, package, mutation, cancellationToken);
        if (result.IsFailure)
        {
            return result;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            AndroidLog.GameMutationCompleted(
                _logger,
                operation,
                endpoint.ToString(),
                package.ToString(),
                AndroidNames.MutationName(mutation),
                result.Value!.ExitCode);
        }

        return result;
    }

    /// <summary>
    /// Доказывает передний план игры в пределах bounded окна: наличие процесса exact package и игра на
    /// переднем плане.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Передний план подтверждается наблюдённым пакетом, а не launcher-компонентом: после запуска
    /// компонента на переднем плане может оказаться другая activity того же пакета.
    /// </para>
    /// <para>
    /// Недоказанное наблюдение (неудачный запрос пакета, процессов или переднего плана) даёт отказ, а не
    /// «вероятно, запущено»: состояние не выдумывается.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation запуска.</param>
    /// <param name="started">Timestamp начала фазы: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Бюджет фазы, который окно не продлевает.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение, доказавшее передний план, либо терминальный отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameObservation>> WaitForForegroundAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        int mutationExitCode,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
        => await WaitForStateAsync(
            endpoint,
            package,
            operation,
            AzurLaneGameState.Foreground,
            mutationExitCode,
            started,
            deadline,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Доказывает остановку игры в пределах bounded окна: отсутствие процессов exact package и
    /// отсутствие игры на переднем плане.
    /// </summary>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation остановки.</param>
    /// <param name="started">Timestamp начала фазы: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Бюджет фазы, который окно не продлевает.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение, доказавшее остановку, либо терминальный отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameObservation>> WaitForStoppedAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        int mutationExitCode,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
        => await WaitForStateAsync(
            endpoint,
            package,
            operation,
            AzurLaneGameState.Stopped,
            mutationExitCode,
            started,
            deadline,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Bounded polling требуемого состояния игры: единственный владелец ожидания, отказа по deadline и
    /// обработки отмены.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Наблюдение выполняется не чаще интервала опроса, а фиксированной задержки вместо наблюдения нет:
    /// каждая итерация — отдельное наблюдение состояния, и каждый poll не является событием уровня
    /// <see cref="LogLevel.Information"/>.
    /// </para>
    /// <para>
    /// Ненулевой код выхода mutation без требуемого состояния завершает ожидание отказом
    /// <see cref="ApplicationFailure.AzurLaneLifecyclePostconditionNotMet"/>: причина не во времени, и
    /// ждать deadline бессмысленно. Нулевой код выхода без требуемого состояния к deadline даёт
    /// <see cref="ApplicationFailure.AzurLaneLifecycleTimeout"/>.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный ADB endpoint, над которым выполняется операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="targetState">Требуемое состояние игры.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation.</param>
    /// <param name="started">Timestamp начала фазы: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Бюджет фазы.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение, доказавшее требуемое состояние, либо терминальный отказ.</returns>
    private async Task<ApplicationResult<AzurLaneGameObservation>> WaitForStateAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string operation,
        AzurLaneGameState targetState,
        int mutationExitCode,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        AzurLaneGameState lastObserved = AzurLaneGameState.Unknown;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApplicationResult<AzurLaneGameObservation>.Failure(
                    AndroidFailures.Cancelled(
                        endpoint,
                        package,
                        lastObserved,
                        _timeProvider.GetElapsedTime(started),
                        AndroidNames.PollingPhase));
            }

            ApplicationResult<AzurLaneGameObservation> observation = _stateService.ObserveAsync(endpoint);
            if (observation.IsFailure)
            {
                return ApplicationResult<AzurLaneGameObservation>.Failure(observation.FailureInfo!);
            }

            AzurLaneGameObservation current = observation.Value!;
            lastObserved = current.State;
            TimeSpan elapsed = _timeProvider.GetElapsedTime(started);

            if (current.State == targetState)
            {
                return observation;
            }

            if (mutationExitCode != 0)
            {
                ApplicationFailure notMet = AndroidFailures.LifecyclePostconditionNotMet(
                    endpoint, package, current.State, targetState, elapsed, mutationExitCode);
                LogFailure(operation, endpoint, package, notMet.Code, current.State, elapsed);
                return ApplicationResult<AzurLaneGameObservation>.Failure(notMet);
            }

            if (elapsed >= deadline)
            {
                return ApplicationResult<AzurLaneGameObservation>.Failure(
                    Timeout(operation, endpoint, package, current.State, targetState, started, mutationExitCode));
            }

            LogPoll(operation, endpoint, current.State, elapsed);

            try
            {
                await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApplicationResult<AzurLaneGameObservation>.Failure(
                    AndroidFailures.Cancelled(
                        endpoint,
                        package,
                        lastObserved,
                        _timeProvider.GetElapsedTime(started),
                        AndroidNames.PollingPhase));
            }
        }
    }

    private ApplicationResult<AzurLaneGameLifecycleOutcome> Succeeded(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState initialState,
        AzurLaneGameObservation finalObservation,
        string mutationEvidence,
        AndroidComponent? launcher,
        long started)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        AzurLaneGameLifecycleOutcome outcome = AzurLaneGameLifecycleOutcome.Proven(
            operation,
            endpoint,
            package,
            initialState,
            finalObservation,
            mutationEvidence,
            launcher,
            elapsed);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.GameLifecycleCompleted(
                _logger,
                operation,
                endpoint.ToString(),
                package.ToString(),
                AndroidNames.GameStateName(initialState),
                AndroidNames.GameStateName(outcome.FinalState),
                (long)elapsed.TotalMilliseconds,
                outcome.Evidence);
        }

        return ApplicationResult<AzurLaneGameLifecycleOutcome>.Success(outcome);
    }

    private ApplicationFailure Timeout(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState observedState,
        AzurLaneGameState targetState,
        long started,
        int mutationExitCode)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        ApplicationFailure failure = AndroidFailures.LifecycleTimeout(
            endpoint, package, observedState, targetState, elapsed, mutationExitCode);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AndroidLog.GameLifecycleDeadlineReached(
                _logger,
                operation,
                endpoint.ToString(),
                package.ToString(),
                AndroidNames.GameStateName(targetState),
                AndroidNames.GameStateName(observedState),
                (long)elapsed.TotalMilliseconds);
        }

        return failure;
    }

    private ApplicationResult<AzurLaneGameLifecycleOutcome> UnknownState(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameObservation observation,
        long started)
        => Failed(
            operation,
            endpoint,
            package,
            AndroidFailures.StateUnknown(
                endpoint, package, AndroidNames.PollingPhase, observation.Evidence),
            observation.State,
            started);

    private ApplicationResult<AzurLaneGameLifecycleOutcome> GateCancelled(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        ApplicationFailure failure = AndroidFailures.Cancelled(
            endpoint, package, AzurLaneGameState.Unknown, TimeSpan.Zero, GatePhase);
        LogFailure(operation, endpoint, package, failure.Code, AzurLaneGameState.Unknown, TimeSpan.Zero);
        return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(failure);
    }

    private ApplicationResult<AzurLaneGameLifecycleOutcome> Failed(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        ApplicationFailure failure,
        AzurLaneGameState state)
        => Failed(operation, endpoint, package, failure, state, _timeProvider.GetTimestamp());

    private ApplicationResult<AzurLaneGameLifecycleOutcome> Failed(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        ApplicationFailure failure,
        AzurLaneGameState state,
        long started)
    {
        LogFailure(operation, endpoint, package, failure.Code, state, _timeProvider.GetElapsedTime(started));
        return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(failure);
    }

    private void LogRequested(string operation, AndroidEndpoint endpoint, AndroidPackageId package)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.GameLifecycleRequested(_logger, operation, endpoint.ToString(), package.ToString());
        }
    }

    private void LogPoll(
        string operation,
        AndroidEndpoint endpoint,
        AzurLaneGameState state,
        TimeSpan elapsed)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            AndroidLog.GameLifecyclePollObserved(
                _logger,
                operation,
                endpoint.ToString(),
                AndroidNames.GameStateName(state),
                (long)elapsed.TotalMilliseconds);
        }
    }

    private void LogFailure(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string failureCode,
        AzurLaneGameState state,
        TimeSpan elapsed)
    {
        if (failureCode == ApplicationFailure.OperationCancelled)
        {
            // Отмена — ожидаемый исход запроса отмены, а не отказ операции: она логируется отдельным
            // событием на уровне Warning, чтобы не выглядеть как ошибка lifecycle.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                AndroidLog.GameLifecycleCancelled(
                    _logger,
                    operation,
                    endpoint.ToString(),
                    package.ToString(),
                    AndroidNames.PollingPhase,
                    (long)elapsed.TotalMilliseconds);
            }

            return;
        }

        if (_logger.IsEnabled(LogLevel.Error))
        {
            AndroidLog.GameLifecycleFailed(
                _logger,
                operation,
                endpoint.ToString(),
                package.ToString(),
                failureCode,
                AndroidNames.GameStateName(state),
                (long)elapsed.TotalMilliseconds);
        }
    }
}
