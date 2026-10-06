using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Orchestration MuMu lifecycle: разрешение выбранного экземпляра и безопасные start/stop/restart с
/// доказанным postcondition.
/// </summary>
/// <remarks>
/// <para>
/// Операция считается успешной только тогда, когда postcondition доказан наблюдением авторитетного
/// host-side состояния конкретного экземпляра. Код выхода control utility доказательством не является,
/// а фиксированная задержка не используется как доказательство никогда: время идёт через стандартный
/// <see cref="TimeProvider"/>, а его числа принадлежат <see cref="MuMuLifecycleTimings"/>.
/// </para>
/// <para>
/// Идемпотентность: если экземпляр уже доказанно находится в требуемом состоянии, операция завершается
/// успехом без mutation.
/// </para>
/// <para>
/// Переход к <see cref="MuMuLifecycleState.Running"/> выполняет один общий внутренний контракт запуска:
/// наблюдённо подтверждённое состояние → один launch → bounded окно эффекта запуска → при формально
/// принятом launch без эффекта и подтверждённом заново <see cref="MuMuLifecycleState.Stopped"/> ровно один
/// повтор launch → доказанный <see cref="MuMuLifecycleState.Running"/>. Через него выражаются обычный
/// start и оба варианта restart, поэтому условия повтора, окно эффекта, повторное подтверждение Stopped и
/// порядок «эффект → повтор → ожидание Running» живут в одном месте и не дублируются между операциями.
/// </para>
/// <para>
/// Bounded polling наблюдает ровно один авторитетный источник состояния —
/// <see cref="IMuMuHost.ObserveInstanceState"/> — и выполняется не чаще интервала опроса; каждый poll не
/// является событием уровня <see cref="LogLevel.Information"/>.
/// </para>
/// <para>
/// Одновременные lifecycle-операции над одним экземпляром запрещены и сериализуются process-local
/// координацией <see cref="MuMuInstanceMutationGate"/>: аренда удерживается от начального наблюдения до
/// доказанного postcondition, а разные экземпляры не блокируют друг друга.
/// </para>
/// <para>
/// Отказы: любой ожидаемый отказ, возвращённый host-ом, пробрасывается без изменений;
/// <see cref="ApplicationFailure.MuMuLifecycleTimeout"/> синтезируется только при достигнутом deadline
/// без нужного состояния, <see cref="ApplicationFailure.MuMuLifecyclePostconditionNotMet"/> — только
/// когда выполненная mutation сообщила отказ своим кодом выхода, а следующее авторитетное наблюдение не
/// показало нужное состояние. Отмена возвращает существующий
/// <see cref="ApplicationFailure.OperationCancelled"/>, а <see cref="ApplicationFailure.InternalError"/>
/// не используется ни для одного ожидаемого MuMu-отказа.
/// </para>
/// </remarks>
public sealed class MuMuLifecycleService
{
    private readonly IMuMuHost _host;
    private readonly MuMuInstanceMutationGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly MuMuLifecycleTimings _timings;
    private readonly ILogger<MuMuLifecycleService> _logger;

    /// <summary>Создаёт orchestration MuMu lifecycle.</summary>
    /// <remarks>
    /// Зависимости обязательны: скрытых значений по умолчанию нет, поэтому orchestration никогда не
    /// уходит на реальные часы или на чужую координацию незаметно для вызывающей стороны.
    /// </remarks>
    /// <param name="host">Host-side поверхность MuMu.</param>
    /// <param name="gate">
    /// Process-local координация mutation; в application host регистрируется singleton-ом, чтобы
    /// гарантия действовала для всего процесса.
    /// </param>
    /// <param name="timeProvider">Источник времени для deadline, задержки и elapsed.</param>
    /// <param name="timings">Владелец интервала опроса и deadline-значений.</param>
    /// <param name="logger">Логгер orchestration из существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    public MuMuLifecycleService(
        IMuMuHost host,
        MuMuInstanceMutationGate gate,
        TimeProvider timeProvider,
        MuMuLifecycleTimings timings,
        ILogger<MuMuLifecycleService> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _gate = gate;
        _timeProvider = timeProvider;
        _timings = timings;
        _logger = logger;
    }

    /// <summary>Разрешает выбранный Android-экземпляр MuMu.</summary>
    /// <remarks>
    /// <para>
    /// Автоматический выбор разрешает только ровно один экземпляр: при нуле экземпляров возвращается
    /// <see cref="ApplicationFailure.MuMuInstanceNotFound"/>, при двух и более —
    /// <see cref="ApplicationFailure.MuMuInstanceAmbiguous"/>. Первый, последний, vmindex <c>0</c> и
    /// «наиболее вероятный» экземпляр не выбираются никогда.
    /// </para>
    /// <para>
    /// Явный выбор ищет экземпляр по stable identity: найденный экземпляр либо
    /// <see cref="ApplicationFailure.MuMuInstanceNotFound"/>. Отображаемое имя identity не является и в
    /// поиске не участвует.
    /// </para>
    /// <para>
    /// Ожидаемый отказ перечисления экземпляров пробрасывается без изменений.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="selection">Семантика выбора экземпляра.</param>
    /// <returns>Разрешение с выбранным экземпляром либо с ожидаемым отказом.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="installation"/> или <paramref name="selection"/> равны <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selection"/> не создан фабриками <see cref="MuMuInstanceSelection.Auto"/> или
    /// <see cref="MuMuInstanceSelection.Explicit"/>: явный выбор без identity не определён, а это ошибка
    /// программирования вызывающей стороны, а не ожидаемый отказ MuMu.
    /// </exception>
    public MuMuInstanceResolution Resolve(MuMuInstallation installation, MuMuInstanceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(selection);

        string selectionMode = MuMuNames.SelectionModeName(selection.IsAutomatic);
        if (!selection.IsAutomatic && selection.ExplicitId is null)
        {
            throw new ArgumentException(
                "Selection должен быть создан фабрикой MuMuInstanceSelection.Auto() или "
                + "MuMuInstanceSelection.Explicit(id): явный выбор без identity экземпляра не определён.",
                nameof(selection));
        }

        ApplicationResult<IReadOnlyList<MuMuInstance>> enumerated = _host.EnumerateInstances(installation);
        if (enumerated.IsFailure)
        {
            return FailSelection(enumerated.FailureInfo!, selectionMode);
        }

        IReadOnlyList<MuMuInstance> instances = enumerated.Value!;
        if (selection.IsAutomatic)
        {
            if (instances.Count == 0)
            {
                return FailSelection(
                    MuMuFailures.InstanceNotFound(selectionMode, requestedId: null, instanceCount: 0),
                    selectionMode);
            }

            if (instances.Count > 1)
            {
                return FailSelection(MuMuFailures.InstanceAmbiguous(selectionMode, instances), selectionMode);
            }

            return ResolveSelected(instances[0], selectionMode, instances.Count);
        }

        MuMuInstanceId requested = selection.ExplicitId.GetValueOrDefault();
        foreach (MuMuInstance candidate in instances)
        {
            if (candidate.Id == requested)
            {
                return ResolveSelected(candidate, selectionMode, instances.Count);
            }
        }

        return FailSelection(
            MuMuFailures.InstanceNotFound(selectionMode, requested, instances.Count),
            selectionMode);
    }

    /// <summary>Запускает экземпляр и доказывает состояние <see cref="MuMuLifecycleState.Running"/>.</summary>
    /// <remarks>
    /// <para>
    /// Если экземпляр уже доказанно запущен, операция завершается успехом без mutation. Иначе выполняется
    /// переход к <see cref="MuMuLifecycleState.Running"/> — единый внутренний контракт запуска: один
    /// launch, bounded окно эффекта запуска
    /// (<see cref="MuMuLifecycleTimings.LaunchEffectWindow"/>) и не более одного повтора launch, если
    /// launch формально принят, признака начала запуска за окно не появилось, а экземпляр подтверждён
    /// заново как <see cref="MuMuLifecycleState.Stopped"/>. Код выхода mutation сам по себе успехом не
    /// считается.
    /// </para>
    /// <para>
    /// Тот же контракт применяется к обоим вариантам restart, потому что гонка вызывается отправкой launch
    /// вскоре после остановки, а не самой операцией restart: гарантии не зависят от того, какой операцией
    /// запрошен launch.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Экземпляр, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="installation"/> или <paramref name="instance"/> равны <see langword="null"/>.
    /// </exception>
    public Task<ApplicationResult<MuMuLifecycleOutcome>> StartAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        CancellationToken cancellationToken)
        => RunAsync(installation, instance, MuMuLifecycleOperation.Start, cancellationToken);

    /// <summary>Останавливает экземпляр и доказывает состояние <see cref="MuMuLifecycleState.Stopped"/>.</summary>
    /// <remarks>
    /// Если экземпляр уже доказанно остановлен, операция завершается успехом без mutation. Иначе
    /// выполняется ровно одна mutation остановки конкретного экземпляра, после чего bounded polling
    /// доказывает состояние <see cref="MuMuLifecycleState.Stopped"/>. Другие экземпляры MuMu не
    /// останавливаются.
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Экземпляр, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="installation"/> или <paramref name="instance"/> равны <see langword="null"/>.
    /// </exception>
    public Task<ApplicationResult<MuMuLifecycleOutcome>> StopAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        CancellationToken cancellationToken)
        => RunAsync(installation, instance, MuMuLifecycleOperation.Stop, cancellationToken);

    /// <summary>Перезапускает экземпляр и доказывает состояние <see cref="MuMuLifecycleState.Running"/>.</summary>
    /// <remarks>
    /// <para>
    /// Postcondition restart — доказанное состояние <see cref="MuMuLifecycleState.Running"/>. Если
    /// экземпляр уже доказанно запущен, операция выполняется как композиция: ровно одна mutation
    /// остановки → доказан <see cref="MuMuLifecycleState.Stopped"/> → ровно одна mutation запуска →
    /// доказан <see cref="MuMuLifecycleState.Running"/>. Каждая фаза доказывается наблюдением, а не кодом
    /// выхода control utility.
    /// </para>
    /// <para>
    /// Для исходного состояния <see cref="MuMuLifecycleState.Stopped"/> restart определён как
    /// <see cref="StartAsync"/>: остановка уже доказана наблюдением, поэтому mutation остановки не
    /// выполняется, а переход к <see cref="MuMuLifecycleState.Running"/> выполняет тот же единый контракт
    /// запуска. Для исходного состояния <see cref="MuMuLifecycleState.Unknown"/> состояние не доказано,
    /// поэтому применяется та же композиция, что и для запущенного экземпляра.
    /// </para>
    /// <para>
    /// Провайдерский restart-verb не запрашивается: host-примитив
    /// <see cref="MuMuLifecycleMutation"/> его не содержит, а неподтверждённый provider-side restart
    /// нарушил бы контракт postcondition.
    /// </para>
    /// <para>
    /// Провайдерская гонка: launch, отправленный вскоре после доказанной остановки, иногда формально
    /// принимается (код выхода <c>0</c>), но молча не выполняется, и экземпляр остаётся остановленным.
    /// Фаза запуска restart поэтому выполняется тем же единым контрактом, что и обычный
    /// <see cref="StartAsync"/>: bounded окно эффекта запуска
    /// (<see cref="MuMuLifecycleTimings.LaunchEffectWindow"/>), где признаком начала запуска считается
    /// любое наблюдение, в котором состояние перестало быть <see cref="MuMuLifecycleState.Stopped"/>, и не
    /// более одного повтора launch при формально принятом launch без эффекта и подтверждённом заново
    /// <see cref="MuMuLifecycleState.Stopped"/>. Повтор выполняется в той же аренде
    /// <see cref="MuMuInstanceMutationGate"/>, живёт внутри <see cref="MuMuLifecycleTimings.RestartDeadline"/>
    /// и того же cancellation contract, второго повтора нет, а успех объявляется только при доказанном
    /// <see cref="MuMuLifecycleState.Running"/> — иначе честный
    /// <see cref="ApplicationFailure.MuMuLifecycleTimeout"/>.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Экземпляр, над которым выполняется операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным состоянием либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="installation"/> или <paramref name="instance"/> равны <see langword="null"/>.
    /// </exception>
    public Task<ApplicationResult<MuMuLifecycleOutcome>> RestartAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        CancellationToken cancellationToken)
        => RunAsync(installation, instance, MuMuLifecycleOperation.Restart, cancellationToken);

    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        MuMuLifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(instance);

        MuMuInstanceId instanceId = instance.Id;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            MuMuLog.LifecycleRequested(_logger, MuMuNames.OperationName(operation), instanceId.ToString());
        }

        IDisposable lease;
        try
        {
            lease = await _gate.AcquireAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ApplicationFailure cancelled = MuMuFailures.Cancelled(
                operation,
                instanceId,
                MuMuLifecycleState.Unknown,
                TimeSpan.Zero,
                MuMuFailures.GatePhase);
            LogFailure(operation, instanceId, cancelled.Code, MuMuLifecycleState.Unknown, TimeSpan.Zero);
            return ApplicationResult<MuMuLifecycleOutcome>.Failure(cancelled);
        }

        try
        {
            return await ExecuteAsync(installation, instanceId, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task<ApplicationResult<MuMuLifecycleOutcome>> ExecuteAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        long started = _timeProvider.GetTimestamp();
        TimeSpan deadline = _timings.DeadlineFor(operation);

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledOutcome(operation, instanceId, MuMuLifecycleState.Unknown, started, MuMuFailures.InitialObservationPhase);
        }

        ApplicationResult<MuMuInstanceState> initialObservation = _host.ObserveInstanceState(installation, instanceId);
        if (initialObservation.IsFailure)
        {
            return PropagateHostFailure(
                operation,
                instanceId,
                MuMuLifecycleState.Unknown,
                _timeProvider.GetElapsedTime(started),
                initialObservation.FailureInfo!);
        }

        MuMuInstanceState initial = initialObservation.Value!;
        return operation switch
        {
            MuMuLifecycleOperation.Start => await RunStartAsync(
                installation, instanceId, operation, initial, started, deadline, cancellationToken).ConfigureAwait(false),
            MuMuLifecycleOperation.Stop => await RunStopAsync(
                installation, instanceId, operation, initial, started, deadline, cancellationToken).ConfigureAwait(false),
            MuMuLifecycleOperation.Restart => await RunRestartAsync(
                installation, instanceId, operation, initial, started, deadline, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Lifecycle-операция MuMu не определена."),
        };
    }

    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunStartAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuInstanceState initial,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (initial.State == MuMuLifecycleState.Running)
        {
            return NoMutationSuccess(operation, instanceId, initial, started);
        }

        // Launch и переход к Running выполняет единый контракт запуска: start не дублирует ни окно
        // эффекта, ни условия повтора.
        return await RunLaunchTransitionAsync(
            installation,
            instanceId,
            operation,
            initial,
            initial.State,
            mutationPrefix: null,
            started,
            deadline,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunStopAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuInstanceState initial,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (initial.State == MuMuLifecycleState.Stopped)
        {
            return NoMutationSuccess(operation, instanceId, initial, started);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledOutcome(operation, instanceId, initial.State, started, MuMuFailures.MutationPhase);
        }

        ApplicationResult<MuMuLifecycleCommandOutcome> mutation = RequestMutation(
            installation, instanceId, MuMuLifecycleMutation.Stop, cancellationToken);
        if (mutation.IsFailure)
        {
            return PropagateHostFailure(
                operation, instanceId, initial.State, _timeProvider.GetElapsedTime(started), mutation.FailureInfo!);
        }

        MuMuLifecycleCommandOutcome command = mutation.Value!;
        ApplicationResult<MuMuInstanceState> terminal = await AwaitStateAsync(
            installation,
            instanceId,
            operation,
            initial.State,
            MuMuLifecycleState.Stopped,
            started,
            deadline,
            command.ExitCode,
            cancellationToken).ConfigureAwait(false);
        if (terminal.IsFailure)
        {
            return ApplicationResult<MuMuLifecycleOutcome>.Failure(terminal.FailureInfo!);
        }

        return Success(
            operation,
            instanceId,
            initial.State,
            MuMuLifecycleState.Stopped,
            MuMuEvidence.Compose(
                MuMuNames.Mutation(MuMuLifecycleMutation.Stop, command.ExitCode),
                terminal.Value!),
            _timeProvider.GetElapsedTime(started));
    }

    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunRestartAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuInstanceState initial,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (initial.State == MuMuLifecycleState.Stopped)
        {
            // Для исходного Stopped restart определён как start: остановка уже доказана наблюдением,
            // поэтому mutation остановки не выполняется, а переход к Running выполняет тот же единый
            // контракт запуска.
            return await RunStartAsync(
                installation, instanceId, operation, initial, started, deadline, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledOutcome(operation, instanceId, initial.State, started, MuMuFailures.MutationPhase);
        }

        ApplicationResult<MuMuLifecycleCommandOutcome> stop = RequestMutation(
            installation, instanceId, MuMuLifecycleMutation.Stop, cancellationToken);
        if (stop.IsFailure)
        {
            return PropagateHostFailure(
                operation, instanceId, initial.State, _timeProvider.GetElapsedTime(started), stop.FailureInfo!);
        }

        MuMuLifecycleCommandOutcome stopCommand = stop.Value!;
        ApplicationResult<MuMuInstanceState> stopped = await AwaitStateAsync(
            installation,
            instanceId,
            operation,
            initial.State,
            MuMuLifecycleState.Stopped,
            started,
            deadline,
            stopCommand.ExitCode,
            cancellationToken).ConfigureAwait(false);
        if (stopped.IsFailure)
        {
            return ApplicationResult<MuMuLifecycleOutcome>.Failure(stopped.FailureInfo!);
        }

        // Launch выполняет тот же единый контракт запуска, что и обычный start: фаза остановки лишь
        // добавляет свою mutation в evidence. Условия повтора и окно эффекта здесь не дублируются.
        return await RunLaunchTransitionAsync(
            installation,
            instanceId,
            operation,
            initial,
            stopped.Value!.State,
            MuMuNames.Mutation(MuMuLifecycleMutation.Stop, stopCommand.ExitCode),
            started,
            deadline,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Единый внутренний контракт перехода к <see cref="MuMuLifecycleState.Running"/> через launch: один
    /// launch, bounded окно эффекта запуска и не более одного повтора launch при подтверждённом no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Механизм — единственный владелец условий повтора, окна эффекта, повторного подтверждения
    /// <see cref="MuMuLifecycleState.Stopped"/> и порядка «эффект → повтор → ожидание Running». Через него
    /// выражаются все пути запуска: обычный <see cref="StartAsync"/>, restart из
    /// <see cref="MuMuLifecycleState.Stopped"/> (тот же путь, что и start) и restart из
    /// <see cref="MuMuLifecycleState.Running"/> после доказанного stop → Stopped. Поэтому гарантии не
    /// зависят от того, какой операцией запрошен launch, и retry-логика не дублируется.
    /// </para>
    /// <para>
    /// Порядок шагов: launch при наблюдённо не-Running состоянии → bounded окно эффекта (признак начала
    /// запуска, а не достижение <see cref="MuMuLifecycleState.Running"/>) → при формально принятом launch
    /// без эффекта и подтверждённом заново <see cref="MuMuLifecycleState.Stopped"/> ровно один повтор
    /// launch → ожидание доказанного <see cref="MuMuLifecycleState.Running"/>. Если окно показало
    /// <see cref="MuMuLifecycleState.Running"/>, ожидать больше нечего, а если состояние перестало быть
    /// доказанно <see cref="MuMuLifecycleState.Stopped"/>, повтор не отправляется.
    /// </para>
    /// <para>
    /// Явный отказ команды launch повтором не маскируется: окно эффекта входит только при коде выхода
    /// <c>0</c>, иначе postcondition проверяется прежним путём. Общий deadline операции и cancellation
    /// contract не сбрасываются: окно измеряется от момента launch, но ограничено тем же deadline, а
    /// второго повтора нет структурно — повтор не входит в цикл.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="initial">Наблюдение, с которого началась операция: оно попадает в итог как InitialState.</param>
    /// <param name="observedState">
    /// Состояние, наблюдённое непосредственно перед launch: для трёх путей запуска это подтверждённое
    /// <see cref="MuMuLifecycleState.Stopped"/>.
    /// </param>
    /// <param name="mutationPrefix">
    /// Evidence mutation, уже выполненных до launch (фаза остановки restart), или <see langword="null"/>,
    /// если launch первый.
    /// </param>
    /// <param name="started">Timestamp начала операции: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Общий deadline операции, который окно и повтор не продлевают.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанным <see cref="MuMuLifecycleState.Running"/> либо ожидаемый отказ.</returns>
    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunLaunchTransitionAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuInstanceState initial,
        MuMuLifecycleState observedState,
        string? mutationPrefix,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledOutcome(operation, instanceId, observedState, started, MuMuFailures.MutationPhase);
        }

        ApplicationResult<MuMuLifecycleCommandOutcome> launch = RequestMutation(
            installation, instanceId, MuMuLifecycleMutation.Start, cancellationToken);
        if (launch.IsFailure)
        {
            return PropagateHostFailure(
                operation, instanceId, observedState, _timeProvider.GetElapsedTime(started), launch.FailureInfo!);
        }

        List<int> launchExitCodes = [launch.Value!.ExitCode];
        MuMuLifecycleCommandOutcome activeLaunch = launch.Value!;
        MuMuLifecycleState knownState = observedState;

        if (activeLaunch.ExitCode == 0)
        {
            // Окно эффекта входит только при формально принятом launch: отказ команды повтором не
            // маскируется и остаётся на прежнем пути postcondition.
            ApplicationResult<MuMuInstanceState> effect = await AwaitLaunchEffectAsync(
                installation,
                instanceId,
                operation,
                observedState,
                started,
                deadline,
                activeLaunch.ExitCode,
                cancellationToken).ConfigureAwait(false);
            if (effect.IsFailure)
            {
                return ApplicationResult<MuMuLifecycleOutcome>.Failure(effect.FailureInfo!);
            }

            MuMuInstanceState effectState = effect.Value!;
            knownState = effectState.State;
            if (knownState == MuMuLifecycleState.Running)
            {
                // Признак начала запуска и postcondition доказаны одним наблюдением: ждать больше нечего.
                return TransitionSuccess(
                    operation, instanceId, initial.State, mutationPrefix, launchExitCodes, effectState, started);
            }

            if (knownState == MuMuLifecycleState.Stopped)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledOutcome(
                        operation, instanceId, knownState, started, MuMuFailures.MutationPhase);
                }

                // Окно исчерпано без признака начала запуска, и состояние подтверждено заново: разрешён
                // ровно один повтор launch в той же аренде mutation gate. Второго повтора нет.
                ApplicationResult<MuMuLifecycleCommandOutcome> retry = RequestMutation(
                    installation, instanceId, MuMuLifecycleMutation.Start, cancellationToken);
                if (retry.IsFailure)
                {
                    return PropagateHostFailure(
                        operation, instanceId, knownState, _timeProvider.GetElapsedTime(started), retry.FailureInfo!);
                }

                LogLaunchRetried(operation, instanceId, started);
                activeLaunch = retry.Value!;
                launchExitCodes.Add(activeLaunch.ExitCode);
            }
        }

        ApplicationResult<MuMuInstanceState> running = await AwaitStateAsync(
            installation,
            instanceId,
            operation,
            knownState,
            MuMuLifecycleState.Running,
            started,
            deadline,
            activeLaunch.ExitCode,
            cancellationToken).ConfigureAwait(false);
        if (running.IsFailure)
        {
            return ApplicationResult<MuMuLifecycleOutcome>.Failure(running.FailureInfo!);
        }

        return TransitionSuccess(
            operation, instanceId, initial.State, mutationPrefix, launchExitCodes, running.Value!, started);
    }

    /// <summary>Собирает успешный итог перехода к Running с evidence фактически выполненных launch.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="initialState">Наблюдение, с которого началась операция.</param>
    /// <param name="mutationPrefix">Evidence mutation до launch или <see langword="null"/>.</param>
    /// <param name="launchExitCodes">Коды выхода выполненных launch в порядке выполнения.</param>
    /// <param name="proven">Наблюдение, доказавшее <see cref="MuMuLifecycleState.Running"/>.</param>
    /// <param name="started">Timestamp начала операции.</param>
    /// <returns>Итог с доказанным <see cref="MuMuLifecycleState.Running"/>.</returns>
    private ApplicationResult<MuMuLifecycleOutcome> TransitionSuccess(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState initialState,
        string? mutationPrefix,
        IReadOnlyList<int> launchExitCodes,
        MuMuInstanceState proven,
        long started)
    {
        string launches = LaunchEvidence(launchExitCodes);
        string mutations = mutationPrefix is null ? launches : mutationPrefix + "," + launches;
        return Success(
            operation,
            instanceId,
            initialState,
            MuMuLifecycleState.Running,
            MuMuEvidence.Compose(mutations, proven),
            _timeProvider.GetElapsedTime(started));
    }

    /// <summary>Собирает evidence выполненных launch в порядке выполнения.</summary>
    /// <param name="launchExitCodes">Коды выхода выполненных launch.</param>
    /// <returns>Evidence launch: один или два элемента, разделённые запятой.</returns>
    private static string LaunchEvidence(IReadOnlyList<int> launchExitCodes)
        => string.Join(",", launchExitCodes.Select(code => MuMuNames.Mutation(MuMuLifecycleMutation.Start, code)));

    private async Task<ApplicationResult<MuMuInstanceState>> AwaitStateAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuLifecycleState initialState,
        MuMuLifecycleState targetState,
        long started,
        TimeSpan deadline,
        int mutationExitCode,
        CancellationToken cancellationToken)
    {
        MuMuLifecycleState lastObserved = initialState;

        while (true)
        {
            ApplicationResult<MuMuInstanceState> observation = ObserveOnce(
                installation, instanceId, operation, started, lastObserved, cancellationToken);
            if (observation.IsFailure)
            {
                return observation;
            }

            MuMuInstanceState state = observation.Value!;
            lastObserved = state.State;
            TimeSpan elapsed = _timeProvider.GetElapsedTime(started);

            if (state.State == targetState)
            {
                return observation;
            }

            if (mutationExitCode != 0)
            {
                // Mutation выполнена, но её код выхода сообщает отказ, а авторитетное наблюдение не
                // показывает нужное состояние: postcondition не доказан, и ждать deadline бессмысленно.
                ApplicationFailure notMet = MuMuFailures.LifecyclePostconditionNotMet(
                    operation, instanceId, state.State, elapsed, mutationExitCode);
                LogFailure(operation, instanceId, notMet.Code, state.State, elapsed);
                return ApplicationResult<MuMuInstanceState>.Failure(notMet);
            }

            if (elapsed >= deadline)
            {
                return TimeoutFailure(operation, instanceId, state.State, targetState, started, mutationExitCode);
            }

            try
            {
                await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApplicationResult<MuMuInstanceState>.Failure(
                    CancelledFailure(operation, instanceId, lastObserved, started, MuMuFailures.PollingPhase));
            }
        }
    }

    /// <summary>
    /// Наблюдает bounded окно эффекта запуска после launch и подтверждает состояние перед возможным
    /// повтором.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Эффектом считается любое доказательство начала запуска: наблюдённое состояние перестало быть
    /// <see cref="MuMuLifecycleState.Stopped"/>. Провайдерский запуск проявляет себя промежуточными
    /// состояниями (<c>player_state</c> одного из <c>starting_*</c> либо <c>is_process_started</c>), а
    /// молча не сработавший launch не меняет состояние вообще, поэтому окно измеряет признак начала
    /// запуска, а не достижение <see cref="MuMuLifecycleState.Running"/>.
    /// </para>
    /// <para>
    /// Окно принадлежит единому переходу к <see cref="MuMuLifecycleState.Running"/>, поэтому одинаково для
    /// обычного start и для обоих вариантов restart. Оно измеряется от момента launch и ограничено
    /// <see cref="MuMuLifecycleTimings.LaunchEffectWindow"/>; общий deadline операции при этом не
    /// сбрасывается и не продлевается, а отмена действует тем же запросом.
    /// </para>
    /// <para>
    /// Возвращаемое наблюдение — решение для вызывающей стороны:
    /// <see cref="MuMuLifecycleState.Running"/> (postcondition доказан),
    /// <see cref="MuMuLifecycleState.Stopped"/> (окно исчерпано, и состояние подтверждено заново
    /// отдельным наблюдением, поэтому повтор launch разрешён) либо иное состояние (начало запуска
    /// доказано, повтор не нужен). Отказ возвращается только терминальный: отмена, ожидаемый отказ host-а
    /// или достигнутый deadline.
    /// </para>
    /// <para>
    /// Метод вызывается только для формально принятого launch: отказ команды остаётся на прежнем пути и
    /// повтором не маскируется.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instanceId">Identity экземпляра, над которым выполняется операция.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="observedState">Состояние, наблюдённое непосредственно перед launch.</param>
    /// <param name="started">Timestamp начала операции: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Общий deadline операции, который окно не продлевает.</param>
    /// <param name="launchExitCode">Код выхода формально принятого launch.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение, которым закончилось окно, либо терминальный отказ.</returns>
    private async Task<ApplicationResult<MuMuInstanceState>> AwaitLaunchEffectAsync(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        MuMuLifecycleState observedState,
        long started,
        TimeSpan deadline,
        int launchExitCode,
        CancellationToken cancellationToken)
    {
        long launchStarted = _timeProvider.GetTimestamp();
        MuMuLifecycleState lastObserved = observedState;

        while (true)
        {
            ApplicationResult<MuMuInstanceState> observation = ObserveOnce(
                installation, instanceId, operation, started, lastObserved, cancellationToken);
            if (observation.IsFailure)
            {
                return observation;
            }

            MuMuLifecycleState state = observation.Value!.State;
            lastObserved = state;
            if (state != MuMuLifecycleState.Stopped)
            {
                // Начало запуска доказано: повтор не нужен, дальше — обычное ожидание Running.
                return observation;
            }

            TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
            if (elapsed >= deadline)
            {
                return TimeoutFailure(
                    operation, instanceId, state, MuMuLifecycleState.Running, started, launchExitCode);
            }

            if (_timeProvider.GetElapsedTime(launchStarted) >= _timings.LaunchEffectWindow)
            {
                LogLaunchEffectMissing(operation, instanceId, state, started);
                break;
            }

            try
            {
                await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApplicationResult<MuMuInstanceState>.Failure(
                    CancelledFailure(operation, instanceId, lastObserved, started, MuMuFailures.PollingPhase));
            }
        }

        // Повтор разрешён только при подтверждённом прежнем состоянии: перед ним состояние
        // подтверждается заново отдельным авторитетным наблюдением.
        ApplicationResult<MuMuInstanceState> confirmation = ObserveOnce(
            installation, instanceId, operation, started, lastObserved, cancellationToken);
        if (confirmation.IsFailure)
        {
            return confirmation;
        }

        MuMuLifecycleState confirmed = confirmation.Value!.State;
        if (confirmed == MuMuLifecycleState.Stopped && _timeProvider.GetElapsedTime(started) >= deadline)
        {
            return TimeoutFailure(operation, instanceId, confirmed, MuMuLifecycleState.Running, started, launchExitCode);
        }

        return confirmation;
    }

    /// <summary>
    /// Выполняет одно авторитетное наблюдение состояния экземпляра с обработкой терминальных исходов.
    /// </summary>
    /// <remarks>
    /// Единственный владелец наблюдения и его диагностики: и bounded polling, и окно эффекта запуска
    /// видят состояние одним и тем же способом, поэтому расхождение между ними невозможно.
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="started">Timestamp начала операции: от него измеряется elapsed.</param>
    /// <param name="lastObserved">Последнее наблюдённое состояние: им помечается отказ наблюдения.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение либо терминальный отказ: отмена или ожидаемый отказ host-а.</returns>
    private ApplicationResult<MuMuInstanceState> ObserveOnce(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operation,
        long started,
        MuMuLifecycleState lastObserved,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<MuMuInstanceState>.Failure(
                CancelledFailure(operation, instanceId, lastObserved, started, MuMuFailures.PollingPhase));
        }

        ApplicationResult<MuMuInstanceState> observation = _host.ObserveInstanceState(installation, instanceId);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        if (observation.IsFailure)
        {
            ApplicationFailure hostFailure = observation.FailureInfo!;
            LogFailure(operation, instanceId, hostFailure.Code, lastObserved, elapsed);
            return observation;
        }

        LogPollObserved(operation, instanceId, observation.Value!.State, elapsed);
        return observation;
    }

    /// <summary>Синтезирует окончательный отказ по достигнутому deadline операции.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние на момент достижения deadline.</param>
    /// <param name="targetState">Состояние, которое требовалось доказать.</param>
    /// <param name="started">Timestamp начала операции: от него измеряется elapsed.</param>
    /// <param name="mutationExitCode">Код выхода последней выполненной mutation.</param>
    /// <returns>Отказ <see cref="ApplicationFailure.MuMuLifecycleTimeout"/>.</returns>
    private ApplicationResult<MuMuInstanceState> TimeoutFailure(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        MuMuLifecycleState targetState,
        long started,
        int mutationExitCode)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        ApplicationFailure timeout = MuMuFailures.LifecycleTimeout(
            operation, instanceId, state, targetState, elapsed, mutationExitCode);
        LogFailure(operation, instanceId, timeout.Code, state, elapsed);
        return ApplicationResult<MuMuInstanceState>.Failure(timeout);
    }

    /// <summary>Логирует одно наблюдение состояния на диагностическом уровне.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    private void LogPollObserved(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        TimeSpan elapsed)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        MuMuLog.LifecyclePollObserved(
            _logger,
            MuMuNames.OperationName(operation),
            instanceId.ToString(),
            MuMuNames.StateName(state),
            (long)elapsed.TotalMilliseconds);
    }

    /// <summary>Логирует исчерпание окна эффекта без признака начала запуска.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние, которое не изменилось за окно.</param>
    /// <param name="started">Timestamp начала операции: от него измеряется elapsed.</param>
    private void LogLaunchEffectMissing(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        long started)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        MuMuLog.LifecycleLaunchEffectMissing(
            _logger,
            MuMuNames.OperationName(operation),
            instanceId.ToString(),
            MuMuNames.StateName(state),
            (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds,
            (long)_timings.LaunchEffectWindow.TotalMilliseconds);
    }

    /// <summary>Логирует выполнение единственного повторного launch.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="started">Timestamp начала операции: от него измеряется elapsed.</param>
    private void LogLaunchRetried(MuMuLifecycleOperation operation, MuMuInstanceId instanceId, long started)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        MuMuLog.LifecycleLaunchRetried(
            _logger,
            MuMuNames.OperationName(operation),
            instanceId.ToString(),
            (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
    }

    private ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(
        MuMuInstallation installation,
        MuMuInstanceId instanceId,
        MuMuLifecycleMutation mutation,
        CancellationToken cancellationToken)
    {
        ApplicationResult<MuMuLifecycleCommandOutcome> result = _host.RequestMutation(
            installation, instanceId, mutation, cancellationToken);
        if (result.IsSuccess)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                MuMuLog.LifecycleMutationCompleted(
                    _logger,
                    MuMuNames.MutationName(mutation),
                    instanceId.ToString(),
                    result.Value!.ExitCode);
            }
        }

        return result;
    }

    private ApplicationResult<MuMuLifecycleOutcome> NoMutationSuccess(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuInstanceState initial,
        long started)
    {
        string evidence = MuMuEvidence.Compose(MuMuEvidence.NoMutation, initial);
        return Success(
            operation,
            instanceId,
            initial.State,
            initial.State,
            evidence,
            _timeProvider.GetElapsedTime(started));
    }

    private ApplicationResult<MuMuLifecycleOutcome> Success(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState initialState,
        MuMuLifecycleState finalState,
        string evidence,
        TimeSpan elapsed)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            MuMuLog.LifecycleCompleted(
                _logger,
                MuMuNames.OperationName(operation),
                instanceId.ToString(),
                MuMuNames.StateName(initialState),
                MuMuNames.StateName(finalState),
                (long)elapsed.TotalMilliseconds,
                evidence);
        }

        return ApplicationResult<MuMuLifecycleOutcome>.Success(
            new MuMuLifecycleOutcome(operation, initialState, finalState, evidence, elapsed)
            {
                InstanceId = instanceId,
            });
    }

    private ApplicationResult<MuMuLifecycleOutcome> PropagateHostFailure(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        TimeSpan elapsed,
        ApplicationFailure hostFailure)
    {
        LogFailure(operation, instanceId, hostFailure.Code, state, elapsed);
        return ApplicationResult<MuMuLifecycleOutcome>.Failure(hostFailure);
    }

    private ApplicationResult<MuMuLifecycleOutcome> CancelledOutcome(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        long started,
        string phase)
        => ApplicationResult<MuMuLifecycleOutcome>.Failure(
            CancelledFailure(operation, instanceId, state, started, phase));

    private ApplicationFailure CancelledFailure(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        long started,
        string phase)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        ApplicationFailure failure = MuMuFailures.Cancelled(operation, instanceId, state, elapsed, phase);
        LogFailure(operation, instanceId, failure.Code, state, elapsed);
        return failure;
    }

    private void LogFailure(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        string failureCode,
        MuMuLifecycleState state,
        TimeSpan elapsed)
    {
        if (!_logger.IsEnabled(LogLevel.Error))
        {
            return;
        }

        MuMuLog.LifecycleFailed(
            _logger,
            MuMuNames.OperationName(operation),
            instanceId.ToString(),
            failureCode,
            MuMuNames.StateName(state),
            (long)elapsed.TotalMilliseconds);
    }

    private MuMuInstanceResolution ResolveSelected(MuMuInstance instance, string selectionMode, int instanceCount)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            MuMuLog.InstanceSelected(_logger, selectionMode, instance.Id.ToString(), instanceCount);
        }

        return MuMuInstanceResolution.Resolved(instance);
    }

    private MuMuInstanceResolution FailSelection(ApplicationFailure failure, string selectionMode)
    {
        MuMuLog.InstanceSelectionFailed(_logger, selectionMode, failure.Code);
        return MuMuInstanceResolution.Failed(failure);
    }
}
