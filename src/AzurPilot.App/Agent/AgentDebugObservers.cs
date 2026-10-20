using System.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;

namespace AzurPilot.App.Agent;

/// <summary>
/// Наблюдает реальные вызовы production game UI host: результаты распознавания, проверку viewport и
/// единственный адресованный tap. Решения и аргументы не изменяются, повторов и дополнительных захватов нет.
/// </summary>
internal sealed class AgentDebugGameUiHost(IGameUiHost inner, AgentDebugRun run) : IGameUiHost
{
    public async Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        IReadOnlyList<string> assets, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        run.Guard("observe", () => run.Trace.Record("gameui-host", "observe_requested", new { assets = assets.Count }));
        ApplicationResult<GameUiObservation> result;
        try
        {
            result = await inner.ObserveAsync(instanceId, endpoint, assets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            run.Guard("observe", () => run.Trace.Record("gameui-host", "observe_failed", new { exception = exception.GetType().Name }));
            throw;
        }

        run.Guard("observe", () => ObserveCompleted(result, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)));
        return result;
    }

    public async Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
    {
        AgentDebugFrameRecord? record = null;
        long started = Stopwatch.GetTimestamp();
        run.Guard("viewport", () =>
        {
            record = run.Frames.Find(frame.FrameId);
            run.Trace.Record("gameui-host", "viewport_requested", null, record?.Reference, frame.FrameId);
        });
        ApplicationResult<bool> result;
        try
        {
            result = await inner.ValidateInputAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            run.Guard("viewport", () => run.Trace.Record("gameui-host", "viewport_failed", new { exception = exception.GetType().Name },
                record?.Reference, frame.FrameId));
            throw;
        }

        run.Guard("viewport", () =>
        {
            double elapsed = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
            run.Metrics.Add("viewport", elapsed);
            string outcome = result.IsSuccess ? (result.Value ? "proven" : "unproven")
                : "rejected:" + Reason(result.FailureInfo!);
            if (record is not null)
            {
                run.Frames.Update(record, r => r.Viewport = outcome);
            }

            run.Trace.Record("gameui-host", result.IsSuccess && result.Value ? "viewport_proven" : "viewport_rejected",
                new { elapsedMs = elapsed, outcome, failure = result.IsFailure ? AgentJson.Failure(result.FailureInfo) : null },
                record?.Reference, frame.FrameId);
        });
        return result;
    }

    public async Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
    {
        AgentDebugFrameRecord? record = null;
        long started = Stopwatch.GetTimestamp();
        run.Guard("tap", () =>
        {
            record = run.Frames.Find(tap.Frame.FrameId);
            Mark(record, new("tap_attempted", tap.ActionId, "tap_attempted", tap.SafeBounds, tap.Point));
            run.Trace.Record("gameui-host", "tap_attempted", new { asset = tap.AssetId, point = tap.Point, safeBounds = tap.SafeBounds },
                record?.Reference, tap.Frame.FrameId, tap.ActionId);
        });
        ApplicationResult<AndroidCommandOutcome> result;
        // Область ввода связывает фактический запуск ADB именно с этим разрешением, кадром и действием.
        AgentDebugTapContext.Current = new AgentDebugTapContext.Scope(tap, record?.Reference);
        try
        {
            result = await inner.TapAsync(tap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Исключение после вызова границы: ввод мог состояться, эффект не доказан.
            run.Guard("tap", () =>
            {
                Mark(record, new("tap_result_unproven", tap.ActionId, "tap_result_unproven", tap.SafeBounds, tap.Point));
                run.Trace.Record("gameui-host", "tap_result_unproven", new { exception = exception.GetType().Name },
                    record?.Reference, tap.Frame.FrameId, tap.ActionId);
            });
            throw;
        }
        finally
        {
            AgentDebugTapContext.Current = null;
        }

        run.Guard("tap", () => TapCompleted(tap, record, result, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)));
        return result;
    }

    private void ObserveCompleted(ApplicationResult<GameUiObservation> result, double elapsed)
    {
        run.Metrics.Add("observe", elapsed);
        if (result.IsFailure)
        {
            run.Trace.Record("gameui-host", "observe_failed", new { elapsedMs = elapsed, failure = AgentJson.Failure(result.FailureInfo) });
            return;
        }

        GameUiObservation observation = result.Value!;
        AgentDebugFrameRecord? record = run.Frames.AttachObservation(observation);
        if (observation.Cost is GameObservationCost cost)
        {
            run.Metrics.Add("match", Math.Round(cost.Match.TotalMilliseconds, 1));
        }

        run.Trace.Record("gameui-host", "observe_completed", new
        {
            elapsedMs = elapsed,
            width = observation.Frame.Width,
            height = observation.Frame.Height,
            progressLevel = observation.ProgressLevel,
            cost = observation.Cost is GameObservationCost c ? new
            {
                captureMs = Math.Round(c.Capture.TotalMilliseconds, 1),
                captureCommandMs = Math.Round(c.CaptureCommand.TotalMilliseconds, 1),
                matchMs = Math.Round(c.Match.TotalMilliseconds, 1),
            } : null,
            matches = observation.Matches.Take(64).Select(m => new
            {
                asset = m.AssetId,
                variant = m.VariantId,
                status = m.Status,
                score = Math.Round(m.Score, 4),
                runnerUp = Math.Round(m.RunnerUpScore, 4),
                margin = Math.Round(m.Score - m.RunnerUpScore, 4),
                mae = Math.Round(m.MeanAbsoluteError, 2),
                // Границы Absent — лишь лучший отвергнутый кандидат; как наблюдённый объект они не показываются.
                bounds = m.Status is GameMatchStatus.Unique or GameMatchStatus.Ambiguous ? m.Bounds : null,
                safeTapBounds = m.SafeTapBounds,
                reason = m.Reason,
            }),
            association = record is null ? "capture_not_observed" : "exact_capture_provenance",
        }, record?.Reference, observation.Frame.FrameId);
    }

    private void TapCompleted(GameTap tap, AgentDebugFrameRecord? record, ApplicationResult<AndroidCommandOutcome> result, double elapsed)
    {
        run.Metrics.Add("tap", elapsed);
        string kind;
        if (result.IsSuccess && result.Value!.ExitCode == 0)
        {
            kind = "tap_completed";
            Mark(record, new(kind, tap.ActionId, "tap_completed", tap.SafeBounds, tap.Point));
        }
        else if (result.IsFailure && Reason(result.FailureInfo!) == "input_arguments_invalid")
        {
            // Адаптер отверг разрешение до запуска ADB: ввод не отправлялся.
            kind = "tap_not_issued";
            Mark(record, new(kind, tap.ActionId, "tap_not_issued", tap.SafeBounds, tap.Point));
        }
        else
        {
            kind = "tap_result_unproven";
            Mark(record, new(kind, tap.ActionId, "tap_result_unproven", tap.SafeBounds, tap.Point));
        }

        run.Trace.Record("gameui-host", kind, new
        {
            elapsedMs = elapsed,
            exitCode = result.IsSuccess ? result.Value!.ExitCode : (int?)null,
            failure = result.IsFailure ? AgentJson.Failure(result.FailureInfo) : null,
        }, record?.Reference, tap.Frame.FrameId, tap.ActionId);
    }

    private void Mark(AgentDebugFrameRecord? record, AgentDebugFrameMark mark)
    {
        if (record is not null)
        {
            run.Frames.Update(record, r => r.Marks.Add(mark));
        }
    }

    private static string Reason(ApplicationFailure failure)
    {
        return failure.Details?.TryGetValue("reason", out string? reason) == true ? reason : failure.Code;
    }
}

/// <summary>Принимает события решений Core flow и связывает их с кадрами по FrameId.</summary>
internal sealed class AgentDebugGameUiTrace(AgentDebugRun run) : IGameUiTrace
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _roles = new(StringComparer.Ordinal);

    public void Record(GameUiTraceEvent traceEvent)
    {
        run.Guard("gameui-flow", () => RecordCore(traceEvent));
    }

    private void RecordCore(GameUiTraceEvent traceEvent)
    {
        IReadOnlyDictionary<string, string>? roles = null;
        lock (_gate)
        {
            if (traceEvent.Kind == "action_started" && traceEvent.ActionId is string started && traceEvent.Details is { } details)
            {
                _roles[started] = details;
            }

            if (traceEvent.ActionId is string action)
            {
                roles = _roles.GetValueOrDefault(action);
            }
        }

        AgentDebugFrameRecord? record = traceEvent.FrameId is Guid id ? run.Frames.Find(id) : null;
        if (record is not null)
        {
            run.Frames.Update(record, r => Apply(r, traceEvent, roles));
        }

        if (traceEvent.Kind == "wait" && traceEvent.Value is double wait)
        {
            run.Metrics.Add("wait", wait);
        }

        run.Trace.Record("gameui-flow", traceEvent.Kind, new
        {
            asset = traceEvent.AssetId,
            reason = traceEvent.Reason,
            state = traceEvent.State,
            region = traceEvent.Region,
            point = traceEvent.Point,
            value = traceEvent.Value,
            details = traceEvent.Details,
        }, record?.Reference, traceEvent.FrameId, traceEvent.ActionId, traceEvent.PhaseId);
    }

    private static void Apply(AgentDebugFrameRecord record, GameUiTraceEvent traceEvent, IReadOnlyDictionary<string, string>? roles)
    {
        switch (traceEvent.Kind)
        {
            case "frame_classified":
                record.State = traceEvent.State;
                record.StateRule = traceEvent.PhaseId;
                break;
            case "action_source":
                if (traceEvent.ActionId is not null)
                {
                    record.Marks.Add(new("action_source", traceEvent.ActionId,
                        traceEvent.Reason == "reused_classified_frame" ? "source=reused_classified_frame" : "source=frame"));
                }

                break;
            case "tap_planned":
                record.Marks.Add(new("tap_planned", traceEvent.ActionId, "tap_planned", traceEvent.Region, traceEvent.Point));
                break;
            case "tap_permitted":
                record.Marks.Add(new("tap_permitted", traceEvent.ActionId, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"tap_permitted age={traceEvent.Value:0}ms"), traceEvent.Region, traceEvent.Point));
                break;
            case "postcondition_frame":
                record.Marks.Add(new("postcondition", traceEvent.ActionId,
                    traceEvent.Reason == "proven" ? "postcondition=proven" : "postcondition=unchanged"));
                break;
            case "action_rejected":
                record.Marks.Add(new("action_rejected", traceEvent.ActionId, "rejected=" + traceEvent.Reason + " state=" + traceEvent.State));
                break;
            default:
                break;
        }

        // Роли ассетов действия (source/required/excluded/postconditions) показывают его guards на кадре.
        if (traceEvent.ActionId is string action && roles is not null)
        {
            _ = record.Actions.TryAdd(action, roles);
        }
    }
}

/// <summary>
/// Область единственного разрешённого tap текущего асинхронного потока: только внутри неё запуск процесса ввода
/// получает кадр и действие. Область расходует одну стадию issued, поэтому второй процесс не наследует связь.
/// </summary>
internal static class AgentDebugTapContext
{
    private static readonly AsyncLocal<Scope?> Holder = new();

    internal static Scope? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }

    internal sealed class Scope(GameTap tap, string? reference)
    {
        private int _issued;

        internal GameTap Tap { get; } = tap;
        internal string? Reference { get; } = reference;

        internal bool TryClaimIssued()
        {
            return Interlocked.Exchange(ref _issued, 1) == 0;
        }
    }
}

/// <summary>Наблюдает каждый реальный запуск внешнего процесса без сырых stdout/stderr и без повторов.</summary>
internal sealed class AgentDebugProcessRunner(IWindowsProcessRunner inner, AgentDebugRun run, string owner) : IWindowsProcessRunner
{
    private const int MaximumArguments = 16;

    public async Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken)
    {
        string tool = Path.GetFileName(request.ExecutablePath);
        long started = Stopwatch.GetTimestamp();
        run.Guard("process", () =>
        {
            run.Trace.Record("process", "process_started", new
            {
                owner,
                tool = AgentJson.Safe(tool),
                arguments = request.Arguments.Take(MaximumArguments).Select(a => AgentJson.Safe(a.Length > 96 ? a[..96] : a)).ToArray(),
                argumentCount = request.Arguments.Count,
                mode = request.StandardOutputMode,
                timeoutMs = Math.Round(request.Timeout.TotalMilliseconds),
            });
        });

        ApplicationResult<WindowsProcessOutcome> result;
        try
        {
            result = await inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            run.Guard("process", () => run.Trace.Record("process", "process_failed",
                new { owner, tool = AgentJson.Safe(tool), exception = exception.GetType().Name }));
            throw;
        }

        run.Guard("process", () =>
        {
            if (IsTapCommand(request))
            {
                TapIssued(result);
            }

            Completed(tool, result, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
        });
        return result;
    }

    private static bool IsTapCommand(WindowsProcessRequest request)
    {
        return request.Arguments.Contains("input", StringComparer.Ordinal) && request.Arguments.Contains("tap", StringComparer.Ordinal);
    }

    /// <summary>
    /// Стадия issued фиксируется только после того, как процесс ввода действительно запущен и вернул итог: отказ
    /// запуска или проекции процесса её не порождает. Связь с кадром и действием берётся из области ввода
    /// единственного разрешения; без неё или при повторном запуске внутри той же области связь не выдумывается.
    /// Координаты и прочие аргументы процесса не записываются.
    /// </summary>
    private void TapIssued(ApplicationResult<WindowsProcessOutcome> result)
    {
        if (result.IsFailure)
        {
            return;
        }

        int exitCode = result.Value!.ExitCode;
        AgentDebugTapContext.Scope? scope = AgentDebugTapContext.Current;
        if (scope is not null && scope.TryClaimIssued())
        {
            run.Trace.Record("process", "tap_issued", new { owner, exitCode, asset = scope.Tap.AssetId, correlation = "tap_permit" },
                scope.Reference, scope.Tap.Frame.FrameId, scope.Tap.ActionId);
            return;
        }

        run.Trace.Record("process", "tap_issued", new { owner, exitCode, correlation = "none" });
    }

    private void Completed(string tool, ApplicationResult<WindowsProcessOutcome> result, double elapsed)
    {
        run.Metrics.Add("process:" + owner, elapsed);
        if (result.IsFailure)
        {
            run.Trace.Record("process", "process_failed", new { owner, tool = AgentJson.Safe(tool), elapsedMs = elapsed, failure = AgentJson.Failure(result.FailureInfo) });
            return;
        }

        WindowsProcessOutcome outcome = result.Value!;
        run.Trace.Record("process", "process_completed", new
        {
            owner,
            tool = AgentJson.Safe(tool),
            exitCode = outcome.ExitCode,
            elapsedMs = elapsed,
            processMs = Math.Round(outcome.Duration.TotalMilliseconds, 1),
            stdoutBytes = outcome.Mode == WindowsProcessStandardOutputMode.Binary ? outcome.StandardOutputBytesRead : (long?)null,
            stdoutChars = outcome.Mode == WindowsProcessStandardOutputMode.Text ? outcome.StandardOutput.Length : (int?)null,
            stderrChars = outcome.StandardError.Length,
            truncated = outcome.StandardOutputTruncated || outcome.StandardErrorTruncated || outcome.StandardOutputBytesTruncated,
        });
    }
}

/// <summary>Переносит существующие structured logs проекта в trace без сырых внешних payload.</summary>
internal sealed class AgentDebugLoggerProvider(AgentDebugRun run) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new Logger(run, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class Logger(AgentDebugRun run, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Ошибка учёта лога не должна дойти до LoggerFactory и production-вызова.
            run.Guard("log", () => Record(logLevel, eventId, state, exception, formatter));
        }

        private void Record<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, string> properties = new(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> pair in values.Where(p => p.Key != "{OriginalFormat}").Take(12))
                {
                    properties[AgentJson.Safe(pair.Key)] = AgentJson.Safe(Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            string message = formatter(state, exception);
            run.Trace.Record("log", "log", new
            {
                category = AgentJson.Safe(category),
                level = logLevel,
                eventId = eventId.Id,
                eventName = eventId.Name,
                message = AgentJson.Safe(message),
                properties = properties.Count == 0 ? null : properties,
                exception = exception?.GetType().Name,
            });
        }
    }
}

/// <summary>Записывает шаги AzurPilotOperation этого прогона (по его trace id), включая аренду mutation.</summary>
internal sealed class AgentDebugActivityListener : IDisposable
{
    private readonly ActivityListener _listener;

    internal AgentDebugActivityListener(AgentDebugRun run, string traceId)
    {
        _listener = new()
        {
            ShouldListenTo = static source => source.Name == AzurPilotOperation.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => run.Guard("activity", () =>
            {
                if (activity.TraceId.ToHexString() == traceId)
                {
                    run.Trace.Record("activity", "activity_started", new { name = activity.OperationName });
                }
            }),
            ActivityStopped = activity => run.Guard("activity", () =>
            {
                if (activity.TraceId.ToHexString() != traceId)
                {
                    return;
                }

                double duration = Math.Round(activity.Duration.TotalMilliseconds, 1);
                run.Trace.Record("activity", "activity_stopped", new
                {
                    name = activity.OperationName,
                    durationMs = duration,
                    status = activity.Status,
                    tags = activity.TagObjects.Take(8).ToDictionary(t => AgentJson.Safe(t.Key),
                        t => AgentJson.Safe(Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture)), StringComparer.Ordinal),
                    events = activity.Events.Take(8).Select(e => new
                    {
                        name = e.Name,
                        atMs = Math.Round((e.Timestamp - activity.StartTimeUtc).TotalMilliseconds, 1),
                    }).ToArray(),
                });
            }),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
