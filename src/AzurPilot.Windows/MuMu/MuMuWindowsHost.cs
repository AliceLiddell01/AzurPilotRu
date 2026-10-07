using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Host-side поверхность MuMu на Windows: связывает Windows-адаптеры с платформенно-независимым
/// контрактом <see cref="IMuMuHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// Реализация ничего не делает сама: обнаружение установки выполняет
/// <see cref="MuMuInstallationDiscovery"/>, разбор ответов и построение аргументов —
/// <see cref="MuMuManagerClient"/>, запуск процесса — <see cref="IWindowsProcessRunner"/>, а правило
/// состояния — <see cref="MuMuPlayerStateMap"/>. Второй parser, второй набор аргументов, второй probing
/// реестра и файловой системы и второй запуск процесса здесь не заводятся.
/// </para>
/// <para>
/// Состояние экземпляра выводится только из ответа control surface про этот экземпляр: существование
/// «какого-то процесса MuMu» доказательством не является, поэтому авторитетное наблюдение всегда
/// адресуется конкретной identity.
/// </para>
/// <para>
/// Методы синхронные по контракту Core, а граница процесса асинхронна: host дожидается ровно одной
/// команды control surface и не добавляет к ней ни повторных попыток, ни ожиданий, ни завершения
/// процессов.
/// </para>
/// <para>
/// Отказы адаптеров пробрасываются без изменений: host не пересоздаёт ожидаемые MuMu-отказы и не
/// подменяет конкретный код общим.
/// </para>
/// </remarks>
public sealed class MuMuWindowsHost : IMuMuHost
{
    private const string AbsentPlayerState = "absent";

    private readonly MuMuInstallationDiscovery _discovery;
    private readonly IWindowsProcessRunner _processRunner;
    private readonly TimeSpan _commandTimeout;
    private readonly ILogger<MuMuWindowsHost> _logger;

    /// <summary>Создаёт host-side поверхность MuMu с дедлайном команды по умолчанию.</summary>
    /// <param name="registrySource">Источник uninstall-записей реестра.</param>
    /// <param name="metadataSource">Источник документов install metadata.</param>
    /// <param name="fileSystemProbe">Граница файловой системы.</param>
    /// <param name="processRunner">Граница запуска процесса control surface.</param>
    /// <param name="logger">Логгер существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    public MuMuWindowsHost(
        IMuMuInstallationRegistrySource registrySource,
        IMuMuInstallMetadataSource metadataSource,
        IMuMuFileSystemProbe fileSystemProbe,
        IWindowsProcessRunner processRunner,
        ILogger<MuMuWindowsHost> logger)
        : this(
            registrySource,
            metadataSource,
            fileSystemProbe,
            processRunner,
            MuMuManagerClient.DefaultCommandTimeout,
            logger)
    {
    }

    /// <summary>Создаёт host-side поверхность MuMu с явным дедлайном команды.</summary>
    /// <param name="registrySource">Источник uninstall-записей реестра.</param>
    /// <param name="metadataSource">Источник документов install metadata.</param>
    /// <param name="fileSystemProbe">Граница файловой системы.</param>
    /// <param name="processRunner">Граница запуска процесса control surface.</param>
    /// <param name="commandTimeout">Дедлайн одной команды control surface.</param>
    /// <param name="logger">Логгер существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public MuMuWindowsHost(
        IMuMuInstallationRegistrySource registrySource,
        IMuMuInstallMetadataSource metadataSource,
        IMuMuFileSystemProbe fileSystemProbe,
        IWindowsProcessRunner processRunner,
        TimeSpan commandTimeout,
        ILogger<MuMuWindowsHost> logger)
    {
        ArgumentNullException.ThrowIfNull(registrySource);
        ArgumentNullException.ThrowIfNull(metadataSource);
        ArgumentNullException.ThrowIfNull(fileSystemProbe);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(logger);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "Дедлайн команды control surface должен быть положительным.");
        }

        _discovery = new MuMuInstallationDiscovery(registrySource, metadataSource, fileSystemProbe);
        _processRunner = processRunner;
        _commandTimeout = commandTimeout;
        _logger = logger;
    }

    /// <inheritdoc />
    public ApplicationResult<MuMuInstallation> DiscoverInstallation()
    {
        ApplicationResult<MuMuInstallationDiscoveryResult> discovery = _discovery.Discover();

        if (discovery.IsFailure && discovery.FailureInfo is ApplicationFailure discoveryFailure)
        {
            // Отказ адаптера обнаружения уже точен: host не подменяет его своим кодом.
            LogDiscoveryFailed(discoveryFailure.Code, installationsFound: 0, rejectedCandidates: 0);
            return ApplicationResult<MuMuInstallation>.Failure(discoveryFailure);
        }

        MuMuInstallationDiscoveryResult result = discovery.Value!;

        if (result.Status == MuMuInstallationDiscoveryStatus.Single)
        {
            MuMuDiscoveredInstallation discovered = result.Installations[0];

            if (_logger.IsEnabled(LogLevel.Information))
            {
                MuMuWindowsHostLog.InstallationDiscovered(
                    _logger,
                    discovered.Version,
                    result.RejectedCandidates.Count);
            }

            return ApplicationResult<MuMuInstallation>.Success(new MuMuInstallation(
                discovered.Version,
                discovered.InstallRoot,
                discovered.ControlSurface.ExecutablePath));
        }

        ApplicationFailure failure = result.Status == MuMuInstallationDiscoveryStatus.Ambiguous
            ? MuMuHostFailures.InstallationAmbiguous(result.Installations.Count)
            : ResolveMissingInstallationFailure(result);

        LogDiscoveryFailed(failure.Code, result.Installations.Count, result.RejectedCandidates.Count);
        return ApplicationResult<MuMuInstallation>.Failure(failure);
    }

    /// <inheritdoc />
    public ApplicationResult<IReadOnlyList<MuMuInstance>> EnumerateInstances(MuMuInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        ApplicationResult<MuMuInstanceEnumeration> enumeration =
            ClientFor(installation).EnumerateInstancesAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (enumeration.IsFailure && enumeration.FailureInfo is ApplicationFailure enumerationFailure)
        {
            return ApplicationResult<IReadOnlyList<MuMuInstance>>.Failure(enumerationFailure);
        }

        IReadOnlyList<MuMuInstanceInfo> found = enumeration.Value!.Instances;
        List<MuMuInstance> instances = new(found.Count);

        foreach (MuMuInstanceInfo info in found)
        {
            // Отображаемое имя и версия Android — сведения для оператора: если провайдер их не сообщил,
            // значение остаётся пустым, а не додумывается по номеру экземпляра.
            instances.Add(new MuMuInstance(
                info.Id,
                info.DisplayName ?? string.Empty,
                info.AndroidVersion ?? string.Empty));
        }

        return ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(instances);
    }

    /// <inheritdoc />
    public ApplicationResult<MuMuInstanceState> ObserveInstanceState(
        MuMuInstallation installation,
        MuMuInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(installation);

        ApplicationResult<MuMuInstanceQueryResult> query =
            ClientFor(installation).QueryInstanceAsync(instance, CancellationToken.None).GetAwaiter().GetResult();

        if (query.IsFailure && query.FailureInfo is ApplicationFailure queryFailure)
        {
            return ApplicationResult<MuMuInstanceState>.Failure(queryFailure);
        }

        MuMuInstanceQueryResult result = query.Value!;

        if (result.Instance is not MuMuInstanceInfo info)
        {
            // Провайдер не сообщил сведений об экземпляре. Доказанное «экземпляра нет» отличается от
            // отказа с недоказанным смыслом: второе закрыто, потому что состояние вывести нельзя.
            MuMuProviderError providerError = result.ProviderError!;

            return ApplicationResult<MuMuInstanceState>.Failure(result.IsIndexNotFound
                ? MuMuHostFailures.InstanceNotFound(instance)
                : MuMuHostFailures.InstanceStateUnavailable(instance, providerError.Code));
        }

        return ApplicationResult<MuMuInstanceState>.Success(
            new MuMuInstanceState(info.State, BuildEvidence(info)));
    }

    /// <inheritdoc />
    public ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(
        MuMuInstallation installation,
        MuMuInstanceId instance,
        MuMuLifecycleMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        MuMuControlCommand command = mutation switch
        {
            MuMuLifecycleMutation.Start => MuMuControlCommand.Launch,
            MuMuLifecycleMutation.Stop => MuMuControlCommand.Shutdown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation),
                mutation,
                "Host-примитив mutation MuMu не определён."),
        };

        ApplicationResult<MuMuControlOutcome> control = ClientFor(installation)
            .ExecuteControlAsync(instance, command, cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (control.IsFailure && control.FailureInfo is ApplicationFailure controlFailure)
        {
            return ApplicationResult<MuMuLifecycleCommandOutcome>.Failure(controlFailure);
        }

        MuMuControlOutcome outcome = control.Value!;

        if (outcome.IsIndexNotFound)
        {
            // Провайдер доказанно сообщил, что запрошенного экземпляра нет: mutation не могла быть
            // выполнена над ним, и это точный ожидаемый отказ, а не недоказанный postcondition.
            return ApplicationResult<MuMuLifecycleCommandOutcome>.Failure(
                MuMuHostFailures.InstanceNotFound(instance));
        }

        // Код выхода возвращается как evidence и не превращается в успех lifecycle: провайдер отвечает о
        // приёме команды, а решение о terminal postcondition принимает orchestration Core.
        return ApplicationResult<MuMuLifecycleCommandOutcome>.Success(
            new MuMuLifecycleCommandOutcome(outcome.ExitCode, outcome.BoundedOutput));
    }

    private static ApplicationFailure ResolveMissingInstallationFailure(MuMuInstallationDiscoveryResult result)
    {
        List<string> reasons = [];
        bool controlSurfaceMissing = false;

        foreach (MuMuRejectedInstallationCandidate candidate in result.RejectedCandidates)
        {
            if (candidate.Reason == MuMuInstallationRejectionReasons.ControlExecutableMissing)
            {
                // Кандидат с существующим каталогом установки, но без поддерживаемой точки входа:
                // это не «MuMu не установлен», а «раскладка установки не поддерживается».
                controlSurfaceMissing = true;
            }

            if (!reasons.Contains(candidate.Reason, StringComparer.Ordinal))
            {
                reasons.Add(candidate.Reason);
            }
        }

        return controlSurfaceMissing
            ? MuMuHostFailures.ControlSurfaceMissing(result.RejectedCandidates.Count)
            : MuMuHostFailures.InstallationNotFound(result.RejectedCandidates.Count, reasons);
    }

    private static string BuildEvidence(MuMuInstanceInfo info)
        => BoundedDiagnosticText.Bounded(
            "index=" + info.Id.Index
            + ";player_state=" + (info.RawPlayerState ?? AbsentPlayerState)
            + ";is_process_started=" + (info.IsProcessStarted ? "true" : "false")
            + ";is_android_started=" + (info.IsAndroidStarted ? "true" : "false"));

    private MuMuManagerClient ClientFor(MuMuInstallation installation)
        => MuMuManagerClient.ForInstallation(_processRunner, installation, _commandTimeout);

    private void LogDiscoveryFailed(string failureCode, int installationsFound, int rejectedCandidates)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            MuMuWindowsHostLog.InstallationDiscoveryFailed(
                _logger,
                failureCode,
                installationsFound,
                rejectedCandidates);
        }
    }
}
