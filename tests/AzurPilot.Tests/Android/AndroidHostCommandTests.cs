using System.ComponentModel;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства Windows-адаптера Android поверх переиспользуемой подмены общей границы запуска
/// процесса: bundled ADB обнаруженной установки, target-explicit команды и fail-closed наблюдения.
/// </summary>
/// <remarks>
/// <para>
/// Второй fake runner не заводится: проверки используют <see cref="FakeWindowsProcessRunner"/> — ту же
/// подмену общей границы процесса, что и проверки MuMu. Реально исполняются production-код адаптера:
/// раскладка bundled ADB, построение аргументов, запуск команды, разбор ответа и проекция отказов.
/// </para>
/// <para>
/// Ни установленная MuMu, ни ADB, ни установленная игра не требуются: граница процесса отвечает
/// сценарием, а пути собираются в runtime из временного каталога.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidHostCommandTests
{
    private static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);

    private static readonly AndroidPackageId Package = new(AzurLaneProduct.Package);

    private const string VersionOutput = "Android Debug Bridge version 1.0.41\nVersion 36.0.0-13206524\n";

    private const string LauncherActivity = "com.manjuu.azurlane.PrePermissionActivity";

    private const string SecondLauncherActivity = "com.manjuu.azurlane.OtherActivity";

    // --- Обнаружение bundled ADB ---

    [Fact(DisplayName = "Обнаружение адресует bundled ADB обнаруженной установки")]
    public void DiscoveryAddressesBundledAdb()
    {
        HostFixture fixture = new();

        AndroidAdbExecutable executable = fixture.Discover();

        string expected = AndroidInstallationLayout.GetAdbExecutablePath(fixture.InstallRoot);

        Assert.Equal(expected, executable.Path);
        Assert.Equal(AndroidInstallationLayout.AdbExecutableFileName, Path.GetFileName(executable.Path));
        Assert.Equal(
            MuMuInstallationLayout.ControlExecutableDirectoryName,
            Path.GetFileName(Path.GetDirectoryName(executable.Path)));
        Assert.StartsWith(fixture.InstallRoot, executable.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.41", executable.VersionEvidence, StringComparison.Ordinal);

        // Путь конкретной машины в диагностику не переносится: достаточно bounded версии.
        Assert.DoesNotContain(fixture.InstallRoot, executable.VersionEvidence, StringComparison.Ordinal);

        RecordedProcessRequest request = Assert.Single(fixture.Runner.Requests);

        Assert.Equal(expected, request.Request.ExecutablePath);
        Assert.Equal(AdbCommandBuilder.BuildVersionArguments().ToArray(), request.Request.Arguments.ToArray());
    }

    [Fact(DisplayName = "Отсутствие пригодного bundled ADB не заменяется другим executable")]
    public void DiscoveryDoesNotFallBackToAnotherExecutable()
    {
        HostFixture fixture = new();
        fixture.Runner.EnqueueOutcome(1, "some other tool output");

        ApplicationResult<AndroidAdbExecutable> result = fixture.Host.DiscoverAdbExecutable(fixture.Installation);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidAdbUnavailable, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.ResponseUnrecognized,
            result.FailureInfo!.Details![AndroidDetailKeys.Reason]);
        AssertNoMachinePath(result.FailureInfo!, fixture.InstallRoot);

        // Цепочки поиска нет: опрошен ровно один executable, и он принадлежит обнаруженной установке.
        RecordedProcessRequest request = Assert.Single(fixture.Runner.Requests);

        Assert.Equal(
            AndroidInstallationLayout.GetAdbExecutablePath(fixture.InstallRoot),
            request.Request.ExecutablePath);
    }

    [Fact(DisplayName = "Неудачный запуск bundled ADB проецируется в Android-код без пути установки")]
    public void StartFailureIsProjectedWithoutInstallPath()
    {
        HostFixture fixture = new();
        string executablePath = AndroidInstallationLayout.GetAdbExecutablePath(fixture.InstallRoot);
        fixture.Runner.EnqueueFailure(
            new AdbProcessFailureProjection().StartFailed(executablePath, new Win32Exception(2)));

        ApplicationResult<AndroidAdbExecutable> result = fixture.Host.DiscoverAdbExecutable(fixture.Installation);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AndroidAdbUnavailable, failure.Code);
        Assert.Equal(AndroidDetailValues.ProcessStartFailed, failure.Details![AndroidDetailKeys.Reason]);
        Assert.Equal(AndroidInstallationLayout.AdbExecutableFileName, failure.Details[AndroidDetailKeys.ExecutableName]);
        AssertNoMachinePath(failure, fixture.InstallRoot);
        _ = Assert.Single(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Команда ADB до обнаружения executable — ошибка программирования")]
    public void CommandBeforeDiscoveryIsProgrammingError()
    {
        HostFixture fixture = new();

        _ = Assert.Throws<InvalidOperationException>(() => fixture.Host.QueryTransport(Endpoint));
    }

    // --- Target-explicit адресация ---

    [Fact(DisplayName = "Каждая команда host-а несёт точный target, а не устройство по умолчанию")]
    public void EveryCommandCarriesExactTarget()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, "device\n");
        fixture.Runner.EnqueueOutcome(0, "1\n");
        fixture.Runner.EnqueueOutcome(0, "15.0\n");
        fixture.Runner.EnqueueOutcome(0, "35\n");

        Assert.True(fixture.Host.QueryTransport(Endpoint).IsSuccess);
        Assert.True(fixture.Host.QueryBoot(Endpoint).IsSuccess);

        Assert.Equal(5, fixture.Runner.Requests.Count);

        for (int index = 1; index < fixture.Runner.Requests.Count; index++)
        {
            IReadOnlyList<string> arguments = fixture.Runner.Requests[index].Request.Arguments;

            Assert.Equal(AdbCommandBuilder.TargetArgument, arguments[0]);
            Assert.Equal(Endpoint.ToString(), arguments[1]);
        }
    }

    [Fact(DisplayName = "Пустой идентификатор пакета — ошибка программирования, а не отказ устройства")]
    public void EmptyPackageIsProgrammingError()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();

        _ = Assert.Throws<ArgumentException>(() => fixture.Host.QueryPackage(Endpoint, default));

        // Команда не выполнялась: ошибка обнаружена до запуска процесса.
        _ = Assert.Single(fixture.Runner.Requests);
    }

    // --- Наблюдения ---

    [Fact(DisplayName = "Состояние transport host сообщает без догадок")]
    public void TransportStatesComeFromDeviceAnswer()
    {
        Assert.Equal(AndroidTransportState.Device, TransportOf(0, "device\n"));
        Assert.Equal(AndroidTransportState.Offline, TransportOf(0, "offline\n"));
        Assert.Equal(AndroidTransportState.Unknown, TransportOf(0, "unrecognized-form\n"));
        Assert.Equal(
            AndroidTransportState.Absent,
            TransportOf(1, string.Empty, $"error: device '{Endpoint}' not found"));
    }

    [Fact(DisplayName = "Готовность Android сообщается фактами shell, boot_completed, release и sdk")]
    public void BootObservationReportsDeviceFacts()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, "1\n");
        fixture.Runner.EnqueueOutcome(0, "15.0\n");
        fixture.Runner.EnqueueOutcome(0, "35\n");

        ApplicationResult<AndroidBootObservation> result = fixture.Host.QueryBoot(Endpoint);

        Assert.True(result.IsSuccess);

        AndroidBootObservation boot = result.Value!;

        Assert.True(boot.ShellAvailable);
        Assert.Equal(1, boot.BootCompleted);
        Assert.Equal("15.0", boot.AndroidRelease);
        Assert.Equal(35, boot.SdkLevel);
        Assert.Equal(4, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Недоступный shell не превращается в наблюдённую готовность")]
    public void UnavailableShellIsNotReadiness()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(1, string.Empty, "error: closed");

        ApplicationResult<AndroidBootObservation> result = fixture.Host.QueryBoot(Endpoint);

        Assert.True(result.IsSuccess);

        AndroidBootObservation boot = result.Value!;

        Assert.False(boot.ShellAvailable);
        Assert.Null(boot.BootCompleted);
        Assert.Null(boot.AndroidRelease);
        Assert.Null(boot.SdkLevel);

        // При недоступном shell свойства устройства не запрашиваются вовсе.
        Assert.Equal(2, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Присутствие пакета host различает установку, отсутствие и недоказанный запрос")]
    public void PackagePresenceIsNotGuessed()
    {
        Assert.Equal(AndroidPackagePresence.Installed, PackageOf(0, "package:/data/app/base.apk\n"));
        Assert.Equal(AndroidPackagePresence.Absent, PackageOf(1, string.Empty));
        Assert.Equal(
            AndroidPackagePresence.QueryFailed,
            PackageOf(1, string.Empty, $"error: device '{Endpoint}' not found"));
    }

    [Fact(DisplayName = "Разрешение launcher-компонента host не догадывается")]
    public void LauncherResolutionIsFailClosed()
    {
        AndroidLauncherResolution resolved = LauncherOf(
            0,
            AzurLaneProduct.Package + "/" + LauncherActivity + "\n");

        Assert.Equal(AndroidLauncherResolutionStatus.Resolved, resolved.Status);
        Assert.Equal(Package, resolved.Component!.Package);
        Assert.Equal(1, resolved.MatchingComponentCount);

        Assert.Equal(AndroidLauncherResolutionStatus.Missing, LauncherOf(0, string.Empty).Status);
        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, LauncherOf(1, string.Empty).Status);
        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, LauncherOf(0, "not-a-component\n").Status);

        AndroidLauncherResolution ambiguous = LauncherOf(0, TwoLauncherComponents());

        Assert.Equal(AndroidLauncherResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.Component);
        Assert.Equal(2, ambiguous.MatchingComponentCount);
    }

    [Fact(DisplayName = "Наблюдение процессов host не подставляет пустой список вместо недоказанного")]
    public void ProcessObservationDoesNotSubstituteEmptyList()
    {
        AndroidProcessObservation running = ProcessesOf(0, "PID NAME\n4242 " + AzurLaneProduct.Package + "\n");

        Assert.Equal(1, running.ProcessCount);
        Assert.NotNull(running.ProcessIds);

        AndroidProcessObservation absent = ProcessesOf(0, "PID NAME\n5151 com.other.app\n");

        Assert.NotNull(absent.ProcessIds);
        Assert.Empty(absent.ProcessIds!);

        AndroidProcessObservation unproven = ProcessesOf(1, string.Empty);

        Assert.Null(unproven.ProcessIds);
        Assert.Equal(0, unproven.ProcessCount);
    }

    [Fact(DisplayName = "Передний план host сравнивается по exact package, а не по компоненту")]
    public void ForegroundComparesExactPackage()
    {
        AndroidForegroundObservation samePackageOtherActivity = ForegroundOf(
            0,
            "  mCurrentFocus=Window{1 u0 " + AzurLaneProduct.Package + "/com.manjuu.azurlane.MainActivity}\n");

        Assert.Equal(AndroidForegroundStatus.Foreground, samePackageOtherActivity.Status);
        Assert.Equal(AzurLaneProduct.Package, samePackageOtherActivity.Component!.Package.ToString());
        Assert.NotEqual(
            AzurLaneProduct.Package + "/" + LauncherActivity,
            samePackageOtherActivity.Component.Flattened);

        AndroidForegroundObservation otherPackage = ForegroundOf(
            0,
            "  mCurrentFocus=Window{1 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}\n");

        Assert.Equal(AndroidForegroundStatus.Other, otherPackage.Status);
        Assert.Equal("app.lawnchair", otherPackage.Component!.Package.ToString());

        AndroidForegroundObservation unknown = ForegroundOf(0, "  mCurrentFocus=null\n");

        Assert.Equal(AndroidForegroundStatus.Unknown, unknown.Status);
        Assert.Null(unknown.Component);
    }

    [Fact(DisplayName = "Усечённый вывод ADB не считается полноценным ответом")]
    public void TruncatedOutputIsNotAProvenAnswer()
    {
        HostFixture transportFixture = new();
        _ = transportFixture.Discover();
        transportFixture.Runner.EnqueueOutcome(0, "device\n", standardOutputTruncated: true);

        Assert.Equal(
            AndroidTransportState.Unknown,
            transportFixture.Host.QueryTransport(Endpoint).Value!.State);

        HostFixture packageFixture = new();
        _ = packageFixture.Discover();
        packageFixture.Runner.EnqueueOutcome(0, "package:/data/app/base.apk\n", standardOutputTruncated: true);

        Assert.Equal(
            AndroidPackagePresence.QueryFailed,
            packageFixture.Host.QueryPackage(Endpoint, Package).Value);
    }

    [Fact(DisplayName = "Полный вывод ADB не переносится в наблюдение и результат вызова")]
    public void FullAdbOutputIsNotCarriedOut()
    {
        HostFixture transportFixture = new();
        _ = transportFixture.Discover();
        transportFixture.Runner.EnqueueOutcome(0, new string('x', BoundedDiagnosticText.MaxLength * 8) + "\n");

        AndroidTransportObservation observation = transportFixture.Host.QueryTransport(Endpoint).Value!;

        Assert.InRange(observation.Evidence.Length, 1, BoundedDiagnosticText.MaxLength);
        Assert.DoesNotContain("\n", observation.Evidence, StringComparison.Ordinal);

        HostFixture connectFixture = new();
        _ = connectFixture.Discover();
        connectFixture.Runner.EnqueueOutcome(
            0,
            new string('y', BoundedDiagnosticText.MaxLength * 8),
            new string('z', BoundedDiagnosticText.MaxLength * 8));

        AndroidCommandOutcome outcome =
            connectFixture.Host.ConnectTransport(Endpoint, CancellationToken.None).Value!;

        Assert.InRange(outcome.BoundedOutput.Length, 1, BoundedDiagnosticText.MaxLength);
    }

    // --- Mutation игры ---

    [Fact(DisplayName = "Mutation запуска адресует только выбранный target и разрешённый компонент")]
    public void StartMutationAddressesOnlySelectedTarget()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, AzurLaneProduct.Package + "/" + LauncherActivity + "\n");
        fixture.Runner.EnqueueOutcome(0, "Starting: Intent { cmp=" + AzurLaneProduct.Package + " }\n");

        ApplicationResult<AndroidCommandOutcome> result = fixture.Host.RequestGameMutation(
            Endpoint,
            Package,
            AndroidGameMutation.Start,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.ExitCode);
        Assert.Equal(3, fixture.Runner.Requests.Count);
        Assert.Equal(
            AdbCommandBuilder.BuildLauncherQueryArguments(Endpoint, Package).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());

        IReadOnlyList<string> start = fixture.Runner.Requests[2].Request.Arguments;

        Assert.Equal(AdbCommandBuilder.TargetArgument, start[0]);
        Assert.Equal(Endpoint.ToString(), start[1]);
        Assert.Equal(AdbCommandBuilder.StartOperation, start[4]);
        Assert.Equal(AdbCommandBuilder.ComponentArgument, start[5]);
        Assert.Equal(AzurLaneProduct.Package + "/" + LauncherActivity, start[6]);

        AssertNoForbiddenOperations(fixture);
    }

    [Fact(DisplayName = "Принудительная остановка адресует точный пакет")]
    public void ForceStopAddressesExactPackage()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, string.Empty);

        ApplicationResult<AndroidCommandOutcome> result = fixture.Host.RequestGameMutation(
            Endpoint,
            Package,
            AndroidGameMutation.ForceStop,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Runner.Requests.Count);
        Assert.Equal(
            AdbCommandBuilder.BuildForceStopGameArguments(Endpoint, Package).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());

        AssertNoForbiddenOperations(fixture);
    }

    [Fact(DisplayName = "Запуск не адресуется недоказанному launcher-компоненту")]
    public void StartIsNotAddressedToUnprovenLauncher()
    {
        HostFixture missing = new();
        _ = missing.Discover();
        missing.Runner.EnqueueOutcome(0, string.Empty);

        ApplicationResult<AndroidCommandOutcome> missingResult = missing.Host.RequestGameMutation(
            Endpoint,
            Package,
            AndroidGameMutation.Start,
            CancellationToken.None);

        Assert.True(missingResult.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherUnresolved, missingResult.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.Missing,
            missingResult.FailureInfo!.Details![AndroidDetailKeys.State]);

        // Mutation не выполнялась вовсе: вторая команда — только запрос launcher-а.
        Assert.Equal(2, missing.Runner.Requests.Count);

        HostFixture ambiguous = new();
        _ = ambiguous.Discover();
        ambiguous.Runner.EnqueueOutcome(0, TwoLauncherComponents());

        ApplicationResult<AndroidCommandOutcome> ambiguousResult = ambiguous.Host.RequestGameMutation(
            Endpoint,
            Package,
            AndroidGameMutation.Start,
            CancellationToken.None);

        Assert.True(ambiguousResult.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherAmbiguous, ambiguousResult.FailureInfo!.Code);
        Assert.Equal(
            "2",
            ambiguousResult.FailureInfo!.Details![AndroidDetailKeys.MatchingComponents]);
        Assert.Equal(2, ambiguous.Runner.Requests.Count);
    }

    // --- Recovery-путь и проекция отказов ---

    [Fact(DisplayName = "Ни нормальный, ни recovery-путь не завершают сервер ADB и не переподключаются")]
    public void NormalAndRecoveryPathsDoNotManageAdbServer()
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        string executablePath = AndroidInstallationLayout.GetAdbExecutablePath(fixture.InstallRoot);
        fixture.Runner.EnqueueFailure(
            new AdbProcessFailureProjection().TimedOut(executablePath, TimeSpan.FromSeconds(30), 0, 0));
        fixture.Runner.EnqueueOutcome(0, "device\n");

        ApplicationResult<AndroidTransportObservation> failed = fixture.Host.QueryTransport(Endpoint);
        ApplicationResult<AndroidTransportObservation> recovered = fixture.Host.QueryTransport(Endpoint);

        Assert.True(failed.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, failed.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.CommandTimeout,
            failed.FailureInfo!.Details![AndroidDetailKeys.Reason]);
        AssertNoMachinePath(failed.FailureInfo!, fixture.InstallRoot);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(AndroidTransportState.Device, recovered.Value!.State);

        Assert.Equal(3, fixture.Runner.Requests.Count);
        AssertNoForbiddenOperations(fixture);
    }

    [Fact(DisplayName = "Проекция отказов ADB использует Android-коды и не переносит абсолютный путь")]
    public void FailureProjectionUsesAndroidCodesWithoutAbsolutePath()
    {
        string installRoot = AndroidTestPaths.Create("projection-install");
        string executablePath = AndroidInstallationLayout.GetAdbExecutablePath(installRoot);
        AdbProcessFailureProjection projection = new();

        ApplicationFailure start = projection.StartFailed(executablePath, new InvalidOperationException("test"));
        ApplicationFailure timeout = projection.TimedOut(executablePath, TimeSpan.FromSeconds(30), 12, 7);
        ApplicationFailure cancelled = projection.Cancelled();

        Assert.Equal(ApplicationFailure.AndroidAdbUnavailable, start.Code);
        Assert.Equal(AndroidDetailValues.ProcessStartFailed, start.Details![AndroidDetailKeys.Reason]);
        Assert.Equal(
            AndroidInstallationLayout.AdbExecutableFileName,
            start.Details[AndroidDetailKeys.ExecutableName]);

        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, timeout.Code);
        Assert.Equal(AndroidDetailValues.CommandTimeout, timeout.Details![AndroidDetailKeys.Reason]);
        Assert.Equal(
            AndroidInstallationLayout.AdbExecutableFileName,
            timeout.Details[AndroidDetailKeys.ExecutableName]);
        Assert.Equal("12", timeout.Details[AndroidDetailKeys.StandardOutputCharacters]);
        Assert.Equal("7", timeout.Details[AndroidDetailKeys.StandardErrorCharacters]);
        Assert.True(timeout.Details.ContainsKey(AndroidDetailKeys.ElapsedMilliseconds));

        Assert.Equal(ApplicationFailure.OperationCancelled, cancelled.Code);

        AssertNoMachinePath(start, installRoot);
        AssertNoMachinePath(timeout, installRoot);

        // MuMu-коды в Android-проекции не используются: это разные возможности.
        Assert.DoesNotContain("mumu", start.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mumu", timeout.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mumu", cancelled.Code, StringComparison.OrdinalIgnoreCase);
    }

    private static string TwoLauncherComponents()
        => AzurLaneProduct.Package + "/" + LauncherActivity + "\n"
            + AzurLaneProduct.Package + "/" + SecondLauncherActivity + "\n";

    private static AndroidTransportState TransportOf(int exitCode, string standardOutput, string standardError = "")
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(exitCode, standardOutput, standardError);

        ApplicationResult<AndroidTransportObservation> result = fixture.Host.QueryTransport(Endpoint);

        Assert.True(result.IsSuccess);
        return result.Value!.State;
    }

    private static AndroidPackagePresence PackageOf(int exitCode, string standardOutput, string standardError = "")
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(exitCode, standardOutput, standardError);

        ApplicationResult<AndroidPackagePresence> result = fixture.Host.QueryPackage(Endpoint, Package);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static AndroidLauncherResolution LauncherOf(int exitCode, string standardOutput)
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(exitCode, standardOutput);

        ApplicationResult<AndroidLauncherResolution> result = fixture.Host.ResolveLauncher(Endpoint, Package);

        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static AndroidProcessObservation ProcessesOf(int exitCode, string standardOutput)
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(exitCode, standardOutput);

        ApplicationResult<AndroidProcessObservation> result = fixture.Host.ObserveProcesses(Endpoint, Package);

        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static AndroidForegroundObservation ForegroundOf(int exitCode, string standardOutput)
    {
        HostFixture fixture = new();
        _ = fixture.Discover();
        fixture.Runner.EnqueueOutcome(exitCode, standardOutput);

        ApplicationResult<AndroidForegroundObservation> result = fixture.Host.ObserveForeground(Endpoint);

        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static void AssertNoForbiddenOperations(HostFixture fixture)
        => AndroidTestOperations.AssertNoneRequested(fixture.Runner.Requests.Select(recorded => recorded.Request.Arguments));

    private static void AssertNoMachinePath(ApplicationFailure failure, string installRoot)
    {
        Assert.NotNull(failure.Details);
        Assert.DoesNotContain(installRoot, failure.Message, StringComparison.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> detail in failure.Details)
        {
            Assert.DoesNotContain(installRoot, detail.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Установка-фикстура и host на подменяемой границе запуска процесса.</summary>
    private sealed class HostFixture
    {
        internal HostFixture()
        {
            InstallRoot = AndroidTestPaths.Create("host-install");
            Installation = new MuMuInstallation(
                "test-version",
                InstallRoot,
                MuMuInstallationLayout.GetControlExecutablePath(InstallRoot));
            Runner = new FakeWindowsProcessRunner();
            Host = new AndroidWindowsHost(Runner);
        }

        /// <summary>Абсолютный путь корня фиктивной установки, собранный в runtime.</summary>
        internal string InstallRoot { get; }

        /// <summary>Обнаруженная установка MuMuPlayer.</summary>
        internal MuMuInstallation Installation { get; }

        /// <summary>Переиспользуемая подмена общей границы запуска процесса.</summary>
        internal FakeWindowsProcessRunner Runner { get; }

        /// <summary>Проверяемый production-адаптер.</summary>
        internal AndroidWindowsHost Host { get; }

        /// <summary>Обнаруживает bundled ADB и требует, чтобы executable был подтверждён.</summary>
        /// <returns>Обнаруженный исполняемый файл bundled ADB.</returns>
        internal AndroidAdbExecutable Discover()
        {
            Runner.EnqueueOutcome(0, VersionOutput);

            ApplicationResult<AndroidAdbExecutable> result = Host.DiscoverAdbExecutable(Installation);

            Assert.True(result.IsSuccess);
            return result.Value!;
        }
    }
}
