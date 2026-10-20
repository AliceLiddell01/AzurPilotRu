using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Host-side поверхность Android на Windows: bundled ADB обнаруженной установки MuMuPlayer, lifecycle
/// игры и read-only screenshot на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// Реализация ничего не разбирает сама: путь bundled ADB выводит <see cref="AndroidInstallationLayout"/>,
/// точный endpoint — <see cref="AndroidEndpointResolver"/>, форму аргументов —
/// <see cref="AdbCommandBuilder"/>, запуск команды — <see cref="AdbClient"/> поверх общей границы
/// запуска процесса, разбор ответа — <see cref="AdbResponseParser"/>. Второй parser, второй набор
/// аргументов и второй запуск процесса здесь не заводятся.
/// </para>
/// <para>
/// Target-explicit адресация обязательна: каждая команда несёт точный endpoint, и наблюдение относится
/// ровно к нему и запрошенному пакету. Значение по умолчанию вместо недоказанного не подставляется:
/// «не удалось спросить» возвращается как недоказанное наблюдение, а не как доказанное отсутствие.
/// </para>
/// <para>
/// Lifecycle методы синхронные по контракту Core, screenshot surface асинхронна. Host не добавляет
/// к запрошенной команде повторных попыток, ожиданий или завершения сервера ADB. Отмена передаётся
/// там, где её несёт контракт: <see cref="ConnectTransport"/>, <see cref="RequestGameMutation"/>
/// и <see cref="CaptureScreenshotAsync"/>.
/// </para>
/// <para>
/// Наблюдение foreground сообщает точный наблюдённый ПАКЕТ, а сравнение выполняется по пакету, не по
/// компоненту: после запуска launcher-компонента foreground может оказаться другой activity того же
/// пакета, поэтому сравнение компонентов не доказало бы postcondition никогда. «Foreground» здесь
/// означает «наблюдённый foreground package совпал с exact product package
/// <see cref="AzurLaneProduct.Package"/>».
/// </para>
/// <para>
/// Отказы адаптеров пробрасываются без изменений: host не пересоздаёт ожидаемые отказы и не подменяет
/// конкретный код общим <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// </remarks>
public sealed partial class AndroidWindowsHost : IAndroidHost, IAndroidScreenshotHost, IAndroidGameInputHost
{
    private readonly IWindowsProcessRunner _processRunner;
    private readonly AndroidEndpointResolver _endpointResolver;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly IAndroidScreenshotObserver? _screenshotObserver;
    private AndroidAdbExecutable? _adbExecutable;

    /// <summary>Создаёт host-side поверхность Android с дедлайном команды по умолчанию и общими часами.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <param name="timeProvider">Общий источник времени процесса.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="processRunner"/> или <paramref name="timeProvider"/> равны <see langword="null"/>.
    /// </exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner, TimeProvider timeProvider)
        : this(processRunner, AdbClient.DefaultCommandTimeout, timeProvider)
    {
    }

    /// <summary>Создаёт host с дедлайном команды по умолчанию и необязательным наблюдателем capture.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <param name="timeProvider">Общий источник времени процесса.</param>
    /// <param name="screenshotObserver">Диагностический наблюдатель; null — без наблюдения.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="processRunner"/> или <paramref name="timeProvider"/> равны <see langword="null"/>.
    /// </exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner, TimeProvider timeProvider,
        IAndroidScreenshotObserver? screenshotObserver)
        : this(processRunner, AdbClient.DefaultCommandTimeout, timeProvider, screenshotObserver)
    {
    }

    /// <summary>Создаёт host-side поверхность Android с явным дедлайном команды.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <param name="commandTimeout">Дедлайн одной команды ADB и одной команды чтения сведений.</param>
    /// <param name="timeProvider">Общий источник времени процесса.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="processRunner"/> или <paramref name="timeProvider"/> равны <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner, TimeSpan commandTimeout, TimeProvider timeProvider)
        : this(processRunner, commandTimeout, timeProvider, null)
    {
    }

    /// <summary>Создаёт host с необязательным наблюдателем реальных screenshot capture.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <param name="commandTimeout">Дедлайн одной команды ADB и одной команды чтения сведений.</param>
    /// <param name="timeProvider">Общий источник времени процесса.</param>
    /// <param name="screenshotObserver">Диагностический наблюдатель; null — без наблюдения.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="processRunner"/> или <paramref name="timeProvider"/> равны <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner, TimeSpan commandTimeout, TimeProvider timeProvider,
        IAndroidScreenshotObserver? screenshotObserver)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "Дедлайн команды ADB должен быть положительным.");
        }

        _processRunner = processRunner;
        _endpointResolver = new AndroidEndpointResolver(processRunner, commandTimeout);
        _commandTimeout = commandTimeout;
        _timeProvider = timeProvider;
        _screenshotObserver = screenshotObserver;
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidAdbExecutable> DiscoverAdbExecutable(MuMuInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        string executablePath = AndroidInstallationLayout.GetAdbExecutablePath(installation.InstallRoot);
        AdbClient client = new(_processRunner, executablePath, _commandTimeout);

        ApplicationResult<WindowsProcessOutcome> outcome =
            client.RunAsync(AdbCommandBuilder.BuildVersionArguments(), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        if (outcome.IsFailure)
        {
            // Отказ границы процесса уже точен: проекция сообщила его как AndroidAdbUnavailable.
            return ApplicationResult<AndroidAdbExecutable>.Failure(outcome.FailureInfo!);
        }

        string? versionEvidence = AdbResponseParser.ParseAdbVersionEvidence(outcome.Value!);

        if (versionEvidence is null)
        {
            // Форма ответа не распознана: исполняемый файл не подтверждён как bundled ADB.
            return ApplicationResult<AndroidAdbExecutable>.Failure(
                AndroidHostFailures.AdbUnavailable(
                    AndroidHostFailures.ResponseUnrecognizedReason,
                    "форма ответа команды version не распознана"));
        }

        AndroidAdbExecutable discovered = new(executablePath, versionEvidence);
        Volatile.Write(ref _adbExecutable, discovered);

        return ApplicationResult<AndroidAdbExecutable>.Success(discovered);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidEndpoint> ResolveEndpoint(
        MuMuInstallation installation,
        MuMuInstanceId instance)
        => _endpointResolver.ResolveAsync(installation, instance, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
        MuMuInstanceId instanceId,
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        IAndroidScreenshotObserver? observer = _screenshotObserver;
        if (observer is null)
        {
            return await CaptureScreenshotCoreAsync(instanceId, endpoint, null, cancellationToken).ConfigureAwait(false);
        }

        // Наблюдатель видит тот же единственный захват; отказ наблюдателя на захват не влияет.
        long started = 0;
        ApplicationResult<CapturedAndroidScreenshot> result;
        try
        {
            result = await CaptureScreenshotCoreAsync(instanceId, endpoint, timestamp =>
            {
                started = timestamp;
                Observe(() => observer.CaptureStarted(instanceId, endpoint, timestamp));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(() => observer.CaptureFailed(instanceId, endpoint, started, new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = "Захват отменён.",
            }));
            throw;
        }

        if (result.IsFailure)
        {
            Observe(() => observer.CaptureFailed(instanceId, endpoint, started, result.FailureInfo!));
        }
        else
        {
            Observe(() => observer.CaptureCompleted(result.Value!.Info));
        }

        return result;
    }

    private static void Observe(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Наблюдатель диагностический: его отказ не меняет исход захвата.
        }
    }

    private async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotCoreAsync(
        MuMuInstanceId instanceId,
        AndroidEndpoint endpoint,
        Action<long>? started,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId.Index))
        {
            return ScreenshotFailure("instance_invalid", instanceId, endpoint);
        }

        if (string.IsNullOrWhiteSpace(endpoint.Host)
            || endpoint.Port is < AndroidEndpoint.MinPort or > AndroidEndpoint.MaxPort)
        {
            return ScreenshotFailure("endpoint_invalid", instanceId, endpoint);
        }

        // Discovery/cache принадлежит тому же Android host: отдельного поиска executable или target нет.
        AdbClient client = GetAdbClient();
        int maximumPngBytes;
        try
        {
            uint nativeMaximumPngBytes = AzurPilotNativeBridge.GetMaximumPngBytes();
            if (nativeMaximumPngBytes > int.MaxValue)
            {
                return ScreenshotFailure("native_budget_invalid", instanceId, endpoint);
            }

            maximumPngBytes = (int)nativeMaximumPngBytes;
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<CapturedAndroidScreenshot>.Failure(
                NativeBoundaryFailureMapper.Map(exception));
        }

        DateTimeOffset captureStartedAtUtc = _timeProvider.GetUtcNow();
        long captureStartedTimestamp = _timeProvider.GetTimestamp();
        started?.Invoke(captureStartedTimestamp);
        ApplicationResult<WindowsProcessOutcome> command = await client.RunBinaryAsync(
            AdbCommandBuilder.BuildScreenshotArguments(endpoint),
            maximumPngBytes,
            cancellationToken).ConfigureAwait(false);

        if (command.IsFailure)
        {
            return ApplicationResult<CapturedAndroidScreenshot>.Failure(command.FailureInfo!);
        }

        WindowsProcessOutcome outcome = command.Value!;
        if (outcome.ExitCode != 0)
        {
            return ScreenshotFailure("command_failed", instanceId, endpoint, outcome);
        }

        if (outcome.StandardOutputBytesTruncated || outcome.StandardOutputTruncated
            || outcome.StandardOutputBytes.Length > maximumPngBytes)
        {
            return ScreenshotFailure("png_truncated_or_oversized", instanceId, endpoint, outcome);
        }

        if (!outcome.StandardOutputBinaryComplete
            || outcome.StandardOutputBytesRead != outcome.StandardOutputBytes.Length
            || outcome.Duration < TimeSpan.Zero)
        {
            return ScreenshotFailure("binary_output_incomplete", instanceId, endpoint, outcome);
        }

        NativeFrame? frame = null;
        try
        {
            frame = AzurPilotNativeBridge.DecodePng(outcome.StandardOutputBytes);
            NativeFrameInfo info = frame.GetInfo();
            if (!IsValidRgb8FrameInfo(info))
            {
                return ScreenshotFailure("frame_metadata_invalid", instanceId, endpoint, outcome);
            }

            CapturedAndroidScreenshot screenshot = new(
                frame,
                new AndroidScreenshotCaptureInfo(
                    instanceId,
                    endpoint,
                    _timeProvider.GetUtcNow(),
                    outcome.Duration,
                    outcome.StandardOutputBytes.Length,
                    outcome.ExitCode,
                    info)
                {
                    CaptureStartedAtUtc = captureStartedAtUtc,
                    CaptureStartedTimestamp = captureStartedTimestamp,
                },
                _screenshotObserver);
            frame = null;
            return ApplicationResult<CapturedAndroidScreenshot>.Success(screenshot);
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            if (exception is NativeFrameDecodeException)
            {
                return ScreenshotFailure(
                    "native_decode_failed",
                    instanceId,
                    endpoint,
                    outcome,
                    exception);
            }

            return ApplicationResult<CapturedAndroidScreenshot>.Failure(
                NativeBoundaryFailureMapper.Map(exception));
        }
        finally
        {
            // До передачи ownership успешному результату любой выход освобождает созданный frame.
            frame?.Dispose();
        }
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidTransportObservation> QueryTransport(AndroidEndpoint endpoint)
    {
        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildGetStateArguments(endpoint), CancellationToken.None);

        return outcome.IsFailure
            ? ApplicationResult<AndroidTransportObservation>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidTransportObservation>.Success(
                AdbResponseParser.ParseTransport(outcome.Value!, endpoint));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidCommandOutcome> ConnectTransport(
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildConnectArguments(endpoint), cancellationToken);

        return outcome.IsFailure
            ? ApplicationResult<AndroidCommandOutcome>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidCommandOutcome>.Success(
                AdbResponseParser.ToCommandOutcome(outcome.Value!));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidBootObservation> QueryBoot(AndroidEndpoint endpoint)
    {
        ApplicationResult<WindowsProcessOutcome> bootOutcome =
            RunAdb(AdbCommandBuilder.BuildBootCompletedArguments(endpoint), CancellationToken.None);

        if (bootOutcome.IsFailure)
        {
            return ApplicationResult<AndroidBootObservation>.Failure(bootOutcome.FailureInfo!);
        }

        WindowsProcessOutcome boot = bootOutcome.Value!;
        bool shellAvailable = boot.ExitCode == 0;
        int? bootCompleted = AdbResponseParser.ParsePropertyInteger(boot);
        string? androidRelease = null;
        int? sdkLevel = null;

        if (shellAvailable)
        {
            ApplicationResult<WindowsProcessOutcome> releaseOutcome =
                RunAdb(AdbCommandBuilder.BuildAndroidReleaseArguments(endpoint), CancellationToken.None);

            if (releaseOutcome.IsFailure)
            {
                return ApplicationResult<AndroidBootObservation>.Failure(releaseOutcome.FailureInfo!);
            }

            androidRelease = AdbResponseParser.ParsePropertyText(releaseOutcome.Value!);

            ApplicationResult<WindowsProcessOutcome> sdkOutcome =
                RunAdb(AdbCommandBuilder.BuildSdkLevelArguments(endpoint), CancellationToken.None);

            if (sdkOutcome.IsFailure)
            {
                return ApplicationResult<AndroidBootObservation>.Failure(sdkOutcome.FailureInfo!);
            }

            sdkLevel = AdbResponseParser.ParsePropertyInteger(sdkOutcome.Value!);
        }

        return ApplicationResult<AndroidBootObservation>.Success(
            new AndroidBootObservation(
                shellAvailable,
                bootCompleted,
                androidRelease,
                sdkLevel,
                BuildBootEvidence(boot, shellAvailable, bootCompleted, androidRelease, sdkLevel)));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidPackagePresence> QueryPackage(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        ValidatePackage(package);

        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildPackagePathArguments(endpoint, package), CancellationToken.None);

        return outcome.IsFailure
            ? ApplicationResult<AndroidPackagePresence>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidPackagePresence>.Success(
                AdbResponseParser.ParsePackagePresence(outcome.Value!, endpoint));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidLauncherResolution> ResolveLauncher(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        ValidatePackage(package);

        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildLauncherQueryArguments(endpoint, package), CancellationToken.None);

        return outcome.IsFailure
            ? ApplicationResult<AndroidLauncherResolution>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidLauncherResolution>.Success(
                AdbResponseParser.ParseLauncherResolution(outcome.Value!, endpoint, package));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidProcessObservation> ObserveProcesses(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        ValidatePackage(package);

        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildProcessListArguments(endpoint), CancellationToken.None);

        return outcome.IsFailure
            ? ApplicationResult<AndroidProcessObservation>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidProcessObservation>.Success(
                AdbResponseParser.ParseProcessObservation(outcome.Value!, package));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidForegroundObservation> ObserveForeground(AndroidEndpoint endpoint)
    {
        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildWindowDumpArguments(endpoint), CancellationToken.None);

        return outcome.IsFailure
            ? ApplicationResult<AndroidForegroundObservation>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidForegroundObservation>.Success(
                AdbResponseParser.ParseForeground(outcome.Value!, AzurLaneProduct.Package));
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AndroidGameMutation mutation,
        CancellationToken cancellationToken)
    {
        ValidatePackage(package);

        return mutation switch
        {
            AndroidGameMutation.Start => StartGame(endpoint, package, cancellationToken),
            AndroidGameMutation.ForceStop => ForceStopGame(endpoint, package, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation), mutation, "Mutation игры Android не поддерживается."),
        };
    }

    private ApplicationResult<AndroidCommandOutcome> StartGame(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        CancellationToken cancellationToken)
    {
        ApplicationResult<AndroidLauncherResolution> resolution = ResolveLauncher(endpoint, package);

        if (resolution.IsFailure)
        {
            return ApplicationResult<AndroidCommandOutcome>.Failure(resolution.FailureInfo!);
        }

        AndroidLauncherResolution launcher = resolution.Value!;

        if (launcher.Status != AndroidLauncherResolutionStatus.Resolved
            || launcher.Component is not AndroidComponent component)
        {
            // Mutation не адресуется недоказанному компоненту: запуск не выполняется вовсе.
            return ApplicationResult<AndroidCommandOutcome>.Failure(
                launcher.Status == AndroidLauncherResolutionStatus.Ambiguous
                    ? AndroidHostFailures.LauncherAmbiguous(endpoint, package, launcher.MatchingComponentCount)
                    : AndroidHostFailures.LauncherUnresolved(
                        endpoint,
                        package,
                        launcher.Status == AndroidLauncherResolutionStatus.Missing
                            ? AndroidHostFailures.LauncherMissingStateName
                            : AndroidHostFailures.LauncherQueryFailedStateName));
        }

        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildStartGameArguments(endpoint, component), cancellationToken);

        return outcome.IsFailure
            ? ApplicationResult<AndroidCommandOutcome>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidCommandOutcome>.Success(
                AdbResponseParser.ToCommandOutcome(outcome.Value!));
    }

    private ApplicationResult<AndroidCommandOutcome> ForceStopGame(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        CancellationToken cancellationToken)
    {
        ApplicationResult<WindowsProcessOutcome> outcome =
            RunAdb(AdbCommandBuilder.BuildForceStopGameArguments(endpoint, package), cancellationToken);

        return outcome.IsFailure
            ? ApplicationResult<AndroidCommandOutcome>.Failure(outcome.FailureInfo!)
            : ApplicationResult<AndroidCommandOutcome>.Success(
                AdbResponseParser.ToCommandOutcome(outcome.Value!));
    }

    private ApplicationResult<WindowsProcessOutcome> RunAdb(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => GetAdbClient()
            .RunAsync(arguments, cancellationToken)
            .GetAwaiter()
            .GetResult();

    private AdbClient GetAdbClient()
    {
        AndroidAdbExecutable? discovered = Volatile.Read(ref _adbExecutable);

        if (discovered is null)
        {
            // Обращение к команде ADB до обнаружения executable — нарушение порядка контракта, а не
            // ожидаемое состояние устройства.
            throw new InvalidOperationException(
                "Команда ADB запрошена до обнаружения bundled ADB: сначала вызывается DiscoverAdbExecutable.");
        }

        return new AdbClient(_processRunner, discovered.Path, _commandTimeout);
    }

    private static bool IsValidRgb8FrameInfo(NativeFrameInfo info)
    {
        ulong rowBytes = (ulong)info.Width * 3;
        ulong frameBytes = (ulong)info.StrideBytes * info.Height;
        return info.Width > 0
            && info.Height > 0
            && info.PixelFormat == AzurPilotNativeBridge.PixelFormatRgb8
            && info.StrideBytes >= rowBytes
            && info.ByteLength == frameBytes;
    }

    private static ApplicationResult<CapturedAndroidScreenshot> ScreenshotFailure(
        string reason,
        MuMuInstanceId instanceId,
        AndroidEndpoint endpoint,
        WindowsProcessOutcome? outcome = null,
        Exception? exception = null)
        => ApplicationResult<CapturedAndroidScreenshot>.Failure(
            AndroidScreenshotFailures.CaptureFailed(reason, instanceId, endpoint, outcome, exception));

    private static void ValidatePackage(AndroidPackageId package)
    {
        if (string.IsNullOrWhiteSpace(package.Value))
        {
            throw new ArgumentException(
                "Идентификатор пакета не задан: значение по умолчанию не является допустимым пакетом.",
                nameof(package));
        }
    }

    private static string BuildBootEvidence(
        WindowsProcessOutcome boot,
        bool shellAvailable,
        int? bootCompleted,
        string? androidRelease,
        int? sdkLevel)
        => BoundedDiagnosticText.Bounded(
            "exit="
            + boot.ExitCode.ToString(CultureInfo.InvariantCulture)
            + ";shell_available="
            + (shellAvailable ? "true" : "false")
            + ";boot_completed="
            + (bootCompleted is int bootValue ? Count(bootValue) : "not_observed")
            + ";release="
            + (androidRelease ?? "not_observed")
            + ";sdk="
            + (sdkLevel is int sdkValue ? Count(sdkValue) : "not_observed"));

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
