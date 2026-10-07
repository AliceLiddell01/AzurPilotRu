using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Orchestration готовности Android: разрешение точного ADB endpoint выбранного экземпляра MuMu и
/// доказанная готовность Android на нём.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint не ищется: он разрешается только для уже выбранной identity экземпляра MuMu через
/// <see cref="IAndroidHost.ResolveEndpoint"/>. Глобального перечисления устройств ADB, выбора по
/// отображаемому имени, по порядку в ответе или по исторической формуле порта здесь нет, и значение по
/// умолчанию не подставляется.
/// </para>
/// <para>
/// <see cref="EnsureReadyAsync"/> — единственное место, где разрешена mutation transport, и только
/// target-local: ровно одно подключение к точному endpoint-у. Mutation выполняется попыткой, а не
/// доказательством: готовность доказывает наблюдение состояния transport, поэтому ненулевой код выхода
/// подключения сам по себе операцию не завершает, а нулевой сам по себе готовность не подтверждает.
/// </para>
/// <para>
/// Время идёт через стандартный <see cref="TimeProvider"/>, а его числа принадлежат
/// <see cref="AndroidLifecycleTimings"/>: фиксированной задержки вместо наблюдения нет, ожидание
/// ограничено deadline, а каждый poll отделён интервалом опроса.
/// </para>
/// <para>
/// Отказы: недостижение готового transport в пределах границы ожидания —
/// <see cref="ApplicationFailure.AndroidTransportNotReady"/>, недостижение завершённой загрузки
/// Android — <see cref="ApplicationFailure.AndroidNotReady"/>, отмена на любой фазе — существующий
/// <see cref="ApplicationFailure.OperationCancelled"/> с фазой в details. Ожидаемые отказы, возвращённые
/// host-ом (в том числе недоступный ADB или неразрешённый endpoint), пробрасываются без изменений, а
/// <see cref="ApplicationFailure.InternalError"/> для ожидаемых Android-отказов не используется.
/// </para>
/// </remarks>
public sealed class AndroidReadinessService
{
    private readonly IAndroidHost _host;
    private readonly AndroidLifecycleTimings _timings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AndroidReadinessService> _logger;

    /// <summary>Создаёт orchestration готовности Android.</summary>
    /// <remarks>
    /// Зависимости обязательны: скрытых значений по умолчанию нет, поэтому orchestration никогда не
    /// уходит на реальные часы или на чужие числа времени незаметно для вызывающей стороны.
    /// </remarks>
    /// <param name="host">Host-side поверхность Android.</param>
    /// <param name="timings">Владелец интервала опроса и deadline-значений.</param>
    /// <param name="timeProvider">Источник времени для deadline, задержки и elapsed.</param>
    /// <param name="logger">Логгер orchestration из существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    public AndroidReadinessService(
        IAndroidHost host,
        AndroidLifecycleTimings timings,
        TimeProvider timeProvider,
        ILogger<AndroidReadinessService> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _timings = timings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Разрешает точный ADB endpoint выбранного Android-экземпляра MuMu.</summary>
    /// <remarks>
    /// <para>
    /// Endpoint берётся у host-а ровно для переданной identity экземпляра: это единственный источник
    /// значения, и второго пути его получить нет. Значение не подставляется по умолчанию и не берётся у
    /// другого экземпляра.
    /// </para>
    /// <para>
    /// Ожидаемый отказ host-а (в частности недоступный bundled ADB или неразрешённый endpoint)
    /// пробрасывается без изменений, поэтому его точный код и details сохраняются.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity уже выбранного Android-экземпляра.</param>
    /// <returns>Успешный результат с точным endpoint-ом либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public ApplicationResult<AndroidEndpoint> ResolveEndpoint(MuMuInstallation installation, MuMuInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(installation);

        ApplicationResult<AndroidEndpoint> resolved = _host.ResolveEndpoint(installation, instance);
        if (resolved.IsFailure)
        {
            ApplicationFailure failure = resolved.FailureInfo!;
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                AndroidLog.EndpointResolutionFailed(_logger, instance.ToString(), failure.Code);
            }

            return resolved;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.EndpointResolved(_logger, resolved.Value!.ToString(), instance.ToString());
        }

        return resolved;
    }

    /// <summary>Доказывает готовность Android на точном endpoint-е выбранного экземпляра.</summary>
    /// <remarks>
    /// <para>
    /// Порядок шагов: разрешение endpoint → ровно одна попытка target-local подключения transport →
    /// bounded ожидание состояния, готового к командам → bounded ожидание готовности Android (shell
    /// доступен и <c>sys.boot_completed</c> равен <c>1</c>). Mutation transport не является
    /// доказательством: готовность подтверждается только наблюдением.
    /// </para>
    /// <para>
    /// Ожидание transport ограничено и <see cref="AndroidLifecycleTimings.TransportConnectDeadline"/>, и
    /// общим <see cref="AndroidLifecycleTimings.ReadinessDeadline"/>: первое заканчивает ожидание
    /// transport, второе — операцию целиком, поэтому ожидание transport не съедает бюджет готовности
    /// Android и не продлевает его.
    /// </para>
    /// <para>
    /// Отмена не сбрасывается и не продлевается: она проверяется перед mutation и между шагами, а также
    /// прерывает задержку между наблюдениями.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity уже выбранного Android-экземпляра.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог с доказанной готовностью Android либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public async Task<ApplicationResult<AndroidReadinessOutcome>> EnsureReadyAsync(
        MuMuInstallation installation,
        MuMuInstanceId instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        ApplicationResult<AndroidEndpoint> resolved = ResolveEndpoint(installation, instance);
        if (resolved.IsFailure)
        {
            return ApplicationResult<AndroidReadinessOutcome>.Failure(resolved.FailureInfo!);
        }

        AndroidEndpoint endpoint = resolved.Value!;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.OperationRequested(_logger, AndroidNames.ReadyOperation, endpoint.ToString());
        }

        long started = _timeProvider.GetTimestamp();
        TimeSpan readinessDeadline = _timings.ReadinessDeadline;

        if (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<AndroidReadinessOutcome>.Failure(
                Cancelled(endpoint, started, AndroidNames.TransportPhase));
        }

        // Подключение — попытка, а не доказательство: его код выхода остаётся bounded evidence, а
        // готовность доказывает следующее наблюдение состояния transport.
        ApplicationResult<AndroidCommandOutcome> connect = _host.ConnectTransport(endpoint, cancellationToken);
        if (connect.IsFailure)
        {
            ApplicationFailure connectFailure = connect.FailureInfo!;
            if (connectFailure.Code == ApplicationFailure.OperationCancelled)
            {
                return ApplicationResult<AndroidReadinessOutcome>.Failure(
                    Cancelled(endpoint, started, AndroidNames.TransportPhase));
            }

            // Ожидаемый отказ подключения не доказывает недостижимость endpoint-а: готовность transport
            // доказывается наблюдением, поэтому отказ остаётся диагностическим фактом.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                AndroidLog.TransportConnectFailed(_logger, endpoint.ToString(), connectFailure.Code);
            }
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            AndroidLog.TransportConnectCompleted(_logger, endpoint.ToString(), connect.Value!.ExitCode);
        }

        ApplicationResult<AndroidTransportObservation> transport = await WaitForTransportAsync(
            endpoint,
            started,
            readinessDeadline,
            cancellationToken).ConfigureAwait(false);
        if (transport.IsFailure)
        {
            return ApplicationResult<AndroidReadinessOutcome>.Failure(transport.FailureInfo!);
        }

        AndroidTransportObservation transportObservation = transport.Value!;
        ApplicationResult<AndroidBootObservation> boot = await WaitForReadyAsync(
            endpoint,
            started,
            readinessDeadline,
            cancellationToken).ConfigureAwait(false);
        if (boot.IsFailure)
        {
            return ApplicationResult<AndroidReadinessOutcome>.Failure(boot.FailureInfo!);
        }

        AndroidBootObservation observation = boot.Value!;
        string evidence = AndroidEvidence.Compose(
            AndroidNames.ReadyOperation,
            "transport=" + AndroidNames.TransportStateName(transportObservation.State),
            "boot=" + AndroidNames.Count(observation.BootCompleted ?? 0),
            "release=" + (observation.AndroidRelease ?? AndroidEvidence.NoMutation),
            "sdk=" + AndroidNames.Count(observation.SdkLevel ?? 0),
            observation.Evidence);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.ReadinessCompleted(
                _logger,
                endpoint.ToString(),
                AndroidNames.TransportStateName(transportObservation.State),
                observation.AndroidRelease ?? AndroidEvidence.NoMutation,
                AndroidNames.Count(observation.SdkLevel ?? 0),
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds,
                evidence);
        }

        return ApplicationResult<AndroidReadinessOutcome>.Success(
            new AndroidReadinessOutcome(endpoint, observation, evidence));
    }

    /// <summary>Наблюдает Android на точном endpoint-е, ничего не меняя.</summary>
    /// <remarks>
    /// <para>
    /// Read-only вариант для диагностики: <c>connect</c>, <c>reconnect</c> и любая другая mutation не
    /// выполняются, поэтому неготовый transport сообщается фактом своего состояния
    /// (<see cref="AndroidTransportState"/>), а не исправляется.
    /// </para>
    /// <para>
    /// Готовность Android наблюдается только при доказанно готовом transport: у неготового transport
    /// <see cref="AndroidReadinessFacts.Boot"/> равен <see langword="null"/>, потому что наблюдения не
    /// было, а «не наблюдалось» не выдаётся за «не готово» и не превращается в догадку.
    /// </para>
    /// <para>
    /// Ожидаемый отказ host-а пробрасывается без изменений: наблюдение не подменяет отказ и не
    /// синтезирует готовность.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity уже выбранного Android-экземпляра.</param>
    /// <returns>Успешное наблюдение либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public ApplicationResult<AndroidReadinessFacts> Observe(
        MuMuInstallation installation,
        MuMuInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(installation);

        ApplicationResult<AndroidEndpoint> resolved = ResolveEndpoint(installation, instance);
        if (resolved.IsFailure)
        {
            return ApplicationResult<AndroidReadinessFacts>.Failure(resolved.FailureInfo!);
        }

        return ObserveEndpoint(resolved.Value!);
    }

    /// <summary>Наблюдает Android на уже разрешённом точном endpoint-е, ничего не меняя.</summary>
    /// <param name="endpoint">Точный endpoint, состояние которого запрашивается.</param>
    /// <returns>Успешное наблюдение либо ожидаемый отказ.</returns>
    public ApplicationResult<AndroidReadinessFacts> Observe(AndroidEndpoint endpoint)
        => ObserveEndpoint(endpoint);

    private ApplicationResult<AndroidReadinessFacts> ObserveEndpoint(AndroidEndpoint endpoint)
    {
        ApplicationResult<AndroidTransportObservation> transport = _host.QueryTransport(endpoint);
        if (transport.IsFailure)
        {
            return ApplicationResult<AndroidReadinessFacts>.Failure(transport.FailureInfo!);
        }

        AndroidTransportObservation transportObservation = transport.Value!;
        AndroidBootObservation? boot = null;

        if (transportObservation.State == AndroidTransportState.Device)
        {
            ApplicationResult<AndroidBootObservation> observed = _host.QueryBoot(endpoint);
            if (observed.IsFailure)
            {
                return ApplicationResult<AndroidReadinessFacts>.Failure(observed.FailureInfo!);
            }

            boot = observed.Value!;
        }

        string evidence = AndroidEvidence.Compose(
            "transport=" + AndroidNames.TransportStateName(transportObservation.State),
            transportObservation.Evidence,
            boot is null ? "boot=not_observed" : "boot=" + AndroidNames.Count(boot.BootCompleted ?? 0),
            boot?.Evidence);

        return ApplicationResult<AndroidReadinessFacts>.Success(
            new AndroidReadinessFacts(endpoint, transportObservation, boot, evidence));
    }

    /// <summary>
    /// Доказывает готовность ADB transport точного endpoint-а в пределах bounded ожидания.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ожидание заканчивается, когда transport доказанно готов к командам, когда истекла граница
    /// ожидания подключения или когда истёк общий deadline операции. Отдельного пути в обход этого
    /// ожидания нет, поэтому окно и повтор не могут разойтись между операциями. Первое наблюдение
    /// выполняется всегда: истёкшая граница заканчивает ожидание только после него, поэтому «transport
    /// не готов» не сообщается без наблюдённого состояния.
    /// </para>
    /// <para>
    /// Если первым истекло общее время операции, а transport так и не стал готов, отказ сообщает
    /// состояние transport: причина в том, что transport не готов, а не в том, какая из двух границ
    /// истекла раньше. Порядок границ задаётся <see cref="AndroidLifecycleTimings"/>.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, готовность которого ожидается.</param>
    /// <param name="started">Timestamp начала операции: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Общий deadline операции, который ожидание transport не продлевает.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение готового transport либо терминальный отказ.</returns>
    private async Task<ApplicationResult<AndroidTransportObservation>> WaitForTransportAsync(
        AndroidEndpoint endpoint,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        AndroidTransportObservation? last = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApplicationResult<AndroidTransportObservation>.Failure(
                    Cancelled(endpoint, started, AndroidNames.TransportPhase));
            }

            TimeSpan elapsed = _timeProvider.GetElapsedTime(started);

            // Первое наблюдение выполняется до того, как истёкшая граница закончит ожидание: иначе отказ
            // сообщал бы «transport не готов» без наблюдения вообще, а доказанно готовый transport был бы
            // объявлен неготовым. Дальше граница действует как обычно, поэтому ожидание остаётся bounded.
            if (last is not null && (elapsed >= deadline || elapsed >= _timings.TransportConnectDeadline))
            {
                return ApplicationResult<AndroidTransportObservation>.Failure(
                    TransportNotReady(endpoint, last, started));
            }

            ApplicationResult<AndroidTransportObservation> observation = _host.QueryTransport(endpoint);
            if (observation.IsFailure)
            {
                ApplicationFailure failure = observation.FailureInfo!;
                if (failure.Code == ApplicationFailure.OperationCancelled)
                {
                    return ApplicationResult<AndroidTransportObservation>.Failure(
                        Cancelled(endpoint, started, AndroidNames.TransportPhase));
                }

                return observation;
            }

            last = observation.Value!;
            if (last.State == AndroidTransportState.Device)
            {
                return observation;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                AndroidLog.TransportPollObserved(
                    _logger,
                    endpoint.ToString(),
                    AndroidNames.TransportStateName(last.State),
                    AndroidNames.TransportPhase,
                    (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }

            try
            {
                await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApplicationResult<AndroidTransportObservation>.Failure(
                    Cancelled(endpoint, started, AndroidNames.TransportPhase));
            }
        }
    }

    /// <summary>Доказывает готовность Android на точном endpoint-е в пределах bounded ожидания.</summary>
    /// <remarks>
    /// Готовность Android доказана, когда shell устройства доступен и <c>sys.boot_completed</c> равен
    /// <c>1</c>: недоступный shell и незавершённая загрузка одинаково означают «ещё не готово», а
    /// отсутствие наблюдённого значения не выдаётся за готовность.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, готовность которого ожидается.</param>
    /// <param name="started">Timestamp начала операции: от него измеряются deadline и elapsed.</param>
    /// <param name="deadline">Общий deadline операции, который ожидание не продлевает.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Наблюдение готового Android либо терминальный отказ.</returns>
    private async Task<ApplicationResult<AndroidBootObservation>> WaitForReadyAsync(
        AndroidEndpoint endpoint,
        long started,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        AndroidBootObservation? last = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApplicationResult<AndroidBootObservation>.Failure(
                    Cancelled(endpoint, started, AndroidNames.BootPhase));
            }

            if (_timeProvider.GetElapsedTime(started) >= deadline)
            {
                return ApplicationResult<AndroidBootObservation>.Failure(NotReady(endpoint, last, started));
            }

            ApplicationResult<AndroidBootObservation> observation = _host.QueryBoot(endpoint);
            if (observation.IsFailure)
            {
                ApplicationFailure failure = observation.FailureInfo!;
                if (failure.Code == ApplicationFailure.OperationCancelled)
                {
                    return ApplicationResult<AndroidBootObservation>.Failure(
                        Cancelled(endpoint, started, AndroidNames.BootPhase));
                }

                return observation;
            }

            last = observation.Value!;
            if (IsReady(last))
            {
                return observation;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                AndroidLog.ReadinessPollObserved(
                    _logger,
                    endpoint.ToString(),
                    last.ShellAvailable,
                    AndroidNames.Count(last.BootCompleted ?? 0),
                    AndroidNames.BootPhase,
                    (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }

            try
            {
                await Task.Delay(_timings.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApplicationResult<AndroidBootObservation>.Failure(
                    Cancelled(endpoint, started, AndroidNames.BootPhase));
            }
        }
    }

    /// <summary>Проверяет, доказано ли наблюдением завершение загрузки Android.</summary>
    /// <param name="observation">Наблюдение готовности Android.</param>
    /// <returns><see langword="true"/>, если shell доступен и загрузка подтверждена устройством.</returns>
    private static bool IsReady(AndroidBootObservation observation)
        => observation.ShellAvailable && observation.BootCompleted == 1;

    private ApplicationFailure TransportNotReady(
        AndroidEndpoint endpoint,
        AndroidTransportObservation? last,
        long started)
    {
        AndroidTransportState state = last?.State ?? AndroidTransportState.Unknown;
        ApplicationFailure failure = AndroidFailures.TransportNotReady(
            endpoint,
            state,
            AndroidNames.TransportPhase);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AndroidLog.TransportNotReady(
                _logger,
                endpoint.ToString(),
                AndroidNames.TransportStateName(state),
                AndroidNames.TransportPhase,
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds,
                last?.Evidence ?? AndroidEvidence.NoMutation);
        }

        return failure;
    }

    private ApplicationFailure NotReady(AndroidEndpoint endpoint, AndroidBootObservation? last, long started)
    {
        string evidence = last is null
            ? AndroidEvidence.NoMutation
            : AndroidEvidence.Compose(
                "shell=" + (last.ShellAvailable ? "available" : "unavailable"),
                "boot=" + AndroidNames.Count(last.BootCompleted ?? 0),
                last.Evidence);
        ApplicationFailure failure = AndroidFailures.NotReady(endpoint, AndroidNames.BootPhase, evidence);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AndroidLog.ReadinessNotProven(
                _logger,
                endpoint.ToString(),
                AndroidNames.BootPhase,
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds,
                evidence);
        }

        return failure;
    }

    private ApplicationFailure Cancelled(AndroidEndpoint endpoint, long started, string phase)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);

        // Отмена адресуется точному endpoint-у: у операции готовности конкретный пакет не запрашивался,
        // поэтому в details не сообщается пакет, к которому операция не относилась.
        ApplicationFailure failure = AndroidFailures.CancelledWithoutPackage(
            endpoint,
            AzurLaneGameState.Unknown,
            elapsed,
            phase);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AndroidLog.OperationCancelled(
                _logger,
                AndroidNames.ReadyOperation,
                endpoint.ToString(),
                phase,
                (long)elapsed.TotalMilliseconds);
        }

        return failure;
    }
}
