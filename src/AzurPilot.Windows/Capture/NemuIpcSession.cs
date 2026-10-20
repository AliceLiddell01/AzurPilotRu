using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AzurPilot.Core.Capture;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Capture;

/// <summary>
/// <c>nemu_ipc</c>: кадр рендерера MuMu через <c>external_renderer_ipc.dll</c> обнаруженной установки.
/// </summary>
/// <remarks>
/// <para>
/// DLL берётся только из каталога установки (известные подкаталоги SDK), проверяется как PE x64 без reparse
/// point и загружается DLL-стороной с поиском зависимостей только в её каталоге и System32. Экземпляр
/// адресуется индексом exact identity, а не портом.
/// </para>
/// <para>
/// Вызовы DLL выполняются на выделенном фоновом потоке. Зависший вызов не блокирует команду: после
/// тайм-аута сессия признаётся сломанной, поток оставляется, а handle не освобождается, чтобы не отключать
/// соединение из-под работающего вызова. Формат RGBA и вертикальный переворот повторяют исходный клиент;
/// корректность каналов подтверждается сравнением с эталоном ADB.
/// </para>
/// </remarks>
internal sealed class NemuIpcSession(ScreenshotSessionContext context) : ScreenshotSession(ScreenshotMethod.NemuIpc, context)
{
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] SdkDirectories = [@"nx_main\sdk", @"shell\sdk"];

    private readonly BlockingCollection<Action> _work = [];
    private NativeNemuSafeHandle? _session;
    private Thread? _thread;

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        string root = Context.Target.Installation?.InstallRoot
            ?? throw new CaptureBackendException("mumu_installation_unknown", unsupported: true);
        if (!int.TryParse(Context.Target.InstanceId.Index, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            throw new CaptureBackendException("instance_index_invalid", unsupported: true);
        }

        CaptureDeviceFacts facts = await Context.FactsAsync(cancellationToken).ConfigureAwait(false);
        string androidMajor = facts.AndroidRelease.Split('.')[0];
        string[] candidates =
        [
            .. SdkDirectories.Take(1),
            @"nx_device\" + androidMajor + @".0\shell\sdk",
            .. SdkDirectories.Skip(1),
        ];
        _thread = new Thread(Run) { IsBackground = true, Name = "AzurPilot nemu_ipc" };
        _thread.Start();

        CaptureBackendException? last = null;
        foreach (string relative in candidates)
        {
            string path = Path.Combine(root, relative, "external_renderer_ipc.dll");
            string? rejected = Validate(root, path);
            if (rejected is not null)
            {
                last = new CaptureBackendException(rejected, unsupported: true, detail: relative);
                continue;
            }

            try
            {
                _session = await CallAsync(() => AzurPilotNativeBridge.OpenNemu(path, root, index), cancellationToken)
                    .ConfigureAwait(false);
                Facts["dll"] = relative.Replace('\\', '/');
                Facts["dll_version"] = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
                Facts["mumu_version"] = Context.Target.Installation.Version;
                return;
            }
            catch (NativeCaptureSourceException exception)
            {
                last = new CaptureBackendException(exception.StatusCode == AzurPilotNativeBridge.StatusProviderFailure
                    ? "nemu_connect_failed" : "nemu_dll_unusable", unsupported: exception.StatusCode
                    == AzurPilotNativeBridge.StatusProviderUnavailable, detail: relative, inner: exception);
            }
        }

        throw last ?? new CaptureBackendException("nemu_dll_not_found", unsupported: true);
    }

    /// <summary>Проверяет путь DLL: внутри установки, обычный файл без reparse point, PE32+ AMD64.</summary>
    internal static string? Validate(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(installation, StringComparison.OrdinalIgnoreCase))
        {
            return "nemu_dll_outside_installation";
        }

        try
        {
            FileInfo file = new(full);
            if (!file.Exists)
            {
                return "nemu_dll_not_found";
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "nemu_dll_reparse_point";
            }

            using FileStream stream = file.OpenRead();
            Span<byte> header = stackalloc byte[0x400];
            int read = stream.Read(header);
            if (read < 0x40 || header[0] != 'M' || header[1] != 'Z')
            {
                return "nemu_dll_not_pe";
            }

            int pe = BitConverter.ToInt32(header[0x3C..]);
            return pe <= 0 || pe + 6 > read || header[pe] != 'P' || header[pe + 1] != 'E'
                ? "nemu_dll_not_pe"
                : BitConverter.ToUInt16(header[(pe + 4)..]) != 0x8664 ? "nemu_dll_not_x64" : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "nemu_dll_unreadable";
        }
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        NativeNemuSafeHandle session = _session ?? throw new CaptureBackendException("session_closed");
        CaptureMoment started = Now();
        NativeFrame frame;
        try
        {
            frame = await CallAsync(() => AzurPilotNativeBridge.CaptureNemu(session, 0, NativeSourcePixelFormat.Rgba8888,
                flipVertical: true), cancellationToken).ConfigureAwait(false);
        }
        catch (NativeCaptureSourceException exception)
        {
            throw new CaptureBackendException(exception.StatusCode == AzurPilotNativeBridge.StatusProviderFailure
                ? "nemu_capture_failed" : "nemu_frame_invalid", inner: exception)
            { SessionBroken = exception.StatusCode == AzurPilotNativeBridge.StatusProviderFailure };
        }

        // Пиксели получены и преобразованы одним вызовом DLL: отдельной фазы декодирования нет.
        return Complete(frame, started, Clock.GetTimestamp(), 0);
    }

    private async Task<T> CallAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.TryAdd(() =>
            {
                try
                {
                    _ = completion.TrySetResult(call());
                }
                catch (Exception exception)
                {
                    _ = completion.TrySetException(exception);
                }
            }))
        {
            throw new CaptureBackendException("session_closed");
        }

        try
        {
            return await completion.Task.WaitAsync(CallTimeout, Clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Вызов DLL продолжается на своём потоке: handle нельзя освобождать, пока он не вернётся.
            Broken = true;
            _session?.SetHandleAsInvalid();
            _session = null;
            throw new CaptureBackendException("nemu_call_timeout");
        }
    }

    private void Run()
    {
        foreach (Action action in _work.GetConsumingEnumerable())
        {
            action();
        }
    }

    protected override ValueTask CloseTransportAsync()
    {
        _work.CompleteAdding();
        if (_session is NativeNemuSafeHandle session)
        {
            // Освобождение идёт после всех поставленных вызовов, когда поток DLL завершился, а не параллельно им.
            if (_thread is null || _thread.Join(CallTimeout))
            {
                session.Dispose();
            }
            else
            {
                session.SetHandleAsInvalid();
            }
        }

        return ValueTask.CompletedTask;
    }
}
