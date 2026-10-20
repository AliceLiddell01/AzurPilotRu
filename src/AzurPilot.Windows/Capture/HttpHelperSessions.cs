using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AzurPilot.Core.Capture;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Capture;

/// <summary>
/// Общая часть транспортов с HTTP-сервером на устройстве: закреплённый файл отправляется в каталог helper-ов,
/// сервер запускается фоновым процессом с точным PID на случайном порту устройства, хост обращается к нему
/// через target-scoped forward на loopback. Прокси и переменные окружения HTTP не используются.
/// </summary>
internal abstract class HttpHelperSession(ScreenshotMethod method, ScreenshotSessionContext context)
    : ScreenshotSession(method, context)
{
    private const int MaximumResponseBytes = AzurPilotNativeBridge.MaximumRawInputBytes;
    private HttpClient? _http;

    protected HttpClient Http => _http ?? throw new InvalidOperationException("Сессия не открыта.");

    protected abstract string DependencyId { get; }

    protected abstract string RemoteName { get; }

    /// <summary>Маркер cmdline helper-а: остановка затрагивает только процесс с этим маркером.</summary>
    protected abstract string Marker { get; }

    protected abstract string LaunchCommand(string remotePath, int devicePort);

    /// <summary>Ответ, доказывающий готовность именно этого сервера.</summary>
    protected abstract Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        CaptureDependency dependency = CaptureDependencies.Get(DependencyId);
        Facts["dependency"] = dependency.Id + "@" + dependency.Version;
        Facts["license"] = dependency.License;
        await Context.CleanOrphansAsync(cancellationToken).ConfigureAwait(false);
        await Context.Adb.PushVerifiedAsync(dependency, RemoteName, executable: false, cancellationToken).ConfigureAwait(false);

        // Случайный порт устройства снижает вероятность столкновения со сторонним сервером; готовность
        // проверяется ответом протокола именно этого helper-а.
        int devicePort = Random.Shared.Next(30000, 40000);
        _ = await StartTrackedHelperAsync(LaunchCommand(CaptureAdb.DeviceDirectory + "/" + RemoteName, devicePort), Marker,
            cancellationToken).ConfigureAwait(false);
        int hostPort = await ForwardTrackedAsync("tcp:" + CaptureAdb.Number(devicePort), cancellationToken)
            .ConfigureAwait(false);
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            BaseAddress = new Uri("http://127.0.0.1:" + CaptureAdb.Number(hostPort) + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumResponseBytes,
        };

        long deadline = Clock.GetTimestamp();
        while (Clock.GetElapsedTime(deadline) < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                if (await IsReadyAsync(attempt.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Сервер ещё стартует: forward принимает соединение раньше, чем helper слушает порт.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Clock, cancellationToken).ConfigureAwait(false);
        }

        throw new CaptureBackendException("helper_not_ready");
    }

    /// <summary>Выполняет запрос с дедлайном и возвращает тело успешного ответа.</summary>
    protected async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CaptureAdb.ShortTimeout);
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            byte[] body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? body
                : throw new CaptureBackendException("helper_http_status", detail: ((int)response.StatusCode).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CaptureBackendException("helper_timeout");
        }
        catch (HttpRequestException exception)
        {
            throw new CaptureBackendException("helper_connection_failed", inner: exception);
        }
    }

    protected override ValueTask CloseTransportAsync()
    {
        _http?.Dispose();
        _http = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary><c>uiautomator2</c>: JSON-RPC <c>takeScreenshot</c> закреплённого u2.jar (JPEG).</summary>
internal sealed class Uiautomator2Session(ScreenshotSessionContext context)
    : HttpHelperSession(ScreenshotMethod.Uiautomator2, context)
{
    // Качество исходного клиента uiautomator2; масштаб 1 сохраняет физическое разрешение.
    private const string ScreenshotRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"takeScreenshot\",\"params\":[1,80]}";

    protected override string DependencyId => "uiautomator2-server";

    protected override string RemoteName => "u2.jar";

    protected override string Marker => "com.wetest.uia2.Main";

    protected override string LaunchCommand(string remotePath, int devicePort)
        => "CLASSPATH=" + remotePath + " app_process / com.wetest.uia2.Main -p " + CaptureAdb.Number(devicePort);

    protected override async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        string body = await Http.GetStringAsync(new Uri("ping", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return body.Trim().Equals("pong", StringComparison.Ordinal);
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        CaptureMoment started = Now();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("jsonrpc/0", UriKind.Relative))
        {
            Content = new StringContent(ScreenshotRequest, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        byte[] body = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] jpeg;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new CaptureBackendException("uiautomator2_rpc_error") { SessionBroken = false };
            }

            jpeg = document.RootElement.GetProperty("result").GetBytesFromBase64();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException)
        {
            throw new CaptureBackendException("uiautomator2_response_invalid", inner: exception) { SessionBroken = false };
        }

        long acquired = Clock.GetTimestamp();
        NativeFrame frame = AzurPilotNativeBridge.DecodeJpeg(jpeg);
        return Complete(frame, started, acquired, jpeg.Length);
    }
}

/// <summary>
/// <c>DroidCast</c> (PNG <c>/preview</c>) и <c>DroidCast_raw</c> (RGB565 <c>/screenshot</c>) закреплённого
/// DroidCast_raw. Размер запроса — физический размер дисплея; сервер сам учитывает поворот.
/// </summary>
internal sealed class DroidCastSession(ScreenshotSessionContext context, bool raw)
    : HttpHelperSession(raw ? ScreenshotMethod.DroidCastRaw : ScreenshotMethod.DroidCast, context)
{
    private int _physicalWidth;
    private int _physicalHeight;

    protected override string DependencyId => "droidcast-raw";

    protected override string RemoteName => "DroidCast_raw.apk";

    protected override string Marker => "ink.mol.droidcast_raw.Main";

    /// <summary>Ожидаемая ориентированная геометрия кадра (по эталону ADB); задаёт маршрутизатор.</summary>
    internal (int Width, int Height)? ExpectedSize { get; init; }

    protected override string LaunchCommand(string remotePath, int devicePort)
        => "CLASSPATH=" + remotePath + " app_process / ink.mol.droidcast_raw.Main --port=" + CaptureAdb.Number(devicePort);

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        string size = await Context.Adb.ShellAsync("wm size", "display_size_unavailable", cancellationToken)
            .ConfigureAwait(false);
        // Override size (если задан) действует вместо физического.
        string? line = size.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("size:", StringComparison.Ordinal));
        string[] parts = line?[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Split('x') ?? [];
        if (parts.Length != 2 || !int.TryParse(parts[0], out _physicalWidth) || !int.TryParse(parts[1], out _physicalHeight)
            || _physicalWidth <= 0 || _physicalHeight <= 0)
        {
            throw new CaptureBackendException("display_size_unrecognized");
        }

        await base.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        // Корень не обслуживается: 404 означает, что HTTP-сервер DroidCast уже слушает порт.
        using HttpResponseMessage response = await Http.GetAsync(new Uri("/", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound;
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        string query = "?width=" + CaptureAdb.Number(_physicalWidth) + "&height=" + CaptureAdb.Number(_physicalHeight);
        CaptureMoment started = Now();
        using HttpRequestMessage request = new(HttpMethod.Get,
            new Uri(Method == ScreenshotMethod.DroidCastRaw ? "screenshot" + query : "preview" + query + "&format=png",
                UriKind.Relative));
        byte[] body = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        long acquired = Clock.GetTimestamp();
        if (Method == ScreenshotMethod.DroidCast)
        {
            try
            {
                return Complete(AzurPilotNativeBridge.DecodePng(body), started, acquired, body.Length);
            }
            catch (NativeFrameDecodeException exception)
            {
                throw new CaptureBackendException("droidcast_png_invalid", inner: exception) { SessionBroken = false };
            }
        }

        (int width, int height) = ExpectedSize ?? (Math.Max(_physicalWidth, _physicalHeight), Math.Min(_physicalWidth, _physicalHeight));
        if (body.LongLength != (long)width * height * 2)
        {
            // Короткий ответ — текст ошибки сервера, а не bitmap.
            throw new CaptureBackendException(body.Length < 500 ? "droidcast_server_error" : "droidcast_raw_size_mismatch",
                detail: CaptureAdb.Number(body.LongLength))
            {
                SessionBroken = false,
            };
        }

        NativeFrame frame = AzurPilotNativeBridge.CreateFrameFromPixels(body, width, height, width * 2,
            NativeSourcePixelFormat.Rgb565LittleEndian, false);
        return Complete(frame, started, acquired, body.Length);
    }
}
