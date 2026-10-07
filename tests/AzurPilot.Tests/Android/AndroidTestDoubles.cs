using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Глобальные операции ADB, которые не должны запрашиваться ни в одном сценарии.
/// </summary>
/// <remarks>
/// Перечень держится в одном месте: и проверки формы аргументов, и проверки host-а поверх подменяемой
/// границы процесса ищут в запрошенных командах одни и те же запрещённые операции, а не свои копии.
/// </remarks>
internal static class AndroidTestOperations
{
    /// <summary>Операции, которых не должно быть ни в нормальном, ни в recovery-сценарии.</summary>
    internal static readonly HashSet<string> ForbiddenGlobal =
    [
        "kill-server",
        "start-server",
        "reconnect",
        "disconnect",
        "wait-for-device",
        "devices",
        "tcpip",
        "reboot",
        "unroot",
        "root",
        "emu",
        "usb",
        "install",
        "uninstall",
        "push",
        "pull",
        "forward",
        "reverse",
        "sideload",
        "pair",
        "mdns",
        "keygen",
        "backup",
        "restore",
        "logcat",
        "bugreport",
        "get-serialno",
        "get-devpath",
    ];

    /// <summary>Требует, чтобы среди аргументов команд не было глобальной операции ADB.</summary>
    /// <param name="commands">Аргументы запрошенных команд в порядке вызова.</param>
    internal static void AssertNoneRequested(IEnumerable<IReadOnlyList<string>> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        foreach (IReadOnlyList<string> command in commands)
        {
            foreach (string argument in command)
            {
                Assert.False(
                    ForbiddenGlobal.Contains(argument),
                    $"Запрошена глобальная операция ADB «{argument}».");
            }
        }
    }
}

/// <summary>
/// Machine-stable ключи bounded details Android-отказов, которыми пользуются проверки.
/// </summary>
/// <remarks>
/// Ключи принадлежат владельцам отказов внутри production-кода и снаружи недоступны, поэтому проверки
/// держат их в одном месте: иначе одна и та же строка разошлась бы по файлам проверок и перестала быть
/// контрактом. Значение ключа — часть machine-stable контракта details, а не свободный текст.
/// </remarks>
internal static class AndroidDetailKeys
{
    internal const string Endpoint = "endpoint";

    internal const string InstanceId = "instance_id";

    internal const string Package = "package";

    internal const string State = "state";

    internal const string TargetState = "target_state";

    internal const string Phase = "phase";

    internal const string Reason = "reason";

    internal const string ExitCode = "exit_code";

    internal const string MatchingComponents = "matching_components";

    internal const string ElapsedMilliseconds = "elapsed_ms";

    internal const string Evidence = "evidence";

    internal const string ExecutableName = "executable_name";

    internal const string StandardOutputCharacters = "stdout_characters";

    internal const string StandardErrorCharacters = "stderr_characters";
}

/// <summary>Machine-stable значения bounded details Android-отказов, которыми пользуются проверки.</summary>
internal static class AndroidDetailValues
{
    internal const string Absent = "absent";

    internal const string Offline = "offline";

    internal const string Device = "device";

    internal const string Unknown = "unknown";

    internal const string Missing = "missing";

    internal const string QueryFailed = "query_failed";

    internal const string Ambiguous = "ambiguous";

    internal const string HostMissing = "host_missing";

    internal const string HostInvalid = "host_invalid";

    internal const string PortMissing = "port_missing";

    internal const string PortOutOfRange = "port_out_of_range";

    internal const string ResponseUnrecognized = "response_unrecognized";

    internal const string CommandTimeout = "command_timeout";

    internal const string ProcessStartFailed = "process_start_failed";

    internal const string TransportPhase = "transport";

    internal const string BootPhase = "boot";

    internal const string PollingPhase = "polling";

    internal const string LauncherPhase = "launcher";

    internal const string GatePhase = "gate";

    internal const string StateNotInstalled = "not_installed";

    internal const string StateStopped = "stopped";

    internal const string StateBackground = "background";

    internal const string StateForeground = "foreground";

    internal const string StateUnknown = "unknown";
}

/// <summary>
/// Пути проверок Android, собранные в runtime из временного каталога.
/// </summary>
/// <remarks>
/// Абсолютные пути проверок не являются константами проекта: буква диска и путь конкретной машины в
/// исходниках проверок не появляются.
/// </remarks>
internal static class AndroidTestPaths
{
    /// <summary>Собирает абсолютный путь проверки под временным каталогом.</summary>
    /// <param name="segments">Сегменты пути.</param>
    /// <returns>Абсолютный путь.</returns>
    internal static string Create(params string[] segments)
        => Path.Combine([Path.GetTempPath(), "azurpilot-android-tests", .. segments]);
}

/// <summary>Запрошенная mutation игры: точный endpoint, пакет и примитив.</summary>
/// <param name="Endpoint">Точный endpoint, которому адресована mutation.</param>
/// <param name="Package">Идентификатор пакета, которому адресована mutation.</param>
/// <param name="Mutation">Запрошенный примитив mutation.</param>
internal readonly record struct AndroidMutationRequest(
    AndroidEndpoint Endpoint,
    AndroidPackageId Package,
    AndroidGameMutation Mutation);

/// <summary>Запрос наблюдения, адресованный точному endpoint-у и запрошенному пакету.</summary>
/// <param name="Endpoint">Точный endpoint, которому адресован запрос.</param>
/// <param name="Package">Идентификатор запрошенного пакета.</param>
internal readonly record struct AndroidPackageRequest(AndroidEndpoint Endpoint, AndroidPackageId Package);

/// <summary>
/// Управляемая подмена host-side поверхности Android.
/// </summary>
/// <remarks>
/// <para>
/// Подменяются только внешние границы: проверки вызывают настоящие orchestration readiness, вывода
/// состояния игры и lifecycle, поэтому реально исполняются bounded polling, deadline, композиция
/// перезапуска и правила отказов. Отдельного fake готового device service нет: double отвечает ровно на
/// примитивы <see cref="IAndroidHost"/> и запоминает, чем именно его спросили.
/// </para>
/// <para>
/// Обработчик незапрошенного примитива не подставляется по умолчанию: проверка обязана явно описать
/// сценарий, иначе легко получить «зелёный» результат из подмены, а не из production-кода.
/// </para>
/// </remarks>
internal sealed class TestAndroidHost : IAndroidHost
{
    private readonly Lock _sync = new();
    private readonly List<MuMuInstallation> _adbDiscoveryRequests = [];
    private readonly List<MuMuInstanceId> _endpointRequests = [];
    private readonly List<AndroidEndpoint> _connectRequests = [];
    private readonly List<AndroidEndpoint> _transportRequests = [];
    private readonly List<AndroidEndpoint> _bootRequests = [];
    private readonly List<AndroidPackageRequest> _packageRequests = [];
    private readonly List<AndroidPackageRequest> _launcherRequests = [];
    private readonly List<AndroidPackageRequest> _processRequests = [];
    private readonly List<AndroidEndpoint> _foregroundRequests = [];
    private readonly List<AndroidMutationRequest> _mutationRequests = [];

    /// <summary>Ответ обнаружения bundled ADB.</summary>
    internal Func<MuMuInstallation, ApplicationResult<AndroidAdbExecutable>>? AdbExecutableHandler { get; set; }

    /// <summary>Ответ разрешения точного endpoint-а запрошенной identity.</summary>
    internal Func<MuMuInstallation, MuMuInstanceId, ApplicationResult<AndroidEndpoint>>? EndpointHandler { get; set; }

    /// <summary>Ответ наблюдения состояния ADB transport.</summary>
    internal Func<AndroidEndpoint, ApplicationResult<AndroidTransportObservation>>? TransportHandler { get; set; }

    /// <summary>Ответ однократного подключения к transport.</summary>
    internal Func<AndroidEndpoint, CancellationToken, ApplicationResult<AndroidCommandOutcome>>? ConnectHandler { get; set; }

    /// <summary>Ответ наблюдения готовности Android.</summary>
    internal Func<AndroidEndpoint, ApplicationResult<AndroidBootObservation>>? BootHandler { get; set; }

    /// <summary>Ответ наблюдения присутствия пакета.</summary>
    internal Func<AndroidEndpoint, AndroidPackageId, ApplicationResult<AndroidPackagePresence>>? PackageHandler { get; set; }

    /// <summary>Ответ разрешения launcher-компонента пакета.</summary>
    internal Func<AndroidEndpoint, AndroidPackageId, ApplicationResult<AndroidLauncherResolution>>? LauncherHandler { get; set; }

    /// <summary>Ответ наблюдения процессов пакета.</summary>
    internal Func<AndroidEndpoint, AndroidPackageId, ApplicationResult<AndroidProcessObservation>>? ProcessesHandler { get; set; }

    /// <summary>Ответ наблюдения переднего плана.</summary>
    internal Func<AndroidEndpoint, ApplicationResult<AndroidForegroundObservation>>? ForegroundHandler { get; set; }

    /// <summary>Ответ однократной mutation игры.</summary>
    internal Func<AndroidMutationRequest, CancellationToken, ApplicationResult<AndroidCommandOutcome>>? MutationHandler { get; set; }

    /// <summary>Установки, у которых запрашивалось обнаружение bundled ADB.</summary>
    internal IReadOnlyList<MuMuInstallation> AdbDiscoveryRequests => Snapshot(_adbDiscoveryRequests);

    /// <summary>Identity экземпляров, у которых запрашивался точный endpoint.</summary>
    internal IReadOnlyList<MuMuInstanceId> EndpointRequests => Snapshot(_endpointRequests);

    /// <summary>Endpoint-ы, у которых запрашивалось подключение transport.</summary>
    internal IReadOnlyList<AndroidEndpoint> ConnectRequests => Snapshot(_connectRequests);

    /// <summary>Endpoint-ы, у которых запрашивалось состояние transport.</summary>
    internal IReadOnlyList<AndroidEndpoint> TransportRequests => Snapshot(_transportRequests);

    /// <summary>Endpoint-ы, у которых запрашивалась готовность Android.</summary>
    internal IReadOnlyList<AndroidEndpoint> BootRequests => Snapshot(_bootRequests);

    /// <summary>Запросы присутствия пакета в порядке вызова.</summary>
    internal IReadOnlyList<AndroidPackageRequest> PackageRequests => Snapshot(_packageRequests);

    /// <summary>Запросы разрешения launcher-компонента в порядке вызова.</summary>
    internal IReadOnlyList<AndroidPackageRequest> LauncherRequests => Snapshot(_launcherRequests);

    /// <summary>Запросы процессов пакета в порядке вызова.</summary>
    internal IReadOnlyList<AndroidPackageRequest> ProcessRequests => Snapshot(_processRequests);

    /// <summary>Endpoint-ы, у которых запрашивался передний план.</summary>
    internal IReadOnlyList<AndroidEndpoint> ForegroundRequests => Snapshot(_foregroundRequests);

    /// <summary>Запрошенные mutation в порядке вызова.</summary>
    internal IReadOnlyList<AndroidMutationRequest> MutationRequests => Snapshot(_mutationRequests);

    /// <inheritdoc />
    public ApplicationResult<AndroidAdbExecutable> DiscoverAdbExecutable(MuMuInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        Record(_adbDiscoveryRequests, installation);

        return AdbExecutableHandler is null
            ? throw MissingHandler(nameof(DiscoverAdbExecutable))
            : AdbExecutableHandler(installation);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidEndpoint> ResolveEndpoint(
        MuMuInstallation installation,
        MuMuInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(installation);
        Record(_endpointRequests, instance);

        return EndpointHandler is null
            ? throw MissingHandler(nameof(ResolveEndpoint))
            : EndpointHandler(installation, instance);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidTransportObservation> QueryTransport(AndroidEndpoint endpoint)
    {
        Record(_transportRequests, endpoint);

        return TransportHandler is null
            ? throw MissingHandler(nameof(QueryTransport))
            : TransportHandler(endpoint);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidCommandOutcome> ConnectTransport(
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        Record(_connectRequests, endpoint);

        return ConnectHandler is null
            ? throw MissingHandler(nameof(ConnectTransport))
            : ConnectHandler(endpoint, cancellationToken);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidBootObservation> QueryBoot(AndroidEndpoint endpoint)
    {
        Record(_bootRequests, endpoint);

        return BootHandler is null
            ? throw MissingHandler(nameof(QueryBoot))
            : BootHandler(endpoint);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidPackagePresence> QueryPackage(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        Record(_packageRequests, new AndroidPackageRequest(endpoint, package));

        return PackageHandler is null
            ? throw MissingHandler(nameof(QueryPackage))
            : PackageHandler(endpoint, package);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidLauncherResolution> ResolveLauncher(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        Record(_launcherRequests, new AndroidPackageRequest(endpoint, package));

        return LauncherHandler is null
            ? throw MissingHandler(nameof(ResolveLauncher))
            : LauncherHandler(endpoint, package);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidProcessObservation> ObserveProcesses(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        Record(_processRequests, new AndroidPackageRequest(endpoint, package));

        return ProcessesHandler is null
            ? throw MissingHandler(nameof(ObserveProcesses))
            : ProcessesHandler(endpoint, package);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidForegroundObservation> ObserveForeground(AndroidEndpoint endpoint)
    {
        Record(_foregroundRequests, endpoint);

        return ForegroundHandler is null
            ? throw MissingHandler(nameof(ObserveForeground))
            : ForegroundHandler(endpoint);
    }

    /// <inheritdoc />
    public ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AndroidGameMutation mutation,
        CancellationToken cancellationToken)
    {
        AndroidMutationRequest request = new(endpoint, package, mutation);
        Record(_mutationRequests, request);

        return MutationHandler is null
            ? throw MissingHandler(nameof(RequestGameMutation))
            : MutationHandler(request, cancellationToken);
    }

    private static InvalidOperationException MissingHandler(string method)
        => new($"Подмена границы Android не получила обработчика {method}.");

    private void Record<T>(List<T> requests, T request)
    {
        lock (_sync)
        {
            requests.Add(request);
        }
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> requests)
    {
        lock (_sync)
        {
            return [.. requests];
        }
    }
}
