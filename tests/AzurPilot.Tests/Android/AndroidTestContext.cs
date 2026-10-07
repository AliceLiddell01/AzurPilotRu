using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.MuMu;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Собранный production-orchestration Android на подменяемой host-границе и управляемом времени.
/// </summary>
/// <remarks>
/// <para>
/// Все зависимости передаются явно: production-orchestration не имеет скрытых значений по умолчанию,
/// поэтому проверка никогда не идёт по реальным часам, не делит координацию с другими проверками и не
/// требует ни установленной MuMu, ни ADB, ни установленной игры.
/// </para>
/// <para>
/// Время принадлежит переиспользуемому управляемому <see cref="MuMuTestTimeProvider"/>: часы
/// продвигаются ровно на запрошенную задержку, поэтому bounded polling действительно доходит до
/// deadline, а fixed sleep доказательством не используется. Числа времени берутся у их владельца
/// <see cref="AndroidLifecycleTimings"/>, а не задаются литералами в проверках.
/// </para>
/// </remarks>
internal sealed class AndroidTestContext
{
    /// <summary>Точный endpoint, которым адресуются проверки.</summary>
    internal static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);

    /// <summary>Второй точный endpoint: проверяет, что адресация не «плывёт» на другой target.</summary>
    internal static readonly AndroidEndpoint OtherEndpoint = new("127.0.0.1", 16417);

    /// <summary>Identity выбранного Android-экземпляра.</summary>
    internal static readonly MuMuInstanceId Instance = MuMuInstanceId.FromIndex("1");

    /// <summary>Идентификатор пакета игры Azur Lane Global/EN.</summary>
    internal static readonly AndroidPackageId Package = new(AzurLaneProduct.Package);

    /// <summary>Корень фиктивной установки: собирается в runtime, machine-specific констант нет.</summary>
    internal static readonly string InstallRoot = AndroidTestPaths.Create("install");

    internal AndroidTestContext()
    {
        Host = new TestAndroidHost();
        TimeProvider = new MuMuTestTimeProvider();
        Gate = new AndroidGameMutationGate();
        Readiness = new AndroidReadinessService(
            Host,
            Timings,
            TimeProvider,
            NullLogger<AndroidReadinessService>.Instance);
        GameState = new AzurLaneGameStateService(
            Host,
            TimeProvider,
            NullLogger<AzurLaneGameStateService>.Instance);
        Lifecycle = new AzurLaneGameLifecycleService(
            Host,
            GameState,
            Gate,
            TimeProvider,
            Timings,
            NullLogger<AzurLaneGameLifecycleService>.Instance);
    }

    /// <summary>Host-double: единственная подменяемая граница Android.</summary>
    internal TestAndroidHost Host { get; }

    /// <summary>Управляемое время.</summary>
    internal MuMuTestTimeProvider TimeProvider { get; }

    /// <summary>Process-local координация mutation, изолированная для этой проверки.</summary>
    internal AndroidGameMutationGate Gate { get; }

    /// <summary>Числа времени, взятые у их владельца.</summary>
    internal AndroidLifecycleTimings Timings { get; } = AndroidLifecycleTimings.Default;

    /// <summary>Проверяемая готовность Android.</summary>
    internal AndroidReadinessService Readiness { get; }

    /// <summary>Проверяемый вывод состояния игры.</summary>
    internal AzurLaneGameStateService GameState { get; }

    /// <summary>Проверяемый lifecycle игры.</summary>
    internal AzurLaneGameLifecycleService Lifecycle { get; }

    /// <summary>Установка-фикстура без machine-specific путей.</summary>
    internal static MuMuInstallation Installation { get; } =
        new("test-version", InstallRoot, MuMuInstallationLayout.GetControlExecutablePath(InstallRoot));

    /// <summary>Собирает наблюдение готового к командам transport.</summary>
    /// <returns>Наблюдение состояния <c>device</c>.</returns>
    internal static AndroidTransportObservation Device()
        => new(AndroidTransportState.Device, "test-transport-device");

    /// <summary>Собирает наблюдение transport, не готового к командам.</summary>
    /// <param name="state">Наблюдённое состояние transport.</param>
    /// <returns>Наблюдение transport.</returns>
    internal static AndroidTransportObservation Transport(AndroidTransportState state)
        => new(state, "test-transport-" + state);

    /// <summary>Собирает наблюдение готовности Android.</summary>
    /// <param name="shellAvailable">Признак доступного shell устройства.</param>
    /// <param name="bootCompleted">Значение <c>sys.boot_completed</c> или <see langword="null"/>.</param>
    /// <param name="release">Версия Android или <see langword="null"/>.</param>
    /// <param name="sdkLevel">Уровень SDK или <see langword="null"/>.</param>
    /// <returns>Наблюдение готовности Android.</returns>
    internal static AndroidBootObservation Boot(
        bool shellAvailable,
        int? bootCompleted,
        string? release = "15.0",
        int? sdkLevel = 35)
        => new(shellAvailable, bootCompleted, release, sdkLevel, "test-boot-observation");

    /// <summary>Собирает наблюдение процессов, доказавшее их наличие.</summary>
    /// <param name="processIds">Идентификаторы наблюдённых процессов пакета.</param>
    /// <returns>Наблюдение процессов пакета.</returns>
    internal static AndroidProcessObservation RunningProcesses(params int[] processIds)
        => new(processIds.Length, processIds, "test-processes-running");

    /// <summary>Собирает наблюдение процессов, доказавшее их отсутствие: пустой список, а не <see langword="null"/>.</summary>
    /// <returns>Наблюдение доказанного отсутствия процессов.</returns>
    internal static AndroidProcessObservation AbsentProcesses()
        => new(0, [], "test-processes-absent");

    /// <summary>Собирает недоказанное наблюдение процессов: список равен <see langword="null"/>, а не пуст.</summary>
    /// <returns>Наблюдение, не доказавшее ни наличия, ни отсутствия процессов.</returns>
    internal static AndroidProcessObservation UnprovenProcesses()
        => new(0, null, "test-processes-unproven");

    /// <summary>Собирает наблюдение переднего плана пакета игры.</summary>
    /// <param name="activity">Наблюдённая activity внутри пакета игры.</param>
    /// <returns>Наблюдение переднего плана, подтвердившее пакет игры.</returns>
    internal static AndroidForegroundObservation GameForeground(string activity = "com.manjuu.azurlane.MainActivity")
        => new(
            AndroidForegroundStatus.Foreground,
            Component(AzurLaneProduct.Package, activity),
            "test-foreground-game");

    /// <summary>Собирает наблюдение переднего плана другого пакета.</summary>
    /// <param name="package">Идентификатор наблюдённого пакета переднего плана.</param>
    /// <returns>Наблюдение переднего плана, не подтвердившее пакет игры.</returns>
    internal static AndroidForegroundObservation OtherForeground(string package = "app.lawnchair")
        => new(
            AndroidForegroundStatus.Other,
            Component(package, package + ".LawnchairLauncher"),
            "test-foreground-other");

    /// <summary>Собирает недоказанное наблюдение переднего плана.</summary>
    /// <returns>Наблюдение переднего плана без доказанного компонента.</returns>
    internal static AndroidForegroundObservation UnknownForeground()
        => new(AndroidForegroundStatus.Unknown, null, "test-foreground-unknown");

    /// <summary>Собирает разрешение launcher-компонента ровно одним компонентом.</summary>
    /// <param name="activity">Activity launcher-а внутри пакета игры.</param>
    /// <returns>Разрешение со статусом <see cref="AndroidLauncherResolutionStatus.Resolved"/>.</returns>
    internal static AndroidLauncherResolution ResolvedLauncher(
        string activity = "com.manjuu.azurlane.PrePermissionActivity")
        => new(
            AndroidLauncherResolutionStatus.Resolved,
            Component(AzurLaneProduct.Package, activity),
            1,
            "test-launcher-resolved");

    /// <summary>Собирает разрешение, не нашедшее launcher-компонента.</summary>
    /// <returns>Разрешение со статусом <see cref="AndroidLauncherResolutionStatus.Missing"/>.</returns>
    internal static AndroidLauncherResolution MissingLauncher()
        => new(AndroidLauncherResolutionStatus.Missing, null, 0, "test-launcher-missing");

    /// <summary>Собирает недоказанное разрешение launcher-компонента.</summary>
    /// <returns>Разрешение со статусом <see cref="AndroidLauncherResolutionStatus.QueryFailed"/>.</returns>
    internal static AndroidLauncherResolution UnprovenLauncher()
        => new(AndroidLauncherResolutionStatus.QueryFailed, null, 0, "test-launcher-unproven");

    /// <summary>Собирает неоднозначное разрешение launcher-компонента.</summary>
    /// <param name="matchingComponentCount">Число совпавших компонентов.</param>
    /// <returns>Разрешение со статусом <see cref="AndroidLauncherResolutionStatus.Ambiguous"/>.</returns>
    internal static AndroidLauncherResolution AmbiguousLauncher(int matchingComponentCount = 2)
        => new(
            AndroidLauncherResolutionStatus.Ambiguous,
            null,
            matchingComponentCount,
            "test-launcher-ambiguous");

    /// <summary>Собирает компонент пакета в канонической форме <c>package/activity</c>.</summary>
    /// <param name="package">Идентификатор пакета.</param>
    /// <param name="activity">Полное имя activity внутри пакета.</param>
    /// <returns>Компонент пакета.</returns>
    internal static AndroidComponent Component(string package, string activity)
        => new(new AndroidPackageId(package), activity, package + "/" + activity);

    /// <summary>Собирает успешный результат однократного вызова ADB.</summary>
    /// <param name="exitCode">Код выхода процесса ADB.</param>
    /// <returns>Успешный результат вызова.</returns>
    internal static ApplicationResult<AndroidCommandOutcome> Command(int exitCode = 0)
        => ApplicationResult<AndroidCommandOutcome>.Success(
            new AndroidCommandOutcome(exitCode, "test-adb-output"));
}
