using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Game;

namespace AzurPilot.App.Agent;

/// <summary>Отметка решения или ввода, привязанная к кадру для аннотации.</summary>
internal sealed record AgentDebugFrameMark(string Kind, string? ActionId, string? Label,
    GamePixelRect? Region = null, GameInputPoint? Point = null);

/// <summary>Состояние одного реального захвата debug-прогона; пиксели хранит только native превью.</summary>
internal sealed class AgentDebugFrameRecord
{
    internal required int Sequence { get; init; }
    internal string Reference => "f" + Sequence.ToString("0000", CultureInfo.InvariantCulture);
    internal required MuMuInstanceId InstanceId { get; init; }
    internal required AndroidEndpoint Endpoint { get; init; }
    internal required long CaptureStartedTimestamp { get; init; }
    internal required DateTimeOffset CaptureStartedAtUtc { get; init; }
    internal required int Width { get; init; }
    internal required int Height { get; init; }
    internal required int PngBytes { get; init; }
    internal required double CommandMs { get; init; }
    internal string Method { get; init; } = "ADB";
    internal long PayloadBytes { get; init; }
    internal required double CaptureMs { get; init; }
    internal required double CapturedAtMs { get; init; }
    internal Guid? FrameId { get; set; }
    internal GameUiObservation? Observation { get; set; }
    internal string? State { get; set; }
    internal string? StateRule { get; set; }
    internal string? Viewport { get; set; }
    internal Dictionary<string, IReadOnlyDictionary<string, string>> Actions { get; } = new(StringComparer.Ordinal);
    internal List<AgentDebugFrameMark> Marks { get; } = [];
    internal NativeFrame? Preview { get; set; }
    internal double PreviewMs { get; set; }
    internal string Status { get; set; } = "captured";
    internal string? Reason { get; set; }
    internal string? File { get; set; }
    internal long FileBytes { get; set; }
    internal string? Sha256 { get; set; }
    internal double RenderMs { get; set; }
    internal double WriteMs { get; set; }
    internal double QueueWaitMs { get; set; }
    internal long EnqueuedTicks { get; set; }
    internal bool Sealed { get; set; }
    internal int LateEvents { get; set; }
}

/// <summary>
/// Кадры-свидетельства debug-прогона. Каждый успешный production capture получает запись; превью
/// логического размера GameGeometry создаётся из живого native frame непосредственно перед его освобождением, без повторного
/// захвата и без копирования 4K RGB в managed. Кадр «запечатывается» с началом следующего захвата или
/// в конце прогона, поэтому решения и ввод, принятые по нему позже capture, попадают на его же JPEG.
/// Кодирование и запись выполняет одна фоновая задача через ограниченную очередь.
/// </summary>
internal sealed class AgentDebugFrames : IAndroidScreenshotObserver, IDisposable
{
    internal const int PreviewWidth = GameGeometry.LogicalWidth;
    internal const int PreviewHeight = GameGeometry.LogicalHeight;
    // 82: Hershey-подписи и тонкие рамки остаются читаемыми, кадр обычно занимает 100–300 КБ.
    internal const int JpegQuality = 82;
    internal const int QueueCapacity = 8;
    internal const int MaximumFrames = 900;
    internal const long MaximumRunBytes = 256L * 1024 * 1024;
    internal const long MinimumFreeBytes = 1024L * 1024 * 1024;
    internal static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly AgentDebugRun _run;
    private readonly string _framesDirectory;
    private readonly List<AgentDebugFrameRecord> _records = [];
    private readonly Dictionary<Guid, AgentDebugFrameRecord> _byFrameId = [];
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dropped = new(StringComparer.Ordinal);
    private readonly Channel<AgentDebugFrameRecord> _queue = Channel.CreateBounded<AgentDebugFrameRecord>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private readonly string? _disabledReason;
    private readonly long _byteBudget;
    private int _captureFailures;
    private int _rendered;
    private long _renderedBytes;
    private long _previewTicks;
    private double _previewMaxMs;
    private int _previews;
    private long _renderTicks;
    private long _writeTicks;
    private long _queueTicks;
    private int _lateEvents;

    internal AgentDebugFrames(AgentDebugRun run, string framesDirectory, string? disabledReason, long byteBudget = MaximumRunBytes)
    {
        _run = run;
        _byteBudget = Math.Min(byteBudget, MaximumRunBytes);
        _framesDirectory = framesDirectory;
        _disabledReason = disabledReason;
        _writer = Task.Run(WriteFramesAsync);
    }

    /// <summary>Освобождает сигнал остановки после завершения прогона.</summary>
    public void Dispose()
    {
        _stop.Dispose();
    }

    internal IReadOnlyList<AgentDebugFrameRecord> Snapshot()
    {
        lock (_gate)
        {
            return [.. _records];
        }
    }

    internal AgentDebugFrameCounters Counters()
    {
        lock (_gate)
        {
            return new(_records.Count, _rendered, Sum(_skipped), Sum(_failed), Sum(_dropped), _captureFailures,
                new SortedDictionary<string, int>(_skipped, StringComparer.Ordinal),
                new SortedDictionary<string, int>(_failed, StringComparer.Ordinal),
                new SortedDictionary<string, int>(_dropped, StringComparer.Ordinal),
                _lateEvents, _renderedBytes, _previews, Milliseconds(_previewTicks), Math.Round(_previewMaxMs, 1),
                Milliseconds(_renderTicks), Milliseconds(_writeTicks), Milliseconds(_queueTicks), _disabledReason);
        }
    }

    /// <inheritdoc />
    public void CaptureStarted(MuMuInstanceId instanceId, AndroidEndpoint endpoint, long captureStartedTimestamp)
    {
        // Новый захват начинается после всех решений по предыдущему кадру этого последовательного flow.
        SealPending();
        _run.Trace.Record("android", "capture_started", new { instanceId = instanceId.ToString(), endpoint = endpoint.ToString() });
    }

    /// <inheritdoc />
    public void CaptureFailed(MuMuInstanceId instanceId, AndroidEndpoint endpoint, long captureStartedTimestamp,
        ApplicationFailure failure)
    {
        lock (_gate)
        {
            _captureFailures++;
        }

        _run.Trace.Record("android", "capture_failed", new
        {
            instanceId = instanceId.ToString(),
            commandStarted = captureStartedTimestamp != 0,
            failure = AgentJson.Failure(failure),
            image = "none",
        });
    }

    /// <inheritdoc />
    public void CaptureCompleted(AndroidScreenshotCaptureInfo info)
    {
        long now = _run.Clock.GetTimestamp();
        AgentDebugFrameRecord record;
        lock (_gate)
        {
            record = new()
            {
                Sequence = _records.Count + 1,
                InstanceId = info.InstanceId,
                Endpoint = info.Endpoint,
                CaptureStartedTimestamp = info.CaptureStartedTimestamp,
                CaptureStartedAtUtc = info.CaptureStartedAtUtc.ToUniversalTime(),
                Width = checked((int)info.FrameInfo.Width),
                Height = checked((int)info.FrameInfo.Height),
                PngBytes = info.AdbPng?.EncodedPngByteCount ?? 0,
                CommandMs = Math.Round(info.Timing.Acquisition.TotalMilliseconds, 1),
                Method = Core.Capture.ScreenshotMethodNames.ToName(info.Method),
                PayloadBytes = info.PayloadByteCount,
                CaptureMs = Math.Round(_run.Clock.GetElapsedTime(info.CaptureStartedTimestamp, now).TotalMilliseconds, 1),
                CapturedAtMs = _run.Trace.ElapsedMs(now),
            };
            _records.Add(record);
        }

        _run.Trace.Record("android", "capture_completed", new
        {
            instanceId = info.InstanceId.ToString(),
            endpoint = info.Endpoint.ToString(),
            width = record.Width,
            height = record.Height,
            format = "rgb8",
            method = record.Method,
            pngBytes = record.PngBytes,
            payloadBytes = record.PayloadBytes,
            captureStartedAtMs = _run.Trace.ElapsedMs(info.CaptureStartedTimestamp),
            commandMs = record.CommandMs,
            streamSequence = info.Stream?.Sequence,
            captureMs = record.CaptureMs,
            decodeAndValidateMs = Math.Round(Math.Max(0, record.CaptureMs - record.CommandMs), 1),
            captureStartedAtUtc = info.CaptureStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, record.Reference);
    }

    /// <inheritdoc />
    public void FrameReleasing(NativeFrame frame, AndroidScreenshotCaptureInfo info)
    {
        AgentDebugFrameRecord? record = Find(info.Endpoint, info.CaptureStartedTimestamp);
        if (record is null)
        {
            return;
        }

        string? skip = _disabledReason;
        if (skip is null)
        {
            try
            {
                _ = new GameGeometry(record.Width, record.Height);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Нестандартная геометрия не подгоняется под 16:9: изображения нет, отказ записан.
                skip = "unsupported_geometry";
            }
        }

        if (skip is not null)
        {
            lock (_gate)
            {
                record.Status = "skipped";
                record.Reason = skip;
            }

            return;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            NativeFrame preview = frame.CreatePreview(PreviewWidth, PreviewHeight);
            long elapsed = Stopwatch.GetTimestamp() - started;
            lock (_gate)
            {
                record.Preview = preview;
                record.PreviewMs = Milliseconds(elapsed);
                _previewTicks += elapsed;
                _previews++;
                _previewMaxMs = Math.Max(_previewMaxMs, record.PreviewMs);
            }
        }
        catch (Exception exception) when (exception is AzurPilotNativeBoundaryException or ObjectDisposedException
            or ArgumentException)
        {
            lock (_gate)
            {
                _previewTicks += Stopwatch.GetTimestamp() - started;
                record.Status = "failed";
                record.Reason = "preview_failed:" + exception.GetType().Name;
            }
        }
    }

    /// <summary>Связывает результат распознавания production с его захватом по точному provenance.</summary>
    internal AgentDebugFrameRecord? AttachObservation(GameUiObservation observation)
    {
        AgentDebugFrameRecord? record = Find(observation.Frame.Endpoint, observation.Frame.CaptureStartedTimestamp);
        lock (_gate)
        {
            if (record is null)
            {
                return null;
            }

            record.FrameId = observation.Frame.FrameId;
            record.Observation = observation;
            _byFrameId[observation.Frame.FrameId] = record;
            CountLate(record);
            return record;
        }
    }

    internal AgentDebugFrameRecord? Find(Guid frameId)
    {
        lock (_gate)
        {
            return _byFrameId.GetValueOrDefault(frameId);
        }
    }

    /// <summary>Изменяет запись под замком; изменения запечатанного кадра учитываются как поздние.</summary>
    internal void Update(AgentDebugFrameRecord record, Action<AgentDebugFrameRecord> update)
    {
        lock (_gate)
        {
            CountLate(record);
            update(record);
        }
    }

    internal void SealPending()
    {
        List<AgentDebugFrameRecord> sealedNow = [];
        lock (_gate)
        {
            foreach (AgentDebugFrameRecord record in _records.Where(r => !r.Sealed))
            {
                record.Sealed = true;
                sealedNow.Add(record);
            }
        }

        foreach (AgentDebugFrameRecord record in sealedNow)
        {
            Seal(record);
        }
    }

    /// <summary>Запечатывает все кадры и ограниченно ждёт фоновой записи.</summary>
    internal async Task CompleteAsync()
    {
        SealPending();
        _ = _queue.Writer.TryComplete();
        Task finished = await Task.WhenAny(_writer, Task.Delay(FlushTimeout)).ConfigureAwait(false);
        if (finished != _writer)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        while (_queue.Reader.TryRead(out AgentDebugFrameRecord? left))
        {
            Fail(left, "flush_timeout");
        }

        lock (_gate)
        {
            foreach (AgentDebugFrameRecord record in _records.Where(r => r.Preview is not null))
            {
                record.Preview!.Dispose();
                record.Preview = null;
            }
        }
    }

    private void Seal(AgentDebugFrameRecord record)
    {
        lock (_gate)
        {
            if (record.Preview is null)
            {
                string reason = record.Reason ?? "preview_not_created";
                if (record.Status == "failed")
                {
                    _failed[reason] = _failed.GetValueOrDefault(reason) + 1;
                }
                else
                {
                    record.Status = "skipped";
                    record.Reason = reason;
                    _skipped[reason] = _skipped.GetValueOrDefault(reason) + 1;
                }

                return;
            }

            record.EnqueuedTicks = Stopwatch.GetTimestamp();
            if (!_queue.Writer.TryWrite(record))
            {
                // Production не ждёт диск: переполнение очереди — явная потеря кадра с причиной.
                record.Status = "dropped";
                record.Reason = "queue_full";
                _dropped["queue_full"] = _dropped.GetValueOrDefault("queue_full") + 1;
                record.Preview.Dispose();
                record.Preview = null;
                return;
            }

            record.Status = "queued";
        }

        _run.Trace.Record("diagnostics", "frame_sealed", new { status = "queued" }, record.Reference, record.FrameId);
    }

    private async Task WriteFramesAsync()
    {
        try
        {
            await foreach (AgentDebugFrameRecord record in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await WriteFrameAsync(record).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
                {
                    // Отказ одного кадра не должен останавливать запись следующих и прерывать завершение отчёта.
                    Fail(record, "writer_failed:" + exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task WriteFrameAsync(AgentDebugFrameRecord record)
    {
        NativeFrame? preview;
        IReadOnlyList<NativeFrameAnnotation> annotations;
        string? limit = null;
        bool annotationFailed;
        lock (_gate)
        {
            _queueTicks += Stopwatch.GetTimestamp() - record.EnqueuedTicks;
            record.QueueWaitMs = Milliseconds(Stopwatch.GetTimestamp() - record.EnqueuedTicks);
            preview = record.Preview;
            record.Preview = null;
            try
            {
                annotations = AgentDebugAnnotations.Build(record);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                annotations = [];
                limit = "annotation_failed:" + exception.GetType().Name;
            }

            if (_rendered >= MaximumFrames)
            {
                limit = "frame_limit";
            }
            else if (_renderedBytes >= _byteBudget)
            {
                limit = BudgetReason();
            }

            annotationFailed = limit?.StartsWith("annotation_failed", StringComparison.Ordinal) == true;
        }

        if (preview is null)
        {
            return;
        }

        try
        {
            limit ??= FreeSpaceBelowLimit() ? "disk_space_low" : null;
            if (annotationFailed)
            {
                Fail(record, limit!);
                return;
            }

            if (limit is not null)
            {
                Skip(record, limit);
                return;
            }

            long renderStarted = Stopwatch.GetTimestamp();
            byte[] jpeg = preview.EncodeJpeg(JpegQuality, annotations.ToArray());
            long rendered = Stopwatch.GetTimestamp() - renderStarted;
            // Размер известен только после кодирования: бюджет не превышается и последним кадром.
            lock (_gate)
            {
                if (_renderedBytes + jpeg.Length > _byteBudget)
                {
                    limit = BudgetReason();
                }
            }

            if (limit is not null)
            {
                Skip(record, limit);
                return;
            }

            string name = record.Sequence.ToString("0000", CultureInfo.InvariantCulture) + "_"
                + (record.FrameId?.ToString("N") ?? "capture") + ".jpg";
            long writeStarted = Stopwatch.GetTimestamp();
            await using (FileStream stream = new(Path.Combine(_framesDirectory, name), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await stream.WriteAsync(jpeg).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            long written = Stopwatch.GetTimestamp() - writeStarted;
            string hash = Convert.ToHexStringLower(SHA256.HashData(jpeg));
            lock (_gate)
            {
                record.Status = "rendered";
                record.File = "frames/" + name;
                record.FileBytes = jpeg.Length;
                record.Sha256 = hash;
                record.RenderMs = Milliseconds(rendered);
                record.WriteMs = Milliseconds(written);
                _renderTicks += rendered;
                _writeTicks += written;
                _rendered++;
                _renderedBytes += jpeg.Length;
            }

            _run.Trace.Record("diagnostics", "frame_written", new
            {
                file = record.File,
                bytes = jpeg.Length,
                annotations = annotations.Count,
                renderMs = record.RenderMs,
                writeMs = record.WriteMs,
                queueWaitMs = record.QueueWaitMs,
            }, record.Reference, record.FrameId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or AzurPilotNativeBoundaryException or ArgumentException or ObjectDisposedException)
        {
            Fail(record, (exception is IOException or UnauthorizedAccessException ? "write_failed:" : "render_failed:")
                + exception.GetType().Name);
        }
        finally
        {
            preview.Dispose();
        }
    }

    // Бюджет меньше предела прогона только из-за остатка порога сессии: причина должна называть её, а не лимит прогона.
    private string BudgetReason()
    {
        return _byteBudget < MaximumRunBytes ? "session_limit" : "byte_limit";
    }

    private bool FreeSpaceBelowLimit()
    {
        try
        {
            string? root = Path.GetPathRoot(_framesDirectory);
            return root is not null && new DriveInfo(root).AvailableFreeSpace < MinimumFreeBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void Skip(AgentDebugFrameRecord record, string reason)
    {
        lock (_gate)
        {
            record.Status = "skipped";
            record.Reason = reason;
            _skipped[reason] = _skipped.GetValueOrDefault(reason) + 1;
        }

        _run.Trace.Record("diagnostics", "frame_skipped", new { reason }, record.Reference, record.FrameId);
    }

    private void Fail(AgentDebugFrameRecord record, string reason)
    {
        lock (_gate)
        {
            record.Status = "failed";
            record.Reason = reason;
            _failed[reason] = _failed.GetValueOrDefault(reason) + 1;
            record.Preview?.Dispose();
            record.Preview = null;
        }

        _run.Trace.Record("diagnostics", "frame_failed", new { reason }, record.Reference, record.FrameId);
    }

    private AgentDebugFrameRecord? Find(AndroidEndpoint endpoint, long captureStartedTimestamp)
    {
        lock (_gate)
        {
            return _records.LastOrDefault(r => r.Endpoint == endpoint && r.CaptureStartedTimestamp == captureStartedTimestamp);
        }
    }

    private void CountLate(AgentDebugFrameRecord record)
    {
        if (record.Status is not ("captured" or "skipped" or "failed") && record.Sealed)
        {
            record.LateEvents++;
            _lateEvents++;
        }
    }

    private static int Sum(Dictionary<string, int> values)
    {
        return values.Values.Sum();
    }

    private static double Milliseconds(long ticks)
    {
        return Math.Round(Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds, 1);
    }
}

/// <summary>Счётчики кадров и стоимость диагностики; вложенные интервалы не суммируются повторно.</summary>
internal sealed record AgentDebugFrameCounters(int Captured, int Rendered, int Skipped, int Failed, int Dropped,
    int CaptureFailures, IReadOnlyDictionary<string, int> SkippedReasons, IReadOnlyDictionary<string, int> FailedReasons,
    IReadOnlyDictionary<string, int> DroppedReasons, int LateEvents, long RenderedBytes, int Previews,
    double PreviewMs, double PreviewMaxMs, double RenderMs, double WriteMs, double QueueWaitMs, string? DisabledReason);

/// <summary>
/// Аннотации debug-JPEG из наблюдённых production-фактов кадра. Координаты переводятся из физических
/// полуоткрытых границ через <see cref="GameGeometry.ToLogical(GamePixelRect)"/>; Absent не рисуется рамкой.
/// Подписи — короткие технические метки ASCII, потому что native шрифт кадра поддерживает только его;
/// русская легенда цветов и меток находится в <c>summary.md</c> (<see cref="Legend"/>).
/// </summary>
internal static class AgentDebugAnnotations
{
    /// <summary>Русская легенда меток JPEG для <c>summary.md</c>: на самом изображении кириллица недоступна.</summary>
    internal static readonly IReadOnlyList<string> Legend =
    [
        "зелёная рамка — `Unique`; оранжевая пунктирная — `Ambiguous` (подпись: ассет, статус, score `s`, margin `m`, mae `e`)",
        "серая пунктирная рамка — ROI ассетов действия; голубая рамка — безопасная область ввода",
        "жёлтый кружок и пунктир — `tap_planned` (кандидат); оранжевый крест — `tap_permitted` и `tap_attempted`",
        "красная точка — `tap_completed` (ADB вернул exit 0, переход UI не доказан); пурпурный крест — `tap_not_issued` или `tap_result_unproven`",
        "`no_box: <ассет>:<статус>` — ассеты без рамки: `Absent` и недоказанные признаки не считаются наблюдённым объектом",
        "шапка: номер кадра, `frameId`, время, размер, длительность capture/ADB, `state=<правило>/<состояние>`, `viewport`, `op`; "
            + "`source=…`, `postcondition=proven|unchanged` и `rejected=…` показывают роль кадра в действии",
    ];

    private const int TextScale = 38;
    private const int LineHeight = 15;

    internal static IReadOnlyList<NativeFrameAnnotation> Build(AgentDebugFrameRecord record)
    {
        List<NativeFrameAnnotation> result = [];
        GameGeometry geometry = new(record.Width, record.Height);
        GameUiObservation? observation = record.Observation;
        HashSet<string> roiAssets = new(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, string> roles in record.Actions.Values)
        {
            foreach (string key in new[] { "source", "required", "excluded", "postconditions" })
            {
                foreach (string id in roles.GetValueOrDefault(key, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    _ = roiAssets.Add(id);
                }
            }
        }

        foreach (string id in roiAssets.Order(StringComparer.Ordinal))
        {
            if (GameAssetCatalog.Default.Assets.SingleOrDefault(a => a.Id == id) is GameAsset asset)
            {
                result.Add(Rect(asset.Search, 150, 150, 150, 1, dashed: true));
            }
        }

        List<string> unobserved = [];
        if (observation is not null)
        {
            foreach (GameUiMatch match in observation.Matches.Take(48))
            {
                string score = string.Create(CultureInfo.InvariantCulture,
                    $"{Ascii(match.AssetId, 22)} {match.Status} s={match.Score:0.00} m={match.Score - match.RunnerUpScore:0.00} e={match.MeanAbsoluteError:0.0}");
                if (match.Status is GameMatchStatus.Unique or GameMatchStatus.Ambiguous && match.Bounds is GamePixelRect bounds)
                {
                    GameRect logical = geometry.ToLogical(bounds);
                    bool unique = match.Status == GameMatchStatus.Unique;
                    result.Add(Rect(logical, unique ? (byte)0 : (byte)255, unique ? (byte)220 : (byte)140, 0, 2, dashed: !unique));
                    result.Add(Label(logical.Left, Math.Max(12, logical.Top - 4), score, unique ? (byte)0 : (byte)255,
                        unique ? (byte)255 : (byte)170, 0));
                    if (match.SafeTapBounds is GamePixelRect safe)
                    {
                        result.Add(Rect(geometry.ToLogical(safe), 0, 200, 255, 1));
                    }
                }
                else
                {
                    unobserved.Add(Ascii(match.AssetId, 22) + ":" + match.Status);
                }
            }
        }

        foreach (AgentDebugFrameMark mark in record.Marks.Take(64))
        {
            AddMark(result, geometry, mark);
        }

        List<string> header = [.. Wrap(string.Create(CultureInfo.InvariantCulture,
            $"#{record.Sequence:0000} {record.Reference} frame={(record.FrameId?.ToString("N")[..8] ?? "-")} t=+{record.CapturedAtMs:0.0}ms {record.Width}x{record.Height} capture={record.CaptureMs:0}ms {record.Method}={record.CommandMs:0}ms"))];
        header.AddRange(Wrap($"state={record.StateRule ?? "-"}/{record.State ?? "-"} viewport={record.Viewport ?? "not_checked"} op={record.OperationLabel()}"));
        foreach (AgentDebugFrameMark mark in record.Marks.Where(m => m.Label is not null
            && m.Kind is "action_rejected" or "postcondition" or "action_source").Take(3))
        {
            header.AddRange(Wrap((mark.ActionId is null ? "" : mark.ActionId + ": ") + mark.Label));
        }

        // Панели не перекрывают кадр заливкой: текст с обводкой рисуется первым, доказательства — поверх него.
        List<NativeFrameAnnotation> panels = [];
        AddPanel(panels, header, top: 0, 255, 255, 255);
        List<string> footer = [];
        if (unobserved.Count > 0)
        {
            // Absent/недоказанное перечисляется подписью: геометрически наблюдённым объектом оно не является.
            footer.AddRange(Wrap("no_box: " + string.Join(" ", unobserved)).Take(4));
        }

        footer.Add("legend=summary.md");
        AddPanel(panels, footer, GameGeometry.LogicalHeight - 6 - (footer.Count * LineHeight), 230, 230, 230);
        List<NativeFrameAnnotation> ordered = [.. panels, .. result];
        return ordered.Count > AzurPilotNativeBridge.MaximumAnnotations
            ? ordered.Take(AzurPilotNativeBridge.MaximumAnnotations).ToArray() : ordered;
    }

    private static string OperationLabel(this AgentDebugFrameRecord record)
    {
        return record.Actions.Count == 0 ? (record.Observation is null ? "capture-only" : "observe")
            : string.Join(",", record.Actions.Keys.Order(StringComparer.Ordinal));
    }

    private static void AddMark(List<NativeFrameAnnotation> result, GameGeometry geometry, AgentDebugFrameMark mark)
    {
        GameInputPoint? point = null;
        if (mark.Point is GameInputPoint physical)
        {
            try
            {
                point = geometry.ToLogical(physical);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
        }

        string label = Ascii((mark.ActionId is null ? "" : mark.ActionId + " ") + (mark.Label ?? mark.Kind), 60);
        switch (mark.Kind)
        {
            case "tap_planned" when point is GameInputPoint p:
                result.Add(new(NativeAnnotationKind.Circle, new(p.X, p.Y, 14, 0), 255, 230, 0, 2));
                result.Add(Label(p.X + 16, p.Y - 16, label, 255, 230, 0));
                break;
            case "tap_permitted" or "tap_attempted" when point is GameInputPoint p:
                result.Add(new(NativeAnnotationKind.Cross, new(p.X, p.Y, 9, 0), 255, 140, 0, 2));
                result.Add(Label(p.X + 16, p.Y + (mark.Kind == "tap_permitted" ? 0 : 14), label, 255, 140, 0));
                break;
            case "tap_completed" when point is GameInputPoint p:
                result.Add(new(NativeAnnotationKind.FilledCircle, new(p.X, p.Y, 6, 0), 255, 0, 0, 2));
                result.Add(Label(p.X + 16, p.Y + 28, label, 255, 60, 60));
                break;
            case "tap_not_issued" or "tap_result_unproven" when point is GameInputPoint p:
                result.Add(new(NativeAnnotationKind.Cross, new(p.X, p.Y, 12, 0), 255, 0, 255, 3));
                result.Add(Label(p.X + 16, p.Y + 28, label, 255, 0, 255));
                break;
            default:
                break;
        }

        if (mark.Region is GamePixelRect region && mark.Kind == "tap_planned")
        {
            try
            {
                result.Add(Rect(geometry.ToLogical(region), 255, 230, 0, 1, dashed: true));
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
    }

    private static void AddPanel(List<NativeFrameAnnotation> result, List<string> lines, int top, byte red, byte green, byte blue)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            // Чёрная обводка делает подпись читаемой на любом фоне, не скрывая пиксели кадра заливкой.
            result.Add(Label(6, top + 14 + (index * LineHeight), lines[index], 0, 0, 0, thickness: 3));
            result.Add(Label(6, top + 14 + (index * LineHeight), lines[index], red, green, blue));
        }
    }

    // Native текст ограничен 63 ASCII-символами: длинная подпись переносится по словам.
    private static IEnumerable<string> Wrap(string text)
    {
        string line = string.Empty;
        foreach (string word in Ascii(text, 400).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = word.Length > 63 ? word[..63] : word;
            if (line.Length > 0 && line.Length + 1 + part.Length > 63)
            {
                yield return line;
                line = string.Empty;
            }

            line = line.Length == 0 ? part : line + " " + part;
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }

    private static NativeFrameAnnotation Rect(GameRect rect, byte red, byte green, byte blue, byte thickness, bool dashed = false)
    {
        return new(NativeAnnotationKind.Rectangle, new(rect.Left, rect.Top, rect.Right, rect.Bottom), red, green, blue,
            thickness, dashed);
    }

    private static NativeFrameAnnotation Label(int left, int baseline, string text, byte red, byte green, byte blue,
        byte thickness = 1)
    {
        return new(NativeAnnotationKind.Text, new(left, baseline, TextScale, 0), red, green, blue, thickness, Text: Ascii(text, 63));
    }

    /// <summary>Печатный ASCII без пустой строки; прочие символы заменяются на '?'.</summary>
    internal static string Ascii(string value, int maximum)
    {
        string ascii = new([.. value.Take(maximum).Select(c => c is >= ' ' and <= '~' ? c : '?')]);
        return ascii.Length == 0 ? "-" : ascii;
    }
}
