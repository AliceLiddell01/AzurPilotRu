using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Game;

/// <summary>Одно действие: свежее распознавание → проверка ввода → нажатие → отдельное наблюдение эффекта с ограничением времени.</summary>
public sealed class GameUiActionRunner(IGameUiHost host, TimeProvider clock, IGameUiTrace? trace = null)
{
    /// <summary>Период start→start наблюдения постусловия по умолчанию (<c>screenshot.normalIntervalMs</c>).</summary>
    public static readonly TimeSpan DefaultPostconditionInterval = TimeSpan.FromMilliseconds(
        Configuration.AzurPilotConfigurationDefaults.Create().Screenshot.NormalIntervalMilliseconds);
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private GameUiMeter? _meter;

    internal GameUiActionRunner(GameUiMeter meter, IGameUiHost host) : this(host, meter.Clock, meter.Trace)
    {
        _meter = meter;
    }

    /// <summary>Период start→start наблюдения постусловия; задаётся настройкой обычного интервала снимков.</summary>
    public TimeSpan PostconditionInterval
    {
        get;
        init => field = value > TimeSpan.Zero ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Интервал наблюдения должен быть положительным.");
    } = DefaultPostconditionInterval;

    /// <summary>Выполняет переход под арендой вызывающего операторского сервиса.</summary>
    public Task<GameActionOutcome> RunAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        GameUiAction action, GameActionProgress progress, CancellationToken cancellationToken)
    {
        return RunAsync(instanceId, endpoint, action, progress, null, cancellationToken);
    }

    /// <summary>
    /// Выполняет переход; уже классифицированный кадр (например, доказательство предыдущего действия)
    /// используется как исходный, только если в нём есть результаты всех признаков действия. Все проверки
    /// исходного кадра, его свежести и viewport перед вводом остаются прежними.
    /// </summary>
    public async Task<GameActionOutcome> RunAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        GameUiAction action, GameActionProgress progress, GameUiObservation? classified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(progress);
        GameUiMeter meter = _meter ??= new(host, _clock, _clock.GetTimestamp(), trace);
        IGameUiTrace? sink = meter.Trace;
        long actionStarted = _clock.GetTimestamp();
        meter.BeginAction();
        GameUiObservation? before = null;
        GameUiObservation? after = null;
        GameInputPoint? point = null;
        bool attempted = false;
        bool proven = false;
        int sourceObservations = 0, postconditionObservations = 0;
        long? tapCompleted = null, lastUnchanged = null;
        IReadOnlyList<string> excluded = action.ExcludedAssetIds ?? [];
        IReadOnlyList<string> required = action.RequiredAssetIds ?? [];
        string[] posts = [action.PostconditionAssetId, .. action.AlternativePostconditionAssetIds ?? []];
        string[] assets = [.. new[] { action.SourceAssetId }.Concat(posts).Concat(excluded).Concat(required).Distinct(StringComparer.Ordinal)];
        sink.Emit(() => new("action_started", actionStarted)
        {
            ActionId = action.Id,
            AssetId = action.SourceAssetId,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = action.SourceAssetId,
                ["required"] = string.Join(',', required),
                ["excluded"] = string.Join(',', excluded),
                ["postconditions"] = string.Join(',', posts),
            },
        });
        double? At(long? timestamp)
        {
            return timestamp is long value ? GameUiMeter.Milliseconds(_clock, actionStarted, value) : null;
        }

        GameActionOutcome Result(ApplicationFailure? failure)
        {
            sink.Emit(() => new("action_completed", _clock.GetTimestamp())
            {
                ActionId = action.Id,
                FrameId = (after ?? before)?.Frame.FrameId,
                State = proven ? "change_proven" : attempted ? "change_unproven" : "not_attempted",
                Reason = failure is null ? null
                    : failure.Details?.TryGetValue("reason", out string? detail) == true ? detail : failure.Code,
            });
            return new(action.Id, before, after, point, attempted, proven, failure, sourceObservations, postconditionObservations)
            {
                Timing = new(GameUiMeter.Milliseconds(_clock, actionStarted, _clock.GetTimestamp()), At(tapCompleted),
                    At(lastUnchanged), proven ? At(after!.Frame.CaptureStartedTimestamp) : null, meter.EndAction()),
            };
        }

        GameActionOutcome Rejected(string reason)
        {
            sink.Emit(() => new("action_rejected", _clock.GetTimestamp())
            {
                ActionId = action.Id,
                FrameId = (after ?? before)?.Frame.FrameId,
                Reason = reason,
                State = attempted ? "after_tap" : "before_tap",
            });
            return Result(Failure(reason, attempted));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (classified is not null && assets.All(id => classified.Find(id) is not null))
            {
                before = classified;
                sink.Emit(() => new("action_source", _clock.GetTimestamp())
                {
                    ActionId = action.Id,
                    FrameId = classified.Frame.FrameId,
                    Reason = "reused_classified_frame",
                });
            }
            else
            {
                long requestedAt = _clock.GetTimestamp();
                ApplicationResult<GameUiObservation> observed = await meter.ObserveAsync(instanceId, endpoint,
                    assets, cancellationToken).ConfigureAwait(false);
                sourceObservations++;
                if (observed.IsFailure)
                {
                    return Result(observed.FailureInfo);
                }

                before = observed.Value!;
                Guid sourceFrame = before.Frame.FrameId;
                sink.Emit(() => new("action_source", _clock.GetTimestamp())
                {
                    ActionId = action.Id,
                    FrameId = sourceFrame,
                    Reason = "new_capture",
                });
                if (before.Frame.CaptureStartedTimestamp < requestedAt)
                {
                    return Rejected("source_frame_invalid");
                }
            }

            if (!SameTarget(before.Frame, instanceId, endpoint))
            {
                return Rejected("target_mismatch");
            }

            if (excluded.Any(id => before.Find(id)?.Status != GameMatchStatus.Absent))
            {
                return Rejected("excluded_asset_present_or_unproven");
            }

            if (required.Any(id => before.Find(id)?.Status != GameMatchStatus.Unique))
            {
                return Rejected("required_asset_unproven");
            }

            if (posts.Any(id => before.Find(id)?.Status != GameMatchStatus.Absent))
            {
                return Rejected("postcondition_not_absent_before_action");
            }

            GameUiMatch? match = before.Find(action.SourceAssetId);
            if (match?.Status != GameMatchStatus.Unique || match.Bounds is not GamePixelRect bounds)
            {
                return Rejected("source_" + (match?.Status.ToString() ?? "missing"));
            }

            GamePixelRect safe;
            try
            {
                GameGeometry geometry = new(before.Frame.Width, before.Frame.Height);
                if (action.TapArea is GameRect area)
                {
                    // Отдельная область ввода не должна перекрывать распознанный якорь.
                    safe = geometry.MapInside(area);
                    if (safe.Left < bounds.Right && bounds.Left < safe.Right && safe.Top < bounds.Bottom && bounds.Top < safe.Bottom)
                    {
                        return Rejected("unsafe_action_region");
                    }
                }
                else if (match.SafeTapBounds is GamePixelRect attached && attached.Left >= bounds.Left && attached.Top >= bounds.Top
                    && attached.Right <= bounds.Right && attached.Bottom <= bounds.Bottom)
                {
                    safe = attached;
                }
                else
                {
                    return Rejected("unsafe_action_region");
                }

                point = geometry.Center(safe);
                GamePixelRect planned = safe;
                Guid plannedFrame = before.Frame.FrameId;
                sink.Emit(() => new("tap_planned", _clock.GetTimestamp())
                {
                    ActionId = action.Id,
                    FrameId = plannedFrame,
                    AssetId = action.SourceAssetId,
                    Region = planned,
                    Point = point,
                });
            }
            catch (ArgumentOutOfRangeException)
            {
                return Rejected("geometry_rejected");
            }

            ApplicationResult<bool> mapping = await meter.ValidateAsync(before.Frame, cancellationToken).ConfigureAwait(false);
            if (mapping.IsFailure)
            {
                return Result(mapping.FailureInfo);
            }

            if (!mapping.Value)
            {
                return Rejected("input_mapping_unproven");
            }

            TimeSpan age = _clock.GetElapsedTime(before.Frame.CaptureStartedTimestamp);
            if (age < TimeSpan.Zero || age > action.MaximumObservationAge
                || before.Frame.CaptureStartedAtUtc > _clock.GetUtcNow())
            {
                return Rejected("observation_stale");
            }

            cancellationToken.ThrowIfCancellationRequested();
            attempted = true;
            progress.MutationAttempted = true;
            progress.TapAttempts++;
            GameTap permit = new(before.Frame, action.Id, action.SourceAssetId, safe, point.Value, action.MaximumObservationAge);
            sink.Emit(() => new("tap_permitted", _clock.GetTimestamp())
            {
                ActionId = action.Id,
                FrameId = permit.Frame.FrameId,
                AssetId = permit.AssetId,
                Region = permit.SafeBounds,
                Point = permit.Point,
                Value = Math.Round(age.TotalMilliseconds, 1),
            });
            ApplicationResult<AndroidCommandOutcome> tap = await meter.TapAsync(permit, cancellationToken).ConfigureAwait(false);
            tapCompleted = _clock.GetTimestamp();
            if (tap.IsFailure)
            {
                return Result(tap.FailureInfo);
            }

            if (tap.Value!.ExitCode != 0)
            {
                return Rejected("adb_input_failed");
            }

            long started = _clock.GetTimestamp();
            HashSet<Guid> frames = [before.Frame.FrameId];
            long previousCapture = before.Frame.CaptureStartedTimestamp;
            long? previousRequest = null;
            while (_clock.GetElapsedTime(started) < action.PostconditionTimeout)
            {
                await meter.PaceAsync(previousRequest, PostconditionInterval, cancellationToken).ConfigureAwait(false);
                previousRequest = _clock.GetTimestamp();
                ApplicationResult<GameUiObservation> next = await meter.ObserveAsync(instanceId, endpoint,
                    assets, cancellationToken).ConfigureAwait(false);
                postconditionObservations++;
                if (next.IsFailure)
                {
                    return Result(next.FailureInfo);
                }

                after = next.Value!;
                if (excluded.Any(id => after.Find(id)?.Status != GameMatchStatus.Absent))
                {
                    return Rejected("excluded_asset_present_or_unproven");
                }

                if (!SameTarget(after.Frame, instanceId, endpoint) || !frames.Add(after.Frame.FrameId)
                    || after.Frame.CaptureStartedTimestamp < previousCapture
                    || after.Frame.CaptureStartedTimestamp < started
                    || after.Frame.Width != before.Frame.Width || after.Frame.Height != before.Frame.Height)
                {
                    return Rejected("postcondition_frame_invalid");
                }

                previousCapture = after.Frame.CaptureStartedTimestamp;

                if (_clock.GetElapsedTime(started) >= action.PostconditionTimeout)
                {
                    return Rejected("postcondition_unknown");
                }

                if (posts.Any(id => after.Find(id)?.Status == GameMatchStatus.Unique)
                    && posts.All(id => after.Find(id)?.Status is GameMatchStatus.Unique or GameMatchStatus.Absent)
                    && after.Find(action.SourceAssetId)?.Status == GameMatchStatus.Absent)
                {
                    ApplicationResult<bool> postMapping = await meter.ValidateAsync(after.Frame, cancellationToken).ConfigureAwait(false);
                    if (postMapping.IsFailure)
                    {
                        return Result(postMapping.FailureInfo);
                    }

                    if (!postMapping.Value)
                    {
                        return Rejected("postcondition_mapping_unproven");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (_clock.GetElapsedTime(started) >= action.PostconditionTimeout
                        || _clock.GetElapsedTime(after.Frame.CaptureStartedTimestamp) > action.MaximumObservationAge)
                    {
                        return Rejected("postcondition_unknown");
                    }

                    proven = true;
                    Guid proofFrame = after.Frame.FrameId;
                    sink.Emit(() => new("postcondition_frame", _clock.GetTimestamp())
                    {
                        ActionId = action.Id,
                        FrameId = proofFrame,
                        Reason = "proven",
                    });
                    progress.ChangeProven = true;
                    return Result(null);
                }

                lastUnchanged = after.Frame.CaptureStartedTimestamp;
                Guid unchangedFrame = after.Frame.FrameId;
                sink.Emit(() => new("postcondition_frame", _clock.GetTimestamp())
                {
                    ActionId = action.Id,
                    FrameId = unchangedFrame,
                    Reason = "unchanged",
                });
            }

            return Rejected("postcondition_unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = "Игровое действие отменено; откат не выполняется.",
                Details = new Dictionary<string, string> { [ApplicationFailure.MutationAttemptedDetailKey] = attempted ? "true" : "false" }
            });
        }
    }

    private static bool SameTarget(GameFrameProvenance frame, MuMuInstanceId instanceId, AndroidEndpoint endpoint)
    {
        return frame.InstanceId == instanceId && frame.Endpoint == endpoint;
    }

    /// <summary>Единая диагностика отказа игрового действия с ограниченным объёмом данных.</summary>
    public static ApplicationFailure Failure(string reason, bool attempted = false)
    {
        return new()
        {
            Code = ApplicationFailure.GameUiActionRejected,
            Message = "Игровое действие не подтверждено.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                [ApplicationFailure.MutationAttemptedDetailKey] = attempted ? "true" : "false",
            },
        };
    }
}
