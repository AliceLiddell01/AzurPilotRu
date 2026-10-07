using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства полного lifecycle через настоящий Windows-адаптер Android и настоящий orchestration
/// Core: подменяется только граница запуска процесса.
/// </summary>
/// <remarks>
/// <para>
/// Здесь не подменяется host: команды строит <see cref="AdbCommandBuilder"/>, запускает
/// <see cref="AdbClient"/> через общую границу процесса, разбирает <see cref="AdbResponseParser"/>, а
/// решения принимают production-сервисы Core. Поэтому проверяется именно совместная работа слоёв:
/// target-explicit адресация, package-scoped mutation и доказанный postcondition.
/// </para>
/// <para>
/// Ни установленная MuMu, ни ADB, ни установленная игра не требуются: исходы команд задаёт
/// переиспользуемая подмена <see cref="FakeWindowsProcessRunner"/>.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidLifecycleOverAdapterTests
{
    private static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);

    private const string VersionOutput = "Android Debug Bridge version 1.0.41\nVersion 36.0.0-13206524\n";

    private const string LauncherComponent =
        AzurLaneProduct.Package + "/com.manjuu.azurlane.PrePermissionActivity";

    private const string PackageInstalled =
        "package:/data/app/~~abc==/" + AzurLaneProduct.Package + "/base.apk\n";

    private const string RunningProcesses = "PID NAME\n4242 " + AzurLaneProduct.Package + "\n";

    private const string NoProcesses = "PID NAME\n";

    private const string GameForegroundDump =
        "  mCurrentFocus=Window{1 u0 " + AzurLaneProduct.Package + "/com.manjuu.azurlane.MainActivity}\n";

    private const string OtherForegroundDump =
        "  mCurrentFocus=Window{1 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}\n";

    [Fact(DisplayName = "Полный lifecycle проходит через настоящий адаптер без глобальных операций ADB")]
    public async Task FullLifecycleRunsThroughAdapter()
    {
        AdapterFixture fixture = new();
        fixture.Discover();

        // Игра установлена, её процессов нет, на переднем плане другой пакет.
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, NoProcesses);
        fixture.Runner.EnqueueOutcome(0, OtherForegroundDump);
        // Launcher разрешается дважды: orchestration адресует mutation, и host адресует её же.
        fixture.Runner.EnqueueOutcome(0, LauncherComponent + "\n");
        fixture.Runner.EnqueueOutcome(0, LauncherComponent + "\n");
        fixture.Runner.EnqueueOutcome(0, "Starting: Intent { cmp=" + LauncherComponent + " }\n");
        // Postcondition запуска: процесс пакета наблюдается, игра на переднем плане.
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, RunningProcesses);
        fixture.Runner.EnqueueOutcome(0, GameForegroundDump);

        ApplicationResult<AzurLaneGameLifecycleOutcome> started = await fixture.Lifecycle.StartAsync(
            fixture.Installation,
            Endpoint,
            CancellationToken.None);

        Assert.True(started.IsSuccess);
        Assert.Equal(AzurLaneGameState.Stopped, started.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, started.Value!.FinalState);
        Assert.Equal(LauncherComponent, started.Value!.Launcher!.Flattened);

        // Остановка игры, запущенной предыдущей операцией.
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, RunningProcesses);
        fixture.Runner.EnqueueOutcome(0, GameForegroundDump);
        fixture.Runner.EnqueueOutcome(0, string.Empty);
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, NoProcesses);
        fixture.Runner.EnqueueOutcome(0, OtherForegroundDump);

        ApplicationResult<AzurLaneGameLifecycleOutcome> stopped = await fixture.Lifecycle.StopAsync(
            fixture.Installation,
            Endpoint,
            CancellationToken.None);

        Assert.True(stopped.IsSuccess);
        Assert.Equal(AzurLaneGameState.Foreground, stopped.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Stopped, stopped.Value!.FinalState);

        // 1 обнаружение executable + 9 команд запуска + 7 команд остановки.
        Assert.Equal(17, fixture.Runner.Requests.Count);

        // Все команды над устройством несут точный target, и ни одна не запрашивает глобальную операцию.
        foreach (RecordedProcessRequest recorded in fixture.Runner.Requests.Skip(1))
        {
            Assert.Equal(AdbCommandBuilder.TargetArgument, recorded.Request.Arguments[0]);
            Assert.Equal(Endpoint.ToString(), recorded.Request.Arguments[1]);
        }

        AndroidTestOperations.AssertNoneRequested(
            fixture.Runner.Requests.Select(recorded => recorded.Request.Arguments));

        // Mutation адресует только выбранный target, только пакет игры и только разрешённый компонент.
        IReadOnlyList<string> start = fixture.Runner.Requests[6].Request.Arguments;

        Assert.Equal(AdbCommandBuilder.StartOperation, start[4]);
        Assert.Equal(AdbCommandBuilder.ComponentArgument, start[5]);
        Assert.Equal(LauncherComponent, start[6]);

        IReadOnlyList<string> forceStop = fixture.Runner.Requests[13].Request.Arguments;

        Assert.Equal(AdbCommandBuilder.ForceStopOperation, forceStop[4]);
        Assert.Equal(AzurLaneProduct.Package, forceStop[5]);

        // Весь lifecycle адресован ровно bundled ADB обнаруженной установки: подстановки другого
        // executable нет ни на одном шаге.
        string adbPath = AndroidInstallationLayout.GetAdbExecutablePath(fixture.InstallRoot);

        Assert.All(
            fixture.Runner.Requests,
            recorded => Assert.Equal(adbPath, recorded.Request.ExecutablePath));
    }

    [Fact(DisplayName = "Идемпотентный запуск через адаптер не выполняет mutation")]
    public async Task IdempotentStartThroughAdapterDoesNotMutate()
    {
        AdapterFixture fixture = new();
        fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, RunningProcesses);
        fixture.Runner.EnqueueOutcome(0, GameForegroundDump);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await fixture.Lifecycle.StartAsync(
            fixture.Installation,
            Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Foreground, result.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, result.Value!.FinalState);

        // 1 обнаружение executable + 3 read-only наблюдения: mutation и разрешения launcher-а нет.
        Assert.Equal(4, fixture.Runner.Requests.Count);
        Assert.DoesNotContain(
            fixture.Runner.Requests,
            recorded => recorded.Request.Arguments.Contains(AdbCommandBuilder.StartOperation));
        AndroidTestOperations.AssertNoneRequested(
            fixture.Runner.Requests.Select(recorded => recorded.Request.Arguments));
    }

    [Fact(DisplayName = "Recovery после неудачной mutation не завершает сервер ADB и не переподключается")]
    public async Task RecoveryAfterFailedMutationDoesNotManageAdbServer()
    {
        AdapterFixture fixture = new();
        fixture.Discover();

        // Игра в фоне: процессы наблюдаются, передний план занят другим пакетом.
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, RunningProcesses);
        fixture.Runner.EnqueueOutcome(0, OtherForegroundDump);
        // Force-stop завершается ненулевым кодом выхода и не меняет состояние устройства.
        fixture.Runner.EnqueueOutcome(1, string.Empty, "error: force-stop failed");
        fixture.Runner.EnqueueOutcome(0, PackageInstalled);
        fixture.Runner.EnqueueOutcome(0, RunningProcesses);
        fixture.Runner.EnqueueOutcome(0, OtherForegroundDump);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await fixture.Lifecycle.StopAsync(
            fixture.Installation,
            Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
            result.FailureInfo!.Code);
        Assert.Equal("1", result.FailureInfo!.Details![AndroidDetailKeys.ExitCode]);

        Assert.DoesNotContain(
            fixture.Runner.Requests,
            recorded => recorded.Request.Arguments.Contains("kill-server"));
        AndroidTestOperations.AssertNoneRequested(
            fixture.Runner.Requests.Select(recorded => recorded.Request.Arguments));
    }

    /// <summary>Настоящий адаптер на подменяемой границе процесса и production-orchestration Core.</summary>
    private sealed class AdapterFixture
    {
        internal AdapterFixture()
        {
            InstallRoot = AndroidTestPaths.Create("adapter-install");
            Installation = new MuMuInstallation(
                "test-version",
                InstallRoot,
                MuMuInstallationLayout.GetControlExecutablePath(InstallRoot));
            Runner = new FakeWindowsProcessRunner();
            Host = new AndroidWindowsHost(Runner);
            TimeProvider = new MuMuTestTimeProvider();
            GameState = new AzurLaneGameStateService(
                Host,
                TimeProvider,
                NullLogger<AzurLaneGameStateService>.Instance);
            Lifecycle = new AzurLaneGameLifecycleService(
                Host,
                GameState,
                new AndroidGameMutationGate(),
                TimeProvider,
                AndroidLifecycleTimings.Default,
                NullLogger<AzurLaneGameLifecycleService>.Instance);
        }

        /// <summary>Корень фиктивной установки, собранный в runtime.</summary>
        internal string InstallRoot { get; }

        /// <summary>Обнаруженная установка MuMuPlayer.</summary>
        internal MuMuInstallation Installation { get; }

        /// <summary>Переиспользуемая подмена общей границы запуска процесса.</summary>
        internal FakeWindowsProcessRunner Runner { get; }

        /// <summary>Настоящий production-адаптер Android.</summary>
        internal AndroidWindowsHost Host { get; }

        /// <summary>Управляемое время.</summary>
        internal MuMuTestTimeProvider TimeProvider { get; }

        /// <summary>Наблюдение состояния игры.</summary>
        internal AzurLaneGameStateService GameState { get; }

        /// <summary>Проверяемый lifecycle игры.</summary>
        internal AzurLaneGameLifecycleService Lifecycle { get; }

        /// <summary>Обнаруживает bundled ADB обнаруженной установки.</summary>
        internal void Discover()
        {
            Runner.EnqueueOutcome(0, VersionOutput);

            ApplicationResult<AndroidAdbExecutable> result = Host.DiscoverAdbExecutable(Installation);

            Assert.True(result.IsSuccess);
        }
    }
}
