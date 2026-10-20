using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Capture;

/// <summary><c>ADB</c>: существующий захват PNG через stdout bundled ADB без изменений контракта.</summary>
internal sealed class AdbPngSession(ScreenshotSessionContext context) : ScreenshotSession(ScreenshotMethod.Adb, context)
{
    internal override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal override Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureAsync(CancellationToken cancellationToken)
        => Context.Android.CaptureScreenshotAsync(Context.Target.InstanceId, Context.Target.Endpoint, cancellationToken);

    protected override Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

/// <summary>
/// Канал <c>&lt;команда&gt; | nc</c>: устройство передаёт байты на loopback-слушатель хоста через target-scoped
/// <c>adb reverse</c>. Слушатель привязан только к 127.0.0.1, принимает одно соединение на захват, проверяет
/// loopback-адрес собеседника и ограничивает объём и время чтения.
/// </summary>
internal sealed class NetcatChannel(ScreenshotSessionContext context) : IDisposable
{
    private TcpListener? _listener;

    internal int DevicePort { get; private set; }

    internal string Command { get; private set; } = "nc";

    /// <summary>Создаёт слушатель и reverse; возвращает ресурс reverse для журнала сессии.</summary>
    internal async Task<int> OpenAsync(Func<int, CancellationToken, Task<int>> reverse, CancellationToken cancellationToken)
    {
        string probe = await context.Adb.ShellAsync("command -v nc || command -v busybox || true", "nc_probe_failed",
            cancellationToken).ConfigureAwait(false);
        if (probe.Contains("/nc", StringComparison.Ordinal))
        {
            Command = "nc";
        }
        else if (probe.Contains("busybox", StringComparison.Ordinal))
        {
            Command = "busybox nc";
        }
        else
        {
            throw new CaptureBackendException("netcat_unavailable", unsupported: true);
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        int hostPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        DevicePort = await reverse(hostPort, cancellationToken).ConfigureAwait(false);
        return DevicePort;
    }

    /// <summary>
    /// Запускает команду устройства с выводом в nc и читает поток: <paramref name="read"/> получает сокет и
    /// возвращает полезную нагрузку. Команда ADB завершается после закрытия соединения.
    /// </summary>
    internal async Task<T> ReceiveAsync<T>(string producer, Func<NetworkStream, CancellationToken, Task<T>> read,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("Канал не открыт.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<ApplicationResult<WindowsProcessOutcome>> command = context.Adb.RunAsync(
            ["shell", producer + " | " + Command + " 127.0.0.1 " + CaptureAdb.Number(DevicePort)], timeout, deadline.Token);
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                throw new CaptureBackendException("netcat_peer_not_loopback");
            }

            client.NoDelay = true;
            client.ReceiveBufferSize = 4 * 1024 * 1024;
            T result = await read(client.GetStream(), deadline.Token).ConfigureAwait(false);
            client.Close();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CaptureBackendException("netcat_timeout");
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            throw new CaptureBackendException("netcat_stream_failed", inner: exception);
        }
        finally
        {
            // Процесс ADB ограничен собственным дедлайном; результат команды не нужен после чтения данных.
            try
            {
                _ = await command.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _listener?.Stop();
        _listener = null;
    }

    /// <summary>Читает ровно <paramref name="buffer"/>.Length байт либо бросает отказ усечения.</summary>
    internal static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new CaptureBackendException("stream_truncated", detail: offset + "/" + buffer.Length);
            }

            offset += read;
        }
    }
}

/// <summary><c>ADB_nc</c>: несжатый <c>screencap</c> через nc и target-scoped reverse.</summary>
internal sealed class AdbNcSession(ScreenshotSessionContext context) : ScreenshotSession(ScreenshotMethod.AdbNc, context)
{
    private const int MaximumPayload = AzurPilotNativeBridge.MaximumRawInputBytes;
    private readonly NetcatChannel _channel = new(context);
    private int _headerBytes;

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        CaptureDeviceFacts facts = await Context.FactsAsync(cancellationToken).ConfigureAwait(false);
        // Android 9+ дописывает к заголовку screencap поле dataspace: 16 байт вместо 12.
        _headerBytes = facts.SdkLevel >= 28 ? 16 : 12;
        _ = await _channel.OpenAsync(ReverseTrackedAsync, cancellationToken).ConfigureAwait(false);
        Facts["netcat"] = _channel.Command;
        Facts["header_bytes"] = CaptureAdb.Number(_headerBytes);
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        CaptureMoment started = Now();
        (byte[] pixels, int width, int height, NativeSourcePixelFormat format, int bpp) = await _channel.ReceiveAsync(
            "screencap", async (stream, token) =>
            {
                byte[] header = new byte[_headerBytes];
                await NetcatChannel.ReadExactAsync(stream, header, token).ConfigureAwait(false);
                int width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
                int height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
                (NativeSourcePixelFormat format, int bpp) = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) switch
                {
                    1 or 2 => (NativeSourcePixelFormat.Rgba8888, 4),
                    3 => (NativeSourcePixelFormat.Rgb888, 3),
                    4 => (NativeSourcePixelFormat.Rgb565LittleEndian, 2),
                    5 => (NativeSourcePixelFormat.Bgra8888, 4),
                    _ => throw new CaptureBackendException("screencap_format_unsupported"),
                };
                long length = (long)width * height * bpp;
                if (width <= 0 || height <= 0 || length > MaximumPayload)
                {
                    throw new CaptureBackendException("screencap_header_invalid");
                }

                byte[] pixels = new byte[length];
                await NetcatChannel.ReadExactAsync(stream, pixels, token).ConfigureAwait(false);
                return (pixels, width, height, format, bpp);
            }, CaptureAdb.ShortTimeout, cancellationToken).ConfigureAwait(false);
        long acquired = Clock.GetTimestamp();
        NativeFrame frame = AzurPilotNativeBridge.CreateFrameFromPixels(pixels, width, height, width * bpp, format, false);
        return Complete(frame, started, acquired, pixels.Length + _headerBytes);
    }

    protected override ValueTask CloseTransportAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
