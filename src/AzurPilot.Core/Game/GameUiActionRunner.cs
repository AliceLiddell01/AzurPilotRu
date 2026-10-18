using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Game;

/// <summary>Одно действие: свежее распознавание → проверка ввода → нажатие → отдельное наблюдение эффекта с ограничением времени.</summary>
public sealed class GameUiActionRunner(IGameUiHost host, TimeProvider clock)
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Выполняет переход под арендой вызывающего операторского сервиса.</summary>
    public async Task<GameActionOutcome> RunAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        GameUiAction action, GameActionProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(progress);
        GameUiObservation? before = null;
        GameUiObservation? after = null;
        GameInputPoint? point = null;
        bool attempted = false;
        bool proven = false;
        IReadOnlyList<string> excluded = action.ExcludedAssetIds ?? [];
        IReadOnlyList<string> required = action.RequiredAssetIds ?? [];
        string[] posts = [action.PostconditionAssetId, .. action.AlternativePostconditionAssetIds ?? []];
        string[] assets = [.. new[] { action.SourceAssetId }.Concat(posts).Concat(excluded).Concat(required).Distinct(StringComparer.Ordinal)];
        GameActionOutcome Result(ApplicationFailure? failure)
        {
            return new(action.Id, before, after, point, attempted, proven, failure);
        }

        GameActionOutcome Rejected(string reason)
        {
            return Result(Failure(reason, attempted));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResult<GameUiObservation> observed = await host.ObserveAsync(instanceId, endpoint,
                assets, cancellationToken).ConfigureAwait(false);
            if (observed.IsFailure)
            {
                return Result(observed.FailureInfo);
            }

            before = observed.Value!;
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

            GameUiMatch? match = before.Find(action.SourceAssetId);
            if (match?.Status != GameMatchStatus.Unique || match.SafeTapBounds is not GamePixelRect safe)
            {
                return Rejected("source_" + (match?.Status.ToString() ?? "missing"));
            }

            if (posts.Any(id => before.Find(id)?.Status != GameMatchStatus.Absent))
            {
                return Rejected("postcondition_not_absent_before_action");
            }

            GameGeometry geometry;
            try
            {
                geometry = new(before.Frame.Width, before.Frame.Height);
                point = geometry.Center(safe);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Rejected("geometry_rejected");
            }

            if (match.Bounds is not GamePixelRect bounds || safe.Left < bounds.Left || safe.Top < bounds.Top
                || safe.Right > bounds.Right || safe.Bottom > bounds.Bottom)
            {
                return Rejected("unsafe_action_region");
            }

            ApplicationResult<bool> mapping = await host.ValidateInputAsync(before.Frame, cancellationToken).ConfigureAwait(false);
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
            GameTap permit = new(before.Frame, action.SourceAssetId, safe, point.Value, action.MaximumObservationAge);
            ApplicationResult<AndroidCommandOutcome> tap = await host.TapAsync(permit, cancellationToken).ConfigureAwait(false);
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
            while (_clock.GetElapsedTime(started) < action.PostconditionTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplicationResult<GameUiObservation> next = await host.ObserveAsync(instanceId, endpoint,
                    assets, cancellationToken).ConfigureAwait(false);
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
                    ApplicationResult<bool> postMapping = await host.ValidateInputAsync(after.Frame, cancellationToken).ConfigureAwait(false);
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
                    progress.ChangeProven = true;
                    return Result(null);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150), _clock, cancellationToken).ConfigureAwait(false);
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
