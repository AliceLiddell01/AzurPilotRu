using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzurPilot.Core;
using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;

namespace AzurPilot.Windows.Capture;

/// <summary>Наблюдаемый выбор транспорта одного захвата.</summary>
/// <param name="InstanceId">Identity target.</param>
/// <param name="Requested">Значение настройки: auto или метод.</param>
/// <param name="Method">Транспорт, выполнивший (или выполняющий) захват.</param>
/// <param name="Reason">Машинная причина выбора.</param>
/// <param name="FallbackFrom">Транспорт, от которого произошёл откат, либо null.</param>
public sealed record ScreenshotSelection(MuMuInstanceId InstanceId, string Requested, ScreenshotMethod Method,
    string Reason, ScreenshotMethod? FallbackFrom = null);

/// <summary>Открытый транспорт для измерений: тот же код захвата, что в production, без выбора и наблюдателя.</summary>
public sealed class ScreenshotTransport : IAsyncDisposable
{
    private readonly ScreenshotSession _session;

    internal ScreenshotTransport(ScreenshotSession session)
    {
        _session = session;
    }

    /// <summary>Метод транспорта.</summary>
    public ScreenshotMethod Method => _session.Method;

    /// <summary>Длительность подготовки сессии.</summary>
    public TimeSpan Setup => _session.Setup;

    /// <summary>Ограниченные факты сессии (версии зависимостей, выбранная DLL и подобное).</summary>
    public IReadOnlyDictionary<string, string> Facts => _session.Facts;

    /// <summary>Сессия сломана и дальнейшие захваты бессмысленны.</summary>
    public bool Broken => _session.Broken;

    /// <summary>Один захват нового кадра.</summary>
    public Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureAsync(CancellationToken cancellationToken)
        => _session.CaptureAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

/// <summary>
/// Единственный production-владелец захвата снимков: реализует <see cref="IAndroidScreenshotHost"/>, выбирает
/// транспорт по настройке (<c>auto</c> или явный метод), держит открытые сессии по target и освобождает их.
/// </summary>
/// <remarks>
/// <para>
/// <c>auto</c> использует ADB, пока для отпечатка target нет калибровки, доказанной измерением на главном
/// экране; калиброванный метод проверяется по геометрии каждого кадра. Отказ калиброванного метода в
/// <c>auto</c> — наблюдаемый откат на ADB с причиной. Явно выбранный метод не подменяется: его отказ
/// возвращается вызывающему как есть.
/// </para>
/// <para>
/// Отпечаток: identity, версия MuMu, версия и SDK Android, SHA-256 manifest зависимостей и версия ABI. Изменение
/// любого из них делает калибровку недействительной; изменение разрешения обнаруживается по кадру.
/// </para>
/// </remarks>
public sealed class ScreenshotCaptureRouter : IAndroidScreenshotHost, IAsyncDisposable
{
    private readonly AndroidWindowsHost _android;
    private readonly TimeProvider _clock;
    private readonly ScreenshotMethodSetting _setting;
    private readonly ScreenshotCalibrationStore _calibration;
    private readonly IAndroidScreenshotObserver? _observer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(MuMuInstanceId, AndroidEndpoint), TargetState> _targets = [];

    /// <summary>Создаёт маршрутизатор поверх существующего Android host.</summary>
    public ScreenshotCaptureRouter(AndroidWindowsHost android, TimeProvider clock, ScreenshotMethodSetting setting,
        ScreenshotCalibrationStore calibration, IAndroidScreenshotObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(android);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(calibration);
        _android = android;
        _clock = clock;
        _setting = setting;
        _calibration = calibration;
        _observer = observer;
    }

    /// <summary>Значение настройки метода.</summary>
    public ScreenshotMethodSetting Setting => _setting;

    /// <summary>Хранилище калибровки auto.</summary>
    public ScreenshotCalibrationStore Calibration => _calibration;

    /// <summary>Последний выбор транспорта; null до первого захвата.</summary>
    public ScreenshotSelection? LastSelection { get; private set; }

    /// <inheritdoc />
    public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(MuMuInstanceId instanceId,
        AndroidEndpoint endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ScreenshotTarget? target = _android.GetScreenshotTarget(instanceId, endpoint);
            if (target is null)
            {
                // Порядок контракта нарушен (ADB не обнаружен): тот же отказ, что и у прежнего пути захвата.
                return await _android.CaptureScreenshotAsync(instanceId, endpoint, cancellationToken).ConfigureAwait(false);
            }

            TargetState state = GetState(target);
            (ScreenshotMethod method, string reason, ScreenshotCalibration? calibration) =
                await ChooseAsync(state, cancellationToken).ConfigureAwait(false);
            Select(new(instanceId, _setting.ToString(), method, reason));
            ApplicationResult<CapturedAndroidScreenshot> result = await CaptureWithAsync(state, method, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess && calibration is not null && method != ScreenshotMethod.Adb
                && (result.Value!.Info.FrameInfo.Width != calibration.Width
                    || result.Value.Info.FrameInfo.Height != calibration.Height))
            {
                // Разрешение изменилось после калибровки: выбор больше не доказан для этих условий.
                result.Value.Dispose();
                _calibration.Invalidate(calibration.Fingerprint);
                state.Rejected = method;
                return await FallbackAsync(state, method, "resolution_changed", cancellationToken).ConfigureAwait(false);
            }

            if (result.IsFailure && _setting.IsAutomatic && method != ScreenshotMethod.Adb)
            {
                state.Rejected = method;
                string failure = result.FailureInfo!.Details?.GetValueOrDefault("reason") ?? result.FailureInfo.Code;
                return await FallbackAsync(state, method, "method_failed:" + failure, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>Открывает отдельный транспорт для измерения; production-сессии и выбор не затрагиваются.</summary>
    public async Task<ApplicationResult<ScreenshotTransport>> OpenTransportAsync(MuMuInstanceId instanceId,
        AndroidEndpoint endpoint, ScreenshotMethod method, CancellationToken cancellationToken)
    {
        ScreenshotTarget? target = _android.GetScreenshotTarget(instanceId, endpoint);
        if (target is null)
        {
            return ApplicationResult<ScreenshotTransport>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidAdbUnavailable,
                Message = "Bundled ADB не обнаружен до открытия транспорта снимков.",
            });
        }

        ApplicationResult<ScreenshotSession> session = await OpenSessionAsync(
            new ScreenshotSessionContext(target, _android.ProcessRunner, _clock, _android), method, cancellationToken)
            .ConfigureAwait(false);
        return session.IsFailure
            ? ApplicationResult<ScreenshotTransport>.Failure(session.FailureInfo!)
            : ApplicationResult<ScreenshotTransport>.Success(new ScreenshotTransport(session.Value!));
    }

    /// <summary>Отпечаток условий target для калибровки auto.</summary>
    public async Task<ApplicationResult<string>> FingerprintAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ScreenshotTarget? target = _android.GetScreenshotTarget(instanceId, endpoint);
        if (target is null)
        {
            return ApplicationResult<string>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidAdbUnavailable,
                Message = "Bundled ADB не обнаружен.",
            });
        }

        try
        {
            return ApplicationResult<string>.Success(await GetState(target).FingerprintAsync(cancellationToken)
                .ConfigureAwait(false));
        }
        catch (CaptureBackendException exception)
        {
            return ApplicationResult<string>.Failure(ScreenshotCaptureFailures.From(exception, ScreenshotMethod.Adb,
                instanceId, endpoint));
        }
    }

    /// <summary>
    /// Освобождает ресурсы устройства (helper, forward, reverse), записанные завершившимися процессами AzurPilot,
    /// например worker-ом измерения, остановленным по сроку. Ресурсы живых процессов не затрагиваются.
    /// </summary>
    public async Task<ApplicationResult<int>> ReleaseOrphanedResourcesAsync(MuMuInstanceId instanceId,
        AndroidEndpoint endpoint, CancellationToken cancellationToken)
    {
        ScreenshotTarget? target = _android.GetScreenshotTarget(instanceId, endpoint);
        if (target is null)
        {
            return ApplicationResult<int>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.AndroidAdbUnavailable,
                Message = "Bundled ADB не обнаружен.",
            });
        }

        ScreenshotSessionContext context = new(target, _android.ProcessRunner, _clock, _android);
        int released = 0;
        foreach (CaptureHelperResource orphan in context.Ledger.TakeOrphans())
        {
            await context.ReleaseAsync(orphan, cancellationToken).ConfigureAwait(false);
            released++;
        }

        return ApplicationResult<int>.Success(released);
    }

    private TargetState GetState(ScreenshotTarget target)
    {
        if (!_targets.TryGetValue((target.InstanceId, target.Endpoint), out TargetState? state))
        {
            state = new TargetState(new ScreenshotSessionContext(target, _android.ProcessRunner, _clock, _android));
            _targets[(target.InstanceId, target.Endpoint)] = state;
        }

        return state;
    }

    private async Task<(ScreenshotMethod, string, ScreenshotCalibration?)> ChooseAsync(TargetState state,
        CancellationToken cancellationToken)
    {
        if (_setting.Method is ScreenshotMethod explicitMethod)
        {
            return (explicitMethod, "explicit_setting", null);
        }

        string fingerprint;
        try
        {
            fingerprint = await state.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CaptureBackendException exception)
        {
            return (ScreenshotMethod.Adb, "bootstrap_fingerprint_unavailable:" + exception.Reason, null);
        }

        ScreenshotCalibration? calibration = _calibration.Find(fingerprint);
        if (calibration is null || !ScreenshotMethodNames.TryParse(calibration.Method, out ScreenshotMethod method))
        {
            return (ScreenshotMethod.Adb, "bootstrap_calibration_missing", null);
        }

        return state.Rejected == method
            ? (ScreenshotMethod.Adb, "calibrated_method_rejected_in_process", null)
            : (method, "calibrated:" + calibration.RunId, calibration);
    }

    private async Task<ApplicationResult<CapturedAndroidScreenshot>> FallbackAsync(TargetState state,
        ScreenshotMethod from, string reason, CancellationToken cancellationToken)
    {
        Select(new(state.Context.Target.InstanceId, _setting.ToString(), ScreenshotMethod.Adb, reason, from));
        return await CaptureWithAsync(state, ScreenshotMethod.Adb, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureWithAsync(TargetState state,
        ScreenshotMethod method, CancellationToken cancellationToken)
    {
        MuMuInstanceId instanceId = state.Context.Target.InstanceId;
        AndroidEndpoint endpoint = state.Context.Target.Endpoint;
        if (!state.Sessions.TryGetValue(method, out ScreenshotSession? session))
        {
            ApplicationResult<ScreenshotSession> opened = await OpenSessionAsync(state.Context, method, cancellationToken)
                .ConfigureAwait(false);
            if (opened.IsFailure)
            {
                Notify(o => o.CaptureFailed(instanceId, endpoint, _clock.GetTimestamp(), opened.FailureInfo!));
                return ApplicationResult<CapturedAndroidScreenshot>.Failure(opened.FailureInfo!);
            }

            session = opened.Value!;
            state.Sessions[method] = session;
        }

        long started = _clock.GetTimestamp();
        Notify(o => o.CaptureStarted(instanceId, endpoint, started));
        ApplicationResult<CapturedAndroidScreenshot> result;
        try
        {
            result = await session.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Notify(o => o.CaptureFailed(instanceId, endpoint, started, new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = "Захват отменён.",
            }));
            throw;
        }

        if (session.Broken)
        {
            _ = state.Sessions.Remove(method);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (result.IsFailure)
        {
            Notify(o => o.CaptureFailed(instanceId, endpoint, started, result.FailureInfo!));
            return result;
        }

        CapturedAndroidScreenshot screenshot = result.Value!;
        Notify(o => o.CaptureCompleted(screenshot.Info));
        if (_observer is not null)
        {
            screenshot.AttachObserver(_observer);
        }

        return result;
    }

    private async Task<ApplicationResult<ScreenshotSession>> OpenSessionAsync(ScreenshotSessionContext context,
        ScreenshotMethod method, CancellationToken cancellationToken)
    {
        ScreenshotSession session = method switch
        {
            ScreenshotMethod.Adb => new AdbPngSession(context),
            ScreenshotMethod.AdbNc => new AdbNcSession(context),
            ScreenshotMethod.AScreenCap => new AScreenCapSession(context, netcat: false),
            ScreenshotMethod.AScreenCapNc => new AScreenCapSession(context, netcat: true),
            ScreenshotMethod.Uiautomator2 => new Uiautomator2Session(context),
            ScreenshotMethod.DroidCast => new DroidCastSession(context, raw: false),
            ScreenshotMethod.DroidCastRaw => new DroidCastSession(context, raw: true),
            ScreenshotMethod.Scrcpy => new ScrcpySession(context),
            ScreenshotMethod.NemuIpc => new NemuIpcSession(context),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Неизвестный метод снимка."),
        };
        long started = _clock.GetTimestamp();
        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            session.Setup = _clock.GetElapsedTime(started);
            return ApplicationResult<ScreenshotSession>.Success(session);
        }
        catch (Exception exception) when (exception is CaptureBackendException or NativeCaptureSourceException
            or AzurPilotNativeBoundaryException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            CaptureBackendException failure = exception as CaptureBackendException
                ?? new CaptureBackendException("native_source_unavailable", inner: exception);
            return ApplicationResult<ScreenshotSession>.Failure(ScreenshotCaptureFailures.From(failure, method,
                context.Target.InstanceId, context.Target.Endpoint));
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Select(ScreenshotSelection selection)
    {
        if (LastSelection is { } last && last.Method == selection.Method && last.Reason == selection.Reason
            && last.InstanceId == selection.InstanceId)
        {
            return;
        }

        LastSelection = selection;
        Notify(o => o.MethodSelected(selection));
    }

    private void Notify(Action<IAndroidScreenshotObserver> notify)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            notify(_observer);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Наблюдатель диагностический: его отказ не меняет исход захвата.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (TargetState state in _targets.Values)
            {
                foreach (ScreenshotSession session in state.Sessions.Values)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                state.Sessions.Clear();
            }

            _targets.Clear();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private sealed class TargetState(ScreenshotSessionContext context)
    {
        private string? _fingerprint;

        internal ScreenshotSessionContext Context => context;

        internal Dictionary<ScreenshotMethod, ScreenshotSession> Sessions { get; } = [];

        internal ScreenshotMethod? Rejected { get; set; }

        internal async Task<string> FingerprintAsync(CancellationToken cancellationToken)
        {
            if (_fingerprint is not null)
            {
                return _fingerprint;
            }

            CaptureDeviceFacts facts = await context.FactsAsync(cancellationToken).ConfigureAwait(false);
            string manifest;
            try
            {
                manifest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                    CaptureDependencies.DirectoryName, CaptureDependencies.ManifestFileName))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                manifest = "missing";
            }

            string source = string.Join('|', context.Target.InstanceId.ToString(),
                context.Target.Installation?.Version ?? "unknown", facts.AndroidRelease,
                facts.SdkLevel.ToString(CultureInfo.InvariantCulture), facts.Abi, manifest,
                NativeBoundaryContract.ExpectedAbiVersionString);
            _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
            return _fingerprint;
        }
    }
}
