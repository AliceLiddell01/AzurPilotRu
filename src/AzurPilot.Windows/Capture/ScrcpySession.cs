using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AzurPilot.Core.Capture;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Capture;

/// <summary>
/// <c>scrcpy</c>: закреплённый scrcpy-server 5.0.1 передаёт H.264 через forward-туннель; пакеты непрерывно
/// декодируются системным декодером внутри DLL, а RGB8 строится только по запросу захвата.
/// </summary>
/// <remarks>
/// <para>
/// Захват не возвращает кэшированный кадр: он ждёт кадр, полученный хостом не раньше чем через
/// <see cref="PipelineLatencyBound"/> после начала запроса. Граница консервативно покрывает задержку
/// кодирования, передачи и декодирования, поэтому содержимое такого кадра существовало на устройстве уже после
/// начала запроса. <see cref="AndroidScreenshotCaptureInfo.CaptureStartedTimestamp"/> = получение − граница.
/// </para>
/// <para>
/// Частота источника — частота кодера (при статичном экране сервер повторяет кадр не реже раза в 100 мс);
/// интервал опроса её не меняет.
/// </para>
/// </remarks>
internal sealed class ScrcpySession(ScreenshotSessionContext context) : ScreenshotSession(ScreenshotMethod.Scrcpy, context)
{
    /// <summary>Консервативная граница задержки конвейера кодирования, передачи и декодирования.</summary>
    internal static readonly TimeSpan PipelineLatencyBound = TimeSpan.FromMilliseconds(200);

    private const string ServerVersion = "5.0.1";
    private const string Marker = "com.genymobile.scrcpy.Server";
    private const uint CodecH264 = 0x68323634;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private NativeVideoDecoderSafeHandle? _decoder;
    private TcpClient? _socket;
    private Task? _reader;
    private TaskCompletionSource _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastArrival;
    private ulong _decodedFrames;
    private ulong _returnedSequence;
    private int _width;
    private int _height;
    private Exception? _readerFailure;
    private string _streamSession = string.Empty;
    private long _minimumOffset = long.MaxValue;
    private long _maximumOffset = long.MinValue;

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        CaptureDependency dependency = CaptureDependencies.Get("scrcpy-server");
        Facts["dependency"] = dependency.Id + "@" + dependency.Version;
        Facts["license"] = dependency.License;
        Facts["latency_bound_ms"] = PipelineLatencyBound.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
        await Context.CleanOrphansAsync(cancellationToken).ConfigureAwait(false);
        await Context.Adb.PushVerifiedAsync(dependency, "scrcpy-server.jar", executable: false, cancellationToken)
            .ConfigureAwait(false);
        _decoder = AzurPilotNativeBridge.CreateH264Decoder();

        string scid = Random.Shared.Next(1, int.MaxValue).ToString("x8", CultureInfo.InvariantCulture);
        _streamSession = scid;
        int hostPort = await ForwardTrackedAsync("localabstract:scrcpy_" + scid, cancellationToken).ConfigureAwait(false);
        // Аудио, управление и очистка экрана отключены: сервер только кодирует дисплей. Понижение разрешения
        // при ошибке кодера запрещено, чтобы не получить кадр другого размера незаметно.
        _ = await StartTrackedHelperAsync("CLASSPATH=" + CaptureAdb.DeviceDirectory + "/scrcpy-server.jar app_process / "
            + Marker + " " + ServerVersion + " scid=" + scid + " log_level=warn tunnel_forward=true audio=false "
            + "control=false cleanup=true video_codec=h264 max_size=0 max_fps=60 downsize_on_error=false "
            + "send_device_meta=true send_dummy_byte=true send_stream_meta=true send_frame_meta=true",
            Marker, cancellationToken).ConfigureAwait(false);

        NetworkStream stream = await ConnectAsync(hostPort, cancellationToken).ConfigureAwait(false);
        byte[] meta = new byte[64 + 4];
        await NetcatChannel.ReadExactAsync(stream, meta, cancellationToken).ConfigureAwait(false);
        uint codec = BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(64));
        if (codec != CodecH264)
        {
            // 0 — поток отключён, 1 — ошибка кодера на устройстве.
            throw new CaptureBackendException(codec is 0 or 1 ? "scrcpy_encoder_failed" : "scrcpy_protocol_mismatch",
                detail: codec.ToString("x8", CultureInfo.InvariantCulture));
        }

        _reader = Task.Run(() => ReadLoopAsync(stream, _stop.Token), CancellationToken.None);
    }

    private async Task<NetworkStream> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        long started = Clock.GetTimestamp();
        while (Clock.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            TcpClient client = new() { NoDelay = true, ReceiveBufferSize = 4 * 1024 * 1024 };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                byte[] dummy = new byte[1];
                // Forward принимает соединение раньше, чем сервер слушает сокет: EOF вместо байта означает «ещё рано».
                if (await stream.ReadAsync(dummy, cancellationToken).ConfigureAwait(false) == 1)
                {
                    _socket = client;
                    return stream;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
            }

            client.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100), Clock, cancellationToken).ConfigureAwait(false);
        }

        throw new CaptureBackendException("scrcpy_connect_timeout");
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[12];
        byte[] packet = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await NetcatChannel.ReadExactAsync(stream, header.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
                ulong ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header);
                await NetcatChannel.ReadExactAsync(stream, header.AsMemory(8, 4), cancellationToken).ConfigureAwait(false);
                if ((ptsAndFlags & (1UL << 63)) != 0)
                {
                    // Пакет сессии: флаги, ширина, высота. Новая сессия кодирования — новый размер потока.
                    lock (_gate)
                    {
                        _width = (int)(ptsAndFlags & 0xFFFFFFFF);
                        _height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                    }

                    continue;
                }

                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                if (size <= 0 || size > AzurPilotNativeBridge.MaximumVideoPacketBytes)
                {
                    throw new CaptureBackendException("scrcpy_packet_invalid");
                }

                if (packet.Length < size)
                {
                    packet = new byte[size];
                }

                await NetcatChannel.ReadExactAsync(stream, packet.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                bool config = (ptsAndFlags & (1UL << 62)) != 0;
                uint flags = (config ? AzurPilotNativeBridge.VideoPacketConfig : 0)
                    | ((ptsAndFlags & (1UL << 61)) != 0 ? AzurPilotNativeBridge.VideoPacketKeyFrame : 0);
                long pts = config ? 0 : (long)(ptsAndFlags & ((1UL << 61) - 1));
                int decoded = AzurPilotNativeBridge.PushVideoPacket(_decoder!, packet.AsSpan(0, size), pts, flags);
                if (decoded > 0)
                {
                    long arrival = Clock.GetTimestamp();
                    long offset = arrival / Math.Max(1, Clock.TimestampFrequency / 1_000_000) - pts;
                    TaskCompletionSource signal;
                    lock (_gate)
                    {
                        _lastArrival = arrival;
                        _decodedFrames += (ulong)decoded;
                        _minimumOffset = Math.Min(_minimumOffset, offset);
                        _maximumOffset = Math.Max(_maximumOffset, offset);
                        signal = _frameSignal;
                        _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    _ = signal.TrySetResult();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                _readerFailure = exception;
                signal = _frameSignal;
            }

            _ = signal.TrySetResult();
        }
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        CaptureMoment requested = Now();
        long required = requested.Timestamp + (long)(PipelineLatencyBound.TotalSeconds * Clock.TimestampFrequency);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FrameTimeout + PipelineLatencyBound);
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_readerFailure is Exception failure)
                {
                    Broken = true;
                    throw failure as CaptureBackendException ?? new CaptureBackendException("scrcpy_stream_closed", inner: failure);
                }

                if (_decodedFrames > _returnedSequence && _lastArrival >= required)
                {
                    break;
                }

                wait = _frameSignal.Task;
            }

            try
            {
                await wait.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CaptureBackendException("scrcpy_no_fresh_frame") { SessionBroken = false };
            }
        }

        int width;
        int height;
        lock (_gate)
        {
            width = _width;
            height = _height;
        }

        long acquired = Clock.GetTimestamp();
        NativeFrame? frame = AzurPilotNativeBridge.TryGetLatestVideoFrame(_decoder!, _returnedSequence, width, height,
            NativeYuvMatrix.Auto, out long pts, out ulong sequence)
            ?? throw new CaptureBackendException("scrcpy_frame_unavailable") { SessionBroken = false };
        _returnedSequence = sequence;
        long frameArrival;
        lock (_gate)
        {
            frameArrival = _lastArrival;
            Facts["offset_spread_ms"] = ((_maximumOffset - _minimumOffset) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        long provenance = frameArrival - (long)(PipelineLatencyBound.TotalSeconds * Clock.TimestampFrequency);
        CaptureMoment started = new(provenance,
            requested.UtcNow + Clock.GetElapsedTime(requested.Timestamp, provenance));
        return Complete(frame, started, acquired, 0, new ScreenshotStreamFrame(sequence, pts, frameArrival, _streamSession),
            requested.Timestamp);
    }

    protected override async ValueTask CloseTransportAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        if (_reader is not null)
        {
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Поток чтения завис в native декодере: декодер не освобождается, чтобы не освободить используемую память.
                _decoder?.SetHandleAsInvalid();
            }
        }

        _decoder?.Dispose();
        _stop.Dispose();
    }
}
