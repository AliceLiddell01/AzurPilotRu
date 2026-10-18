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

/// <summary>Ограниченная история без пикселей и сведений аккаунта.</summary>
public sealed record GameUiReadinessEvidence(string StateId, GameUiReadinessState State, Guid FrameId,
    long CaptureTimestamp, TimeSpan Elapsed, bool ViewportProven, double? VisualActivity);

/// <summary>Итог наблюдения/обеспечения; даже Ready относится только к указанным свежим кадрам.</summary>
public sealed record GameUiReadinessOutcome(GameUiReadinessState State, string Reason,
    GameUiObservation? LastObservation, GameFrameProvenance? FirstReadyFrame, int StableFrames,
    int Observations, int Waits, int TapAttempts, TimeSpan Elapsed,
    IReadOnlyList<GameUiReadinessEvidence> Evidence, IReadOnlyList<GameActionOutcome> Actions, ApplicationFailure? Failure);

/// <summary>Bounded state-driven flow под арендой operator boundary; read-only режим никогда не вводит данные.</summary>
public sealed class GameUiReadinessService(IGameUiHost host, GameUiReadinessPolicy policy,
    Func<string, GameUiAction> getAction, TimeProvider clock)
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Stability = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>Наблюдает или обеспечивает UI; общий token содержит deadline всей операции, включая lifecycle.</summary>
    public async Task<GameUiReadinessOutcome> RunAsync(MuMuInstanceId target, AndroidEndpoint endpoint, bool ensure,
        GameActionProgress mutation, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        long stateStarted = started;
        TimeSpan limit = TimeSpan.FromSeconds(ensure ? 120 : 15);
        List<GameUiReadinessEvidence> evidence = [];
        List<GameActionOutcome> actions = [];
        HashSet<string> attemptedActions = new(StringComparer.Ordinal);
        HashSet<Guid> frames = [];
        GameUiObservation? last = null;
        GameFrameProvenance? firstReady = null;
        int stable = 0, observations = 0, waits = 0;
        int initialTapAttempts = mutation.TapAttempts;
        long previousCapture = started - 1;
        string previousState = "";
        double? previousActivity = null;
        GameUiReadinessState state = GameUiReadinessState.Unknown;
        bool readyProven = false;
        GameUiReadinessOutcome Result(string reason, ApplicationFailure? failure = null)
        {
            return new(state == GameUiReadinessState.Ready && !readyProven ? GameUiReadinessState.Transition : state,
                reason, last, firstReady, stable, observations, waits, mutation.TapAttempts - initialTapAttempts, clock.GetElapsedTime(started),
                [.. evidence], [.. actions], failure);
        }

        GameUiReadinessOutcome Rejected(string reason)
        {
            return Result(reason, GameUiActionRunner.Failure(reason, mutation.MutationAttempted));
        }

        try
        {
            while (clock.GetElapsedTime(started) < limit && observations < 160)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long requestedAt = clock.GetTimestamp();
                ApplicationResult<GameUiObservation> captured = await host.ObserveAsync(target, endpoint, policy.AssetIds,
                    cancellationToken).ConfigureAwait(false);
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
                    return Rejected("geometry_rejected");
                }

                GameUiStateRule? rule = policy.Classify(last);
                state = rule?.State ?? GameUiReadinessState.Unknown;
                if (last.Matches.Any(m => m.Status == GameMatchStatus.TechnicalFailure))
                {
                    state = GameUiReadinessState.TechnicalFailure;
                    return Rejected("recognition_failed");
                }

                // Foreground/viewport проверяются заново и для ожиданий, и для каждого кандидата Ready.
                ApplicationResult<bool> mapped = await host.ValidateInputAsync(last.Frame, cancellationToken).ConfigureAwait(false);
                if (mapped.IsFailure || !mapped.Value)
                {
                    state = GameUiReadinessState.Unknown;
                    return Result("viewport_unproven", mapped.IsFailure ? mapped.FailureInfo
                        : GameUiActionRunner.Failure("viewport_unproven", mutation.MutationAttempted));
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(started) >= limit || clock.GetElapsedTime(last.Frame.CaptureStartedTimestamp) > MaximumAge)
                {
                    return Rejected("deadline_or_stale_viewport");
                }

                string stateId = rule?.Id ?? "unknown";
                if (previousState != stateId || (state == GameUiReadinessState.Loading && last.VisualActivity is double activity
                    && previousActivity is double previous && Math.Abs(activity - previous) > 0.02))
                {
                    stateStarted = clock.GetTimestamp();
                }

                previousActivity = last.VisualActivity;
                previousState = stateId;
                evidence.Add(new(stateId, state, last.Frame.FrameId, last.Frame.CaptureStartedTimestamp,
                    clock.GetElapsedTime(started), true, last.VisualActivity));
                if (evidence.Count > 16)
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
                }
                else
                {
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
                        if (attemptedActions.Count >= 4 || !attemptedActions.Add(actionId))
                        {
                            return Rejected("action_cycle_prevented");
                        }

                        GameActionOutcome action = await new GameUiActionRunner(host, clock).RunAsync(target, endpoint,
                            getAction(actionId), mutation, cancellationToken).ConfigureAwait(false);
                        actions.Add(action);
                        if (!action.ChangeProven)
                        {
                            state = GameUiReadinessState.Unknown;
                            return Result("action_postcondition_unproven", action.Failure ?? GameUiActionRunner.Failure("action_postcondition_unproven", mutation.MutationAttempted));
                        }

                        // Postcondition действия не заменяет независимую проверку Ready.
                        if (action.After is not null)
                        {
                            _ = frames.Add(action.After.Frame.FrameId);
                            previousCapture = action.After.Frame.CaptureStartedTimestamp;
                        }

                        previousState = "";
                        stateStarted = clock.GetTimestamp();
                        continue;
                    }

                    TimeSpan inactivity = clock.GetElapsedTime(stateStarted);
                    if (inactivity >= TimeSpan.FromSeconds(state == GameUiReadinessState.Loading ? 40 : 12))
                    {
                        return Rejected(state == GameUiReadinessState.Loading ? "loading_no_visual_activity" : "state_unproven_or_no_progress");
                    }
                }

                waits++;
                await Task.Delay(Interval, clock, cancellationToken).ConfigureAwait(false);
            }

            return Rejected("readiness_deadline");
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
