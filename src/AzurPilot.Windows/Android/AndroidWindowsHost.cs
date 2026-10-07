using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Host-side поверхность Android на Windows: bundled ADB обнаруженной установки MuMuPlayer и lifecycle
/// игры Azur Lane на точном endpoint-е.
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
/// Методы синхронные по контракту Core, а граница процесса асинхронна: host дожидается ровно одной
/// команды ADB и не добавляет к ней ни повторных попыток, ни ожиданий, ни завершения сервера ADB.
/// Отмена выполняется только там, где её несёт контракт — <see cref="ConnectTransport"/> и
/// <see cref="RequestGameMutation"/>.
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
public sealed class AndroidWindowsHost : IAndroidHost
{
    private readonly IWindowsProcessRunner _processRunner;
    private readonly AndroidEndpointResolver _endpointResolver;
    private readonly TimeSpan _commandTimeout;
    private AndroidAdbExecutable? _adbExecutable;

    /// <summary>Создаёт host-side поверхность Android с дедлайном команды по умолчанию.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processRunner"/> равен <see langword="null"/>.</exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner)
        : this(processRunner, AdbClient.DefaultCommandTimeout)
    {
    }

    /// <summary>Создаёт host-side поверхность Android с явным дедлайном команды.</summary>
    /// <param name="processRunner">Общая граница запуска процесса.</param>
    /// <param name="commandTimeout">Дедлайн одной команды ADB и одной команды чтения сведений.</param>
    /// <exception cref="ArgumentNullException"><paramref name="processRunner"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public AndroidWindowsHost(IWindowsProcessRunner processRunner, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(processRunner);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "Дедлайн команды ADB должен быть положительным.");
        }

        _processRunner = processRunner;
        _endpointResolver = new AndroidEndpointResolver(processRunner, commandTimeout);
        _commandTimeout = commandTimeout;
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
    {
        AndroidAdbExecutable? discovered = Volatile.Read(ref _adbExecutable);

        if (discovered is null)
        {
            // Обращение к команде ADB до обнаружения executable — нарушение порядка контракта, а не
            // ожидаемое состояние устройства.
            throw new InvalidOperationException(
                "Команда ADB запрошена до обнаружения bundled ADB: сначала вызывается DiscoverAdbExecutable.");
        }

        return new AdbClient(_processRunner, discovered.Path, _commandTimeout)
            .RunAsync(arguments, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

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
