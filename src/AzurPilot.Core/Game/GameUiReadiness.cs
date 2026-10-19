using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Game;

/// <summary>Готовность UI независима от lifecycle Android и процесса игры.</summary>
public enum GameUiReadinessState
{
    /// <summary>Главный экран устойчиво доказан.</summary>
    Ready,
    /// <summary>Известная загрузка; ввод не требуется.</summary>
    Loading,
    /// <summary>Известный переход либо ещё неустойчивое главное меню.</summary>
    Transition,
    /// <summary>Известный экран с разрешённым правилом действия.</summary>
    AwaitingInput,
    /// <summary>Нужно решение человека; автоматического действия нет.</summary>
    RequiresUserAction,
    /// <summary>Экран или необходимые guards не доказаны.</summary>
    Unknown,
    /// <summary>Отказ технической границы.</summary>
    TechnicalFailure,
}

/// <summary>Данные классификации известного состояния; отсутствие шаблона не доказывает отсутствие произвольного окна.</summary>
public sealed record GameUiStateRule(string Id, GameUiReadinessState State, IReadOnlyList<string> RequiredAssetIds,
    IReadOnlyList<string> AbsentAssetIds, string? ActionId = null);

/// <summary>Воспроизводимый профиль готовности и поддерживаемых переходов каталога.</summary>
public sealed record GameUiReadinessPolicy(IReadOnlyList<GameUiStateRule> Rules)
{
    /// <summary>Все обязательные признаки наблюдаются в одном кадре.</summary>
    public IReadOnlyList<string> AssetIds => [.. Rules.SelectMany(r => r.RequiredAssetIds.Concat(r.AbsentAssetIds))
        .Distinct(StringComparer.Ordinal)];

    /// <summary>Идентификаторы фаз телеметрии: каждое правило, неизвестный экран и каждый разрешённый переход.</summary>
    public IReadOnlyList<string> PhaseIds => [.. Rules.Select(r => StatePhase(r.Id)).Append(StatePhase(UnknownStateId))
        .Concat(Rules.Where(r => r.ActionId is not null).Select(r => ActionPhase(r.ActionId!))).Distinct(StringComparer.Ordinal)];

    internal const string UnknownStateId = "unknown";

    internal static string StatePhase(string ruleId)
    {
        return "state:" + ruleId;
    }

    internal static string ActionPhase(string actionId)
    {
        return "action:" + actionId;
    }

    /// <summary>Правило Ready — только кандидат до проверки viewport и устойчивости.</summary>
    public GameUiStateRule? Classify(GameUiObservation observation)
    {
        if (AssetIds.Any(id => observation.Find(id)?.Status is not (GameMatchStatus.Unique or GameMatchStatus.Absent)))
        {
            return null;
        }

        GameUiStateRule[] found = [.. Rules.Where(r => r.RequiredAssetIds.All(id => observation.Find(id)?.Status == GameMatchStatus.Unique)
            && r.AbsentAssetIds.All(id => observation.Find(id)?.Status == GameMatchStatus.Absent))];
        return found.Length == 1 ? found[0] : null;
    }
}

/// <summary>Ограниченная история без пикселей и сведений аккаунта; ViewportProven отмечает кадры с отдельной проверкой viewport.</summary>
public sealed record GameUiReadinessEvidence(string StateId, GameUiReadinessState State, Guid FrameId,
    long CaptureTimestamp, TimeSpan Elapsed, bool ViewportProven, double? ProgressLevel);

/// <summary>Итог наблюдения/обеспечения; даже Ready относится только к указанным свежим кадрам.</summary>
public sealed record GameUiReadinessOutcome(GameUiReadinessState State, string Reason,
    GameUiObservation? LastObservation, GameFrameProvenance? FirstReadyFrame, int StableFrames,
    int Observations, int Waits, int TapAttempts, TimeSpan Elapsed,
    IReadOnlyList<GameUiReadinessEvidence> Evidence, IReadOnlyList<GameActionOutcome> Actions, ApplicationFailure? Failure)
{
    /// <summary>Фазы UI на шкале операции; невстреченные отмечены NotObserved.</summary>
    public IReadOnlyList<GameUiPhase> Phases { get; init; } = [];
    /// <summary>Суммарная собственная работа бота в UI-части.</summary>
    public GameUiWork? Work { get; init; }
}

/// <summary>
/// Bounded state-driven flow под арендой operator boundary; read-only режим никогда не вводит данные.
/// Общий срок операции принадлежит token вызывающего; собственные пределы сервиса ограничивают
/// бездействие, начальный неизвестный экран и вызов без внешнего deadline.
/// </summary>
public sealed class GameUiReadinessService(IGameUiHost host, GameUiReadinessPolicy policy,
    Func<string, GameUiAction> getAction, TimeProvider clock)
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Stability = TimeSpan.FromSeconds(1.5);
    // Минимальный интервал между началами захватов: медленный захват не дополняется лишней паузой.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StateInactivity = TimeSpan.FromSeconds(12);
    // Пассивная загрузка (стартовая или без подтверждённого Confirm) ограничена временем от первого входа
    // в состояние: полная полоса, кубик и иллюстрация не являются прогрессом. Предел равен стартовому переходу.
    private static readonly TimeSpan PassiveLoading = TimeSpan.FromSeconds(120);
    // После подтверждённого Confirm новый максимум заполнения полосы продлевает ожидание на этот срок.
    private static readonly TimeSpan LoadingInactivity = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan InitialTransition = TimeSpan.FromSeconds(120);
    // Верхняя граница Agent CLI deadline; защищает вызывающего без собственного deadline и не обрывает агентский раньше.
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    // Read-only доказательство Ready требует трёх устойчивых кадров; лишние кадры не ожидаются бесконечно.
    private const int ReadOnlyFrames = 6;
    private const int MaximumEvidence = 16;

    /// <summary>Наблюдает или обеспечивает UI; origin задаёт начало шкалы фаз всей операции.</summary>
    public async Task<GameUiReadinessOutcome> RunAsync(MuMuInstanceId target, AndroidEndpoint endpoint, bool ensure,
        GameActionProgress mutation, CancellationToken cancellationToken, bool allowInitialTransition = false, long? origin = null)
    {
        long started = clock.GetTimestamp();
        GameUiMeter meter = new(host, clock, origin ?? started);
        long stateStarted = started;
        List<GameUiReadinessEvidence> evidence = [];
        List<GameActionOutcome> actions = [];
        HashSet<string> attemptedActions = new(StringComparer.Ordinal);
        HashSet<Guid> frames = [];
        GameUiObservation? last = null;
        GameFrameProvenance? firstReady = null;
        int stable = 0, observations = 0;
        int initialTapAttempts = mutation.TapAttempts;
        long previousCapture = started - 1;
        long? previousRequest = null;
        string previousState = "";
        // Контекст скачивания: разрешение даёт только подтверждённое в этом flow действие, результатом
        // которого стала измеримая полоса; любой кадр вне загрузки его снимает. Максимум не сбрасывается.
        bool progressAuthorized = false;
        double? progressHighWater = null;
        long? lastProgress = null;
        // Повторный вход в то же пассивное состояние (мерцание распознавания) не перезапускает его срок;
        // записи сбрасываются доказанным действием, а срок неизвестных экранов — ещё и входом в загрузку.
        Dictionary<string, long> loadingEntered = new(StringComparer.Ordinal);
        Dictionary<string, long> idleEntered = new(StringComparer.Ordinal);
        GameUiReadinessState state = GameUiReadinessState.Unknown;
        bool readyProven = false;
        bool supportedStateObserved = false;
        GameUiReadinessOutcome Result(string reason, ApplicationFailure? failure = null)
        {
            meter.Exit(clock.GetTimestamp(), reason);
            GameUiWork work = meter.Total;
            return new(state == GameUiReadinessState.Ready && !readyProven ? GameUiReadinessState.Transition : state,
                reason, last, firstReady, stable, observations, work.Waits, mutation.TapAttempts - initialTapAttempts, clock.GetElapsedTime(started),
                [.. evidence], [.. actions], failure)
            {
                Phases = meter.Phases(policy.PhaseIds),
                Work = work,
            };
        }

        GameUiReadinessOutcome Rejected(string reason)
        {
            return Result(reason, GameUiActionRunner.Failure(reason, mutation.MutationAttempted));
        }

        try
        {
            while (true)
            {
                if (clock.GetElapsedTime(started) >= MaximumDuration)
                {
                    return Rejected("readiness_maximum_duration");
                }

                if (!ensure && observations >= ReadOnlyFrames)
                {
                    return Result("ready_unstable");
                }

                await meter.PaceAsync(previousRequest, FrameInterval, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                long requestedAt = clock.GetTimestamp();
                previousRequest = requestedAt;
                ApplicationResult<GameUiObservation> captured = await meter.ObserveAsync(target, endpoint, policy.AssetIds,
                    cancellationToken, deferPhase: true).ConfigureAwait(false);
                observations++;
                if (captured.IsFailure)
                {
                    state = GameUiReadinessState.TechnicalFailure;
                    return Result("observation_failed", captured.FailureInfo);
                }

                last = captured.Value!;
                TimeSpan age = clock.GetElapsedTime(last.Frame.CaptureStartedTimestamp);
                if (last.Frame.InstanceId != target || last.Frame.Endpoint != endpoint || !frames.Add(last.Frame.FrameId)
                    || last.Frame.CaptureStartedTimestamp < requestedAt
                    || last.Frame.CaptureStartedTimestamp <= previousCapture || age < TimeSpan.Zero || age > MaximumAge
                    || last.Frame.CaptureStartedAtUtc > clock.GetUtcNow())
                {
                    state = GameUiReadinessState.Unknown;
                    return Rejected("frame_invalid_or_stale");
                }

                previousCapture = last.Frame.CaptureStartedTimestamp;
                try
                {
                    _ = new GameGeometry(last.Frame.Width, last.Frame.Height);
                }
                catch (ArgumentOutOfRangeException)
                {
                    state = GameUiReadinessState.Unknown;
                    return Rejected("geometry_rejected");
                }

                GameUiStateRule? rule = policy.Classify(last);
                // Распознанная загрузка — часть стартового перехода и не сокращает ожидание его неизвестных экранов.
                supportedStateObserved |= rule is not null && rule.State != GameUiReadinessState.Loading;
                state = rule?.State ?? GameUiReadinessState.Unknown;
                string stateId = rule?.Id ?? GameUiReadinessPolicy.UnknownStateId;
                meter.Enter(GameUiReadinessPolicy.StatePhase(stateId), last.Frame.CaptureStartedTimestamp);
                if (last.Matches.Any(m => m.Status == GameMatchStatus.TechnicalFailure))
                {
                    state = GameUiReadinessState.TechnicalFailure;
                    return Rejected("recognition_failed");
                }

                // Viewport доказывается для кадров, на которых основан итог: каждый кандидат Ready и
                // результат read-only наблюдения. Перед вводом его отдельно проверяет действие;
                // пассивное ожидание без ввода не использует кадр как доказательство.
                bool viewportProven = false;
                if (state == GameUiReadinessState.Ready || !ensure)
                {
                    ApplicationResult<bool> mapped = await meter.ValidateAsync(last.Frame, cancellationToken).ConfigureAwait(false);
                    if (mapped.IsFailure || !mapped.Value)
                    {
                        state = GameUiReadinessState.Unknown;
                        return Result("viewport_unproven", mapped.IsFailure ? mapped.FailureInfo
                            : GameUiActionRunner.Failure("viewport_unproven", mutation.MutationAttempted));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.GetElapsedTime(last.Frame.CaptureStartedTimestamp) > MaximumAge)
                    {
                        return Rejected("stale_viewport_proof");
                    }

                    viewportProven = true;
                }

                if (state != GameUiReadinessState.Loading)
                {
                    progressAuthorized = false;
                    lastProgress = null;
                }
                else if (progressAuthorized && last.ProgressLevel is double level and >= 0 and <= 1
                    && progressHighWater is double highWater && level > highWater)
                {
                    // Только новый максимум доли заполнения: снижение, сброс и возврат к прежнему значению не продлевают ожидание.
                    progressHighWater = level;
                    lastProgress = clock.GetTimestamp();
                }

                if (previousState != stateId)
                {
                    stateStarted = clock.GetTimestamp();
                    if (state == GameUiReadinessState.Loading)
                    {
                        // Распознанная загрузка — собственный предел; мерцание вокруг неё не копит срок неизвестных экранов.
                        idleEntered.Clear();
                        if (!loadingEntered.TryAdd(stateId, stateStarted))
                        {
                            stateStarted = loadingEntered[stateId];
                        }
                    }
                    else if (state is GameUiReadinessState.Transition or GameUiReadinessState.Unknown
                        && !idleEntered.TryAdd(stateId, stateStarted))
                    {
                        stateStarted = idleEntered[stateId];
                    }
                }

                previousState = stateId;
                evidence.Add(new(stateId, state, last.Frame.FrameId, last.Frame.CaptureStartedTimestamp,
                    clock.GetElapsedTime(started), viewportProven, last.ProgressLevel));
                if (evidence.Count > MaximumEvidence)
                {
                    evidence.RemoveAt(0);
                }

                if (state == GameUiReadinessState.Ready)
                {
                    if (firstReady is not null && (firstReady.Width != last.Frame.Width || firstReady.Height != last.Frame.Height))
                    {
                        return Rejected("unstable_geometry");
                    }

                    firstReady ??= last.Frame;
                    stable++;
                    if (stable >= 3 && clock.GetElapsedTime(firstReady.CaptureStartedTimestamp,
                        last.Frame.CaptureStartedTimestamp) >= Stability)
                    {
                        readyProven = true;
                        return Result("stable_main_ui");
                    }

                    continue;
                }

                firstReady = null;
                stable = 0;
                if (state == GameUiReadinessState.RequiresUserAction)
                {
                    return Rejected(stateId);
                }

                if (!ensure)
                {
                    return Result(stateId);
                }

                if (state == GameUiReadinessState.AwaitingInput && rule?.ActionId is string actionId)
                {
                    // Каждый переход каталога выполняется не более одного раза за flow.
                    if (!attemptedActions.Add(actionId))
                    {
                        return Rejected("action_cycle_prevented");
                    }

                    meter.Enter(GameUiReadinessPolicy.ActionPhase(actionId), clock.GetTimestamp());
                    GameActionOutcome action = await new GameUiActionRunner(meter, host).RunAsync(target, endpoint,
                        getAction(actionId), mutation, last, cancellationToken).ConfigureAwait(false);
                    actions.Add(action);
                    if (!action.ChangeProven)
                    {
                        state = GameUiReadinessState.Unknown;
                        return Result("action_postcondition_unproven", action.Failure ?? GameUiActionRunner.Failure("action_postcondition_unproven", mutation.MutationAttempted));
                    }

                    meter.Exit(clock.GetTimestamp(), "change_proven");
                    // Postcondition действия не заменяет независимую проверку Ready.
                    if (action.After is not null)
                    {
                        _ = frames.Add(action.After.Frame.FrameId);
                        previousCapture = action.After.Frame.CaptureStartedTimestamp;
                        // Право продления даёт только основной postcondition действия, а не альтернативный исход.
                        if (action.After.Find(getAction(actionId).PostconditionAssetId)?.Status == GameMatchStatus.Unique
                            && action.After.ProgressLevel is double initialLevel and >= 0 and <= 1)
                        {
                            // Начальное заполнение — точка отсчёта, а не прогресс.
                            progressAuthorized = true;
                            progressHighWater = initialLevel;
                            lastProgress = null;
                        }
                    }

                    previousState = "";
                    previousRequest = null;
                    loadingEntered.Clear();
                    idleEntered.Clear();
                    stateStarted = clock.GetTimestamp();
                    continue;
                }

                TimeSpan inactivity = clock.GetElapsedTime(stateStarted);
                if (state == GameUiReadinessState.Loading)
                {
                    // Рост подтверждённого скачивания только продлевает пассивный срок, но не сокращает его.
                    if (inactivity >= PassiveLoading && !(lastProgress is long grown && clock.GetElapsedTime(grown) < LoadingInactivity))
                    {
                        return Rejected(lastProgress is null ? "loading_unfinished" : "loading_no_progress");
                    }

                    continue;
                }

                bool initial = state == GameUiReadinessState.Unknown && allowInitialTransition && !supportedStateObserved;
                if (inactivity >= (initial ? InitialTransition : StateInactivity))
                {
                    return Rejected(initial ? "initial_transition_unproven" : "state_unproven_or_no_progress");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result("cancelled", new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = "Ожидание игрового UI отменено; возможный эффект сохранён, откат не выполняется.",
            });
        }
    }
}
