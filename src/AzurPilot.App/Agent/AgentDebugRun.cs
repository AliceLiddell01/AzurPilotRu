using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.App.Agent;

/// <summary>Агрегат длительностей одного вида работы; хранит только сумму, число и максимум.</summary>
internal sealed record AgentDebugMetric(int Count, double TotalMs, double MaxMs)
{
    public double MeanMs => Count == 0 ? 0 : Math.Round(TotalMs / Count, 1);
}

/// <summary>Потокобезопасные агрегаты фактических длительностей production-вызовов.</summary>
internal sealed class AgentDebugMetrics
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, AgentDebugMetric> _values = new(StringComparer.Ordinal);

    internal void Add(string name, double milliseconds)
    {
        lock (_gate)
        {
            AgentDebugMetric current = _values.GetValueOrDefault(name) ?? new(0, 0, 0);
            _values[name] = new(current.Count + 1, Math.Round(current.TotalMs + milliseconds, 1), Math.Max(current.MaxMs, milliseconds));
        }
    }

    internal IReadOnlyDictionary<string, AgentDebugMetric> Snapshot()
    {
        lock (_gate)
        {
            return new SortedDictionary<string, AgentDebugMetric>(_values, StringComparer.Ordinal);
        }
    }
}

/// <summary>Итог записи отчёта: относительный путь и полнота диагностики, независимые от результата команды.</summary>
internal sealed record AgentDebugCompletion(string RelativeRunDirectory, string Status, IReadOnlyList<string> IncompleteReasons);

/// <summary>
/// Один debug-прогон Agent CLI: каталог отчёта, trace, кадры и итоговые файлы. Создаётся только по явному
/// <c>--debug-report</c>; без него ни один файл не пишется. Диагностика наблюдает production и не может
/// изменить envelope, effect, разрешения или повторить действие.
/// </summary>
internal sealed class AgentDebugRun : IDisposable
{
    internal const int FormatVersion = 1;
    internal const int MaximumSessionLength = 64;
    internal const string ArgumentName = "--debug-report";
    internal const string ReportsDirectory = ".codex/local/reports";
    internal const string SessionIndexFile = "runs.jsonl";
    internal const long MaximumSessionBytes = 2L * 1024 * 1024 * 1024;
    internal const string SessionPattern = "^(?!(con|prn|aux|nul|com[0-9]|lpt[0-9])$)[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$(?![\\s\\S])";
    private const int MaximumSessionFiles = 20_000;
    private static readonly string[] ReservedNames = ["con", "prn", "aux", "nul"];
    private static readonly string[] HashedFiles = ["AzurPilot.App.dll", "AzurPilot.Core.dll", "AzurPilot.Windows.dll", "AzurPilot.Native.dll"];

    private readonly string _checkoutRoot;
    private readonly string _manifestPath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly int _timeoutMilliseconds;
    private readonly IReadOnlyList<string> _relatedRuns;
    private readonly AgentDebugActivityListener _activities;
    private readonly long _startedTicks = Stopwatch.GetTimestamp();

    private AgentDebugRun(string checkoutRoot, string sessionDirectory, string runId, string session, string correlationId,
        string operation, string? requestedTarget, IReadOnlyList<string> arguments, int timeoutMilliseconds,
        TimeProvider clock, string? framesDisabledReason, IReadOnlyList<string> relatedRuns)
    {
        _checkoutRoot = checkoutRoot;
        SessionDirectory = sessionDirectory;
        RunId = runId;
        Session = session;
        CorrelationId = correlationId;
        Operation = operation;
        RequestedTarget = requestedTarget;
        _arguments = arguments;
        _timeoutMilliseconds = timeoutMilliseconds;
        _relatedRuns = relatedRuns;
        Clock = clock;
        Origin = clock.GetTimestamp();
        StartedAtUtc = clock.GetUtcNow().ToUniversalTime();
        RunDirectory = Path.Combine(sessionDirectory, runId);
        RelativeRunDirectory = ReportsDirectory + "/" + session + "/" + runId;
        string frames = Path.Combine(RunDirectory, "frames");
        _ = Directory.CreateDirectory(frames);
        _manifestPath = Path.Combine(RunDirectory, "manifest.json");
        Trace = new(Path.Combine(RunDirectory, "trace.jsonl"), clock, Origin);
        Frames = new(this, frames, framesDisabledReason);
        GameUiTrace = new(this);
        _activities = new(this, correlationId);
    }

    internal TimeProvider Clock { get; }
    internal long Origin { get; }
    internal DateTimeOffset StartedAtUtc { get; }
    internal string Session { get; }
    internal string RunId { get; }
    internal string SessionDirectory { get; }
    internal string RunDirectory { get; }
    internal string RelativeRunDirectory { get; }
    internal string CorrelationId { get; }
    internal string Operation { get; }
    internal string? RequestedTarget { get; }
    internal string? ResolvedTarget { get; set; }
    internal AgentDebugTrace Trace { get; }
    internal AgentDebugFrames Frames { get; }
    internal AgentDebugGameUiTrace GameUiTrace { get; }
    internal AgentDebugMetrics Metrics { get; } = new();

    private int _diagnosticFailures;

    /// <summary>Число сбоев собственного учёта debug, не повлиявших на production-результат.</summary>
    internal int DiagnosticFailures => Volatile.Read(ref _diagnosticFailures);

    /// <summary>
    /// Выполняет учёт debug так, что его ошибка не меняет результат production-вызова: сбой считается и
    /// становится причиной неполноты отчёта.
    /// </summary>
    internal void Guard(string source, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = Interlocked.Increment(ref _diagnosticFailures);
            Trace.Record("diagnostics", "diagnostic_failed", new { source, exception = exception.GetType().Name });
        }
    }

    /// <summary>Закрывает trace, listener и фоновые ресурсы; повторный вызов безопасен.</summary>
    public void Dispose()
    {
        _activities.Dispose();
        Trace.Close();
        Frames.Dispose();
    }

    /// <summary>Безопасный идентификатор сессии: строчные ASCII-буквы, цифры и дефис, без имён устройств Windows.</summary>
    internal static bool IsValidSession(string value)
    {
        return value.Length is > 0 and <= MaximumSessionLength && value[0] != '-' && value[^1] != '-'
            && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            && !ReservedNames.Contains(value, StringComparer.Ordinal)
            && !((value.StartsWith("com", StringComparison.Ordinal) || value.StartsWith("lpt", StringComparison.Ordinal))
                && value.Length == 4 && char.IsAsciiDigit(value[3]));
    }

    /// <summary>
    /// Создаёт каталог прогона под <c>.codex/local/reports/&lt;session&gt;</c> ближайшего checkout выше рабочего
    /// каталога. Отказ возвращается до загрузки runtime и не порождает никаких эффектов.
    /// </summary>
    internal static ApplicationResult<AgentDebugRun> Create(string session, string correlationId, string operation,
        string? requestedTarget, IReadOnlyList<string> arguments, int timeoutMilliseconds, TimeProvider clock,
        string? workingDirectory = null)
    {
        if (!IsValidSession(session))
        {
            return Unavailable("session_invalid");
        }

        string? root = FindCheckoutRoot(workingDirectory ?? Environment.CurrentDirectory);
        if (root is null)
        {
            return Unavailable("checkout_root_not_found");
        }

        try
        {
            string reports = Path.GetFullPath(Path.Combine(root, ".codex", "local", "reports"));
            string sessionDirectory = Path.GetFullPath(Path.Combine(reports, session));
            if (!sessionDirectory.StartsWith(reports + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Unavailable("session_path_escape");
            }

            // Ссылку нужно отвергнуть до создания каталогов: иначе они появятся за ignored boundary.
            string[] components = [Path.Combine(root, ".codex"), Path.Combine(root, ".codex", "local"), reports, sessionDirectory];
            foreach (string directory in components)
            {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    return Unavailable("report_path_reparse_point");
                }

                if (!Directory.Exists(directory))
                {
                    _ = Directory.CreateDirectory(directory);
                }
            }

            string runId = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-"
                + operation + "-" + correlationId[..12];
            if (Directory.Exists(Path.Combine(sessionDirectory, runId)))
            {
                return Unavailable("run_directory_exists");
            }

            (long sessionBytes, int sessionFiles) = MeasureSession(sessionDirectory);
            string? disabled = sessionBytes >= MaximumSessionBytes || sessionFiles >= MaximumSessionFiles ? "session_limit" : null;
            AgentDebugRun run = new(root, sessionDirectory, runId, session, correlationId, operation, requestedTarget,
                arguments, timeoutMilliseconds, clock, disabled, ReadRelatedRuns(sessionDirectory));
            try
            {
                run.WriteManifest(null, null, ["run_in_progress"], null, FileMode.CreateNew);
            }
            catch
            {
                // Отказ начального manifest не должен оставлять открытыми trace и listener, фоновую запись
                // кадров и частично созданный каталог прогона: операция не запускалась и эффектов нет.
                run._activities.Dispose();
                run.Trace.Close();
                run.Frames.CompleteAsync().GetAwaiter().GetResult();
                run.Frames.Dispose();
                try
                {
                    Directory.Delete(run.RunDirectory, recursive: true);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Исходный отказ важнее: остаток каталога не маскирует причину.
                }

                throw;
            }

            run.Trace.Record("agent", "run_started", new
            {
                operation,
                requestedTarget,
                arguments,
                timeoutMs = timeoutMilliseconds,
                session,
                sessionBytes,
                framesDisabled = disabled,
            });
            return ApplicationResult<AgentDebugRun>.Success(run);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unavailable("report_directory_unwritable");
        }
    }

    /// <summary>
    /// Запечатывает кадры, ограниченно ждёт фоновой записи и пишет итоговые файлы. Ошибки записи остаются
    /// причинами неполноты; envelope команды не изменяется, а в отчёт попадает его итоговая форма после ограничения вывода.
    /// </summary>
    internal async Task<AgentDebugCompletion> CompleteAsync(AgentEnvelope envelope)
    {
        long completionStarted = Stopwatch.GetTimestamp();
        List<string> reasons = [];
        int outputBytes = 0;
        try
        {
            AgentEnvelope copy = envelope;
            outputBytes = AgentJson.Serialize(ref copy).Length;
            // Отчёт фиксирует тот же envelope, что увидит stdout, включая замену при превышении лимита вывода.
            envelope = copy;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or JsonException)
        {
            reasons.Add("envelope_size_unknown");
        }

        Trace.Record("agent", "envelope_ready", new
        {
            envelope.Status,
            envelope.ExitCode,
            envelope.Effect,
            failure = envelope.Failure,
            outputBytes,
        });
        _activities.Dispose();
        await Frames.CompleteAsync().ConfigureAwait(false);
        AgentDebugFrameCounters counters = Frames.Counters();
        if (counters.Rendered < counters.Captured)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"frames_missing:{counters.Captured - counters.Rendered}/{counters.Captured}"));
        }

        if (counters.LateEvents > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"late_annotation_events:{counters.LateEvents}"));
        }

        if (DiagnosticFailures > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"diagnostic_failures:{DiagnosticFailures}"));
        }

        if (counters.DisabledReason is string disabled)
        {
            reasons.Add("frames_disabled:" + disabled);
        }

        double finalizeMs = Math.Round(Stopwatch.GetElapsedTime(completionStarted).TotalMilliseconds, 1);
        Trace.Record("diagnostics", "frames_flushed", new { counters.Rendered, counters.Captured, finalizeMs });
        Trace.Close();
        if (Trace.Dropped > 0 || Trace.Failure is not null)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"trace_incomplete:{Trace.Failure}:{Trace.Dropped}"));
        }

        DateTimeOffset completed = Clock.GetUtcNow().ToUniversalTime();
        try
        {
            WriteJson(Path.Combine(RunDirectory, "performance.json"), Performance(counters, finalizeMs));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reasons.Add("performance_write_failed");
        }

        try
        {
            await File.WriteAllTextAsync(Path.Combine(RunDirectory, "summary.md"),
                Summary(envelope, counters, reasons, completed), new UTF8Encoding(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reasons.Add("summary_write_failed");
        }

        string status = reasons.Count == 0 ? "complete" : "incomplete";
        try
        {
            WriteManifest(envelope, counters, reasons, completed, FileMode.Create);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reasons.Add("manifest_write_failed");
            status = "incomplete";
        }

        if (!await AppendSessionIndexAsync(envelope, counters, status, completed).ConfigureAwait(false))
        {
            // Строка индекса, на которую опирается приёмка, не записана: отчёт нельзя считать полным.
            reasons.Add("session_index_write_failed");
            status = "incomplete";
            try
            {
                await File.WriteAllTextAsync(Path.Combine(RunDirectory, "summary.md"),
                    Summary(envelope, counters, reasons, completed), new UTF8Encoding(false)).ConfigureAwait(false);
                WriteManifest(envelope, counters, reasons, completed, FileMode.Create);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Причина уже возвращается вызывающему в результате; прежние файлы отчёта остаются как были.
            }
        }

        return new(RelativeRunDirectory, status, reasons);
    }

    private static ApplicationResult<AgentDebugRun> Unavailable(string reason)
    {
        return ApplicationResult<AgentDebugRun>.Failure(new()
        {
            Code = ApplicationFailure.DebugReportUnavailable,
            Message = "Debug-отчёт Agent CLI нельзя создать; операция не запускалась.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                [ApplicationFailure.MutationAttemptedDetailKey] = "false",
            },
        });
    }

    private static string? FindCheckoutRoot(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static (long Bytes, int Files) MeasureSession(string sessionDirectory)
    {
        long bytes = 0;
        int files = 0;
        foreach (FileInfo file in new DirectoryInfo(sessionDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            bytes += file.Length;
            if (++files >= MaximumSessionFiles)
            {
                break;
            }
        }

        return (bytes, files);
    }

    private static List<string> ReadRelatedRuns(string sessionDirectory)
    {
        string index = Path.Combine(sessionDirectory, SessionIndexFile);
        if (!File.Exists(index))
        {
            return [];
        }

        List<string> runs = [];
        try
        {
            using FileStream stream = new(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.UTF8);
            while (reader.ReadLine() is string line && runs.Count < 512)
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("runId", out JsonElement id) && id.GetString() is string runId)
                    {
                        runs.Add(runId);
                    }
                }
                catch (JsonException)
                {
                    runs.Add("unreadable_index_line");
                }
            }
        }
        catch (IOException)
        {
            runs.Add("index_unreadable");
        }

        return runs;
    }

    private async Task<bool> AppendSessionIndexAsync(AgentEnvelope envelope, AgentDebugFrameCounters counters, string status, DateTimeOffset completed)
    {
        byte[] line = [.. JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId = RunId,
            correlationId = CorrelationId,
            operation = Operation,
            requestedTarget = RequestedTarget,
            target = envelope.Target,
            startedAtUtc = StartedAtUtc,
            completedAtUtc = completed,
            commandStatus = envelope.Status,
            exitCode = envelope.ExitCode,
            effect = envelope.Effect,
            diagnostics = status,
            capturedFrames = counters.Captured,
            renderedFrames = counters.Rendered,
            path = RelativeRunDirectory,
        }, AgentDebugTrace.Options), (byte)'\n'];
        string lockPath = Path.Combine(SessionDirectory, "runs.lock");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                // Параллельные прогоны одной сессии добавляют строки по очереди под эксклюзивным открытием.
                using FileStream gate = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using FileStream index = new(Path.Combine(SessionDirectory, SessionIndexFile), FileMode.Append, FileAccess.Write, FileShare.Read);
                index.Write(line);
                index.Flush(flushToDisk: true);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private object Performance(AgentDebugFrameCounters counters, double finalizeMs)
    {
        IReadOnlyList<AgentDebugFrameRecord> frames = Frames.Snapshot();
        IReadOnlyDictionary<string, AgentDebugMetric> metrics = Metrics.Snapshot();
        return new
        {
            formatVersion = FormatVersion,
            runId = RunId,
            nesting = "observe включает capture, распознавание и создание превью; capture включает ADB-команду и decode/проверку; "
                + "превью — диагностическая работа внутри observe, но вне capture и match; рендер и запись JPEG выполняются в фоне.",
            production = new
            {
                runMs = Math.Round(Stopwatch.GetElapsedTime(_startedTicks).TotalMilliseconds - finalizeMs, 1),
                captures = new
                {
                    count = counters.Captured,
                    failures = counters.CaptureFailures,
                    totalMs = Math.Round(frames.Sum(f => f.CaptureMs), 1),
                    maxMs = frames.Count == 0 ? 0 : frames.Max(f => f.CaptureMs),
                    adbCommandMs = Math.Round(frames.Sum(f => f.CommandMs), 1),
                    decodeAndValidateMs = Math.Round(frames.Sum(f => Math.Max(0, f.CaptureMs - f.CommandMs)), 1),
                },
                perCapture = frames.Select(f => new
                {
                    sequence = f.Sequence,
                    frame = f.Reference,
                    frameId = f.FrameId?.ToString("N"),
                    capturedAtMs = f.CapturedAtMs,
                    captureMs = f.CaptureMs,
                    adbCommandMs = f.CommandMs,
                    decodeAndValidateMs = Math.Round(Math.Max(0, f.CaptureMs - f.CommandMs), 1),
                    pngBytes = f.PngBytes,
                    previewMs = f.PreviewMs,
                }),
                calls = metrics,
            },
            diagnostics = new
            {
                criticalPath = new
                {
                    previewCount = counters.Previews,
                    previewMs = counters.PreviewMs,
                    previewMaxMs = counters.PreviewMaxMs,
                    traceWriteMs = Trace.WriteMs,
                    note = "Превью создаётся синхронно до освобождения кадра; запись trace — синхронная передача строки ОС.",
                },
                background = new
                {
                    renderMs = counters.RenderMs,
                    writeMs = counters.WriteMs,
                    queueWaitMs = counters.QueueWaitMs,
                },
                finalizeMs,
                traceEvents = Trace.Events,
                traceBytes = Trace.Bytes,
                frames = new { counters.Captured, counters.Rendered, counters.Skipped, counters.Failed, counters.Dropped },
                renderedBytes = counters.RenderedBytes,
            },
        };
    }

    private void WriteManifest(AgentEnvelope? envelope, AgentDebugFrameCounters? counters, List<string> reasons,
        DateTimeOffset? completed, FileMode mode)
    {
        IReadOnlyList<AgentDebugFrameRecord> frames = counters is null ? [] : Frames.Snapshot();
        object manifest = new
        {
            formatVersion = FormatVersion,
            state = completed is null ? "running" : "completed",
            diagnostics = new { status = completed is null ? "incomplete" : reasons.Count == 0 ? "complete" : "incomplete", incompleteReasons = reasons },
            run = new
            {
                id = RunId,
                session = Session,
                correlationId = CorrelationId,
                operation = Operation,
                requestedTarget = RequestedTarget,
                resolvedTarget = ResolvedTarget,
                arguments = _arguments,
                timeoutMs = _timeoutMilliseconds,
                startedAtUtc = StartedAtUtc,
                completedAtUtc = completed,
            },
            command = envelope is null ? null : new
            {
                envelope.Status,
                envelope.ExitCode,
                envelope.Effect,
                failure = envelope.Failure,
                note = "Результат команды не является доказательством UI, cleanup или полноты диагностики.",
            },
            checkout = completed is null ? null : Checkout(),
            build = completed is null ? null : Build(),
            settings = new
            {
                previewWidth = AgentDebugFrames.PreviewWidth,
                previewHeight = AgentDebugFrames.PreviewHeight,
                jpegQuality = AgentDebugFrames.JpegQuality,
                queueCapacity = AgentDebugFrames.QueueCapacity,
                maximumFrames = AgentDebugFrames.MaximumFrames,
                maximumRunBytes = AgentDebugFrames.MaximumRunBytes,
                minimumFreeBytes = AgentDebugFrames.MinimumFreeBytes,
                maximumSessionBytes = MaximumSessionBytes,
                maximumTraceEvents = AgentDebugTrace.MaximumEvents,
                maximumTraceBytes = AgentDebugTrace.MaximumBytes,
                flushTimeoutMs = AgentDebugFrames.FlushTimeout.TotalMilliseconds,
                masking = "none",
                retention = "manual",
            },
            counters = counters is null ? null : new
            {
                counters.Captured,
                counters.Rendered,
                counters.Skipped,
                counters.Failed,
                counters.Dropped,
                counters.CaptureFailures,
                counters.SkippedReasons,
                counters.FailedReasons,
                counters.DroppedReasons,
                lateAnnotationEvents = counters.LateEvents,
                traceEvents = Trace.Events,
                traceDropped = Trace.Dropped,
                traceFailure = Trace.Failure,
            },
            frames = frames.Select(f => new
            {
                sequence = f.Sequence,
                frame = f.Reference,
                frameId = f.FrameId?.ToString("N"),
                f.Status,
                f.Reason,
                f.File,
                bytes = f.FileBytes == 0 ? (long?)null : f.FileBytes,
                f.Sha256,
                f.Width,
                f.Height,
                captureStartedAtUtc = f.CaptureStartedAtUtc,
                f.State,
                f.StateRule,
                f.Viewport,
                actions = f.Actions.Keys,
                marks = f.Marks.Select(m => m.Kind),
            }),
            files = completed is null ? null : Files(),
            relatedRuns = _relatedRuns,
        };
        string temporary = _manifestPath + ".tmp";
        WriteJson(mode == FileMode.CreateNew ? _manifestPath : temporary, manifest, mode);
        if (mode != FileMode.CreateNew)
        {
            File.Move(temporary, _manifestPath, overwrite: true);
        }
    }

    private IEnumerable<object> Files()
    {
        return [.. new DirectoryInfo(RunDirectory).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => new { path = Path.GetRelativePath(RunDirectory, f.FullName).Replace('\\', '/'), bytes = f.Length })];
    }

    private object Checkout()
    {
        (string? head, string? branch) = GitHead(_checkoutRoot);
        return new
        {
            gitHead = head,
            gitBranch = branch,
            source = head is null ? "not_read" : "git_metadata",
            dirty = (bool?)null,
            note = "Dirty state приложением не проверяется; фактический HEAD/diff фиксирует live-testing.",
        };
    }

    private static object Build()
    {
        string baseDirectory = AppContext.BaseDirectory;
        uint? abi = null;
        try
        {
            abi = AzurPilotNativeBridge.NativeAbiVersion();
        }
        catch (AzurPilotNativeBoundaryException)
        {
        }

        string? process = Environment.ProcessPath;
        IEnumerable<string> candidates = process is null ? HashedFiles : HashedFiles.Prepend(Path.GetFileName(process));
        return new
        {
            appVersion = typeof(AgentDebugRun).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            process = process is null ? null : Path.GetFileName(process),
            nativeAbiVersion = abi,
            files = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
            {
                string path = name == Path.GetFileName(process) && process is not null ? process : Path.Combine(baseDirectory, name);
                if (!File.Exists(path))
                {
                    return (object)new { name, sha256 = (string?)null, bytes = (long?)null };
                }

                using FileStream stream = File.OpenRead(path);
                return new { name, sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)), bytes = (long?)stream.Length };
            }).ToArray(),
        };
    }

    private static (string? Head, string? Branch) GitHead(string root)
    {
        try
        {
            string gitDirectory = Path.Combine(root, ".git");
            if (File.Exists(gitDirectory))
            {
                string pointer = File.ReadAllText(gitDirectory).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return (null, null);
                }

                gitDirectory = Path.GetFullPath(Path.Combine(root, pointer["gitdir:".Length..].Trim()));
            }

            string common = File.Exists(Path.Combine(gitDirectory, "commondir"))
                ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(Path.Combine(gitDirectory, "commondir")).Trim()))
                : gitDirectory;
            string head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return (IsSha(head) ? head : null, null);
            }

            string reference = head[5..];
            string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
            foreach (string directory in new[] { gitDirectory, common })
            {
                string file = Path.Combine(directory, reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file) && File.ReadAllText(file).Trim() is string sha && IsSha(sha))
                {
                    return (sha, branch);
                }
            }

            string packed = Path.Combine(common, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (string line in File.ReadLines(packed))
                {
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal) && IsSha(line[..40]))
                    {
                        return (line[..40], branch);
                    }
                }
            }

            return (null, branch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static bool IsSha(string value)
    {
        return value.Length == 40 && value.All(char.IsAsciiHexDigit);
    }

    private static void WriteJson(string path, object value, FileMode mode = FileMode.Create)
    {
        using FileStream stream = new(path, mode, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, AgentDebugTrace.Options);
    }

    private string Summary(AgentEnvelope envelope, AgentDebugFrameCounters counters, List<string> reasons, DateTimeOffset completed)
    {
        IReadOnlyList<AgentDebugFrameRecord> frames = Frames.Snapshot();
        StringBuilder text = new();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"# Debug-отчёт Agent CLI: `{Operation}`").AppendLine();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Прогон: `{RunId}`, сессия `{Session}`, correlationId `{CorrelationId}`");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Время: {StartedAtUtc:O} → {completed:O}");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Команда: `agent {string.Join(' ', _arguments)}`");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Target: запрошен `{RequestedTarget ?? "по настройкам"}`, разрешён `{envelope.Target ?? "не разрешён"}`");
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Каталог: `{RelativeRunDirectory}`").AppendLine();
        _ = text.AppendLine("## Результат команды").AppendLine();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"`status={envelope.Status}`, `exitCode={envelope.ExitCode}`, `effect={envelope.Effect}`, "
            + $"`failure={FailureCode(envelope.Failure) ?? "null"}`.");
        _ = text.AppendLine("Успешный envelope не доказывает игровой UI, конечное состояние среды или cleanup; это делает отдельная приёмка.").AppendLine();
        _ = text.AppendLine(CultureInfo.InvariantCulture, $"## Диагностика: {(reasons.Count == 0 ? "COMPLETE" : "INCOMPLETE")}").AppendLine();
        foreach (string reason in reasons)
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"- {reason}");
        }

        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Кадры: захвачено {counters.Captured}, JPEG {counters.Rendered}, пропущено {counters.Skipped}, "
            + $"ошибок {counters.Failed}, потеряно {counters.Dropped}; отказов захвата без изображения {counters.CaptureFailures}.");
        foreach ((string reason, int count) in counters.SkippedReasons.Concat(counters.FailedReasons).Concat(counters.DroppedReasons))
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  - `{reason}`: {count}");
        }

        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Trace: событий {Trace.Events}, потеряно {Trace.Dropped}.").AppendLine();
        if (frames.Count > 0)
        {
            _ = text.AppendLine("## Кадры").AppendLine();
            _ = text.AppendLine("| # | frameId | состояние | viewport | действия/отметки | файл |");
            _ = text.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (AgentDebugFrameRecord frame in frames)
            {
                string marks = string.Join(", ", frame.Actions.Keys.Concat(frame.Marks.Select(m => m.Kind).Distinct(StringComparer.Ordinal)));
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"| {frame.Sequence} | `{frame.FrameId?.ToString("N")[..8] ?? "-"}` | "
                    + $"{frame.StateRule ?? "-"}/{frame.State ?? "-"} | {frame.Viewport ?? "-"} | {(marks.Length == 0 ? "-" : marks)} | "
                    + $"{(frame.File is null ? frame.Status + ":" + frame.Reason : "`" + frame.File + "`")} |");
            }

            _ = text.AppendLine();
        }

        IReadOnlyList<string> notObserved = NotObservedPhases(envelope.Data);
        _ = text.AppendLine("## Невстреченные фазы (NOT_TESTED)").AppendLine();
        _ = text.AppendLine(notObserved.Count == 0 ? "- нет сведений о невстреченных фазах в ответе команды"
            : string.Join(Environment.NewLine, notObserved.Select(p => "- `" + p + "`")));
        _ = text.AppendLine();
        _ = text.AppendLine("## Тайминги").AppendLine();
        foreach ((string name, AgentDebugMetric metric) in Metrics.Snapshot())
        {
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"- `{name}`: {metric.Count} шт., всего {metric.TotalMs} ms, среднее {metric.MeanMs} ms, максимум {metric.MaxMs} ms");
        }

        _ = text.AppendLine(CultureInfo.InvariantCulture, $"- Диагностика в критическом пути: превью {counters.PreviewMs} ms ({counters.Previews} шт.), trace {Trace.WriteMs} ms; "
            + $"фон: рендер {counters.RenderMs} ms, запись {counters.WriteMs} ms. Подробно — `performance.json`.").AppendLine();
        _ = text.AppendLine("## Ограничения").AppendLine();
        _ = text.AppendLine("- JPEG логического размера GameGeometry — иллюстрация реального кадра; распознавание и ввод использовали исходный кадр полного разрешения.");
        _ = text.AppendLine("- Рамки показывают только Unique/Ambiguous; Absent и недоказанные признаки перечислены подписью.");
        _ = text.AppendLine("- Маскирование не выполнялось: файлы остаются локальными и не публикуются.");
        _ = text.AppendLine("- Вердикт PASS/FAIL/PARTIAL/NOT_RUN/BLOCKED ставит live-приёмка, а не этот отчёт.");
        return text.ToString();
    }

    private static string? FailureCode(object? failure)
    {
        if (failure is null)
        {
            return null;
        }

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(failure, AgentJson.Options));
        return json.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    // Фазы со status=NotObserved из ответа команды: невстреченное состояние не равно успешному прохождению.
    private static IReadOnlyList<string> NotObservedPhases(object? data)
    {
        if (data is null)
        {
            return [];
        }

        List<string> result = [];
        try
        {
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(data, AgentJson.Options));
            Visit(json.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ["phases_unreadable"];
        }

        return [.. result.Distinct(StringComparer.Ordinal).Take(64)];

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                    && status.GetString() == "NotObserved" && element.TryGetProperty("id", out JsonElement id))
                {
                    result.Add(id.GetString() ?? "-");
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }
}
