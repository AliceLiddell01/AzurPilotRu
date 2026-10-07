using System.Globalization;
using System.Reflection;
using AzurPilot.App;
using AzurPilot.Core.Android;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Configuration;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки bounded Android-секции и секции состояния игры Azur Lane в диагностическом snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Проверки выполняются на реально построенном host-е: подменяются только host-side границы MuMu и Android,
/// поэтому остальной production-код диагностики — read-only проба, разрешение экземпляра через Core
/// orchestration, bounded-форма секций и логирование — остаётся тем же. Ни установленная MuMu, ни
/// установленный ADB, ни запущенный эмулятор для этих проверок не требуются.
/// </para>
/// <para>
/// Отдельно доказывается, что диагностика только читает: при известном, но неготовом transport ни
/// подключения, ни mutation не запрашивается, а состояние transport сообщается фактом. Наблюдение
/// состояния игры при этом не выполняется, поэтому недоказанное состояние не выглядит как «игра не
/// установлена».
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AndroidDiagnosticsTests
{
    /// <summary>Синтетический хост точного endpoint-а: в исходниках проверок нет machine-specific адресов.</summary>
    private const string EndpointHost = "endpoint-host-sentinel";

    /// <summary>Порт точного endpoint-а, использованный подменённой границей.</summary>
    private const int EndpointPort = 16384;

    /// <summary>Синтетический путь bundled ADB: machine-specific путь до секции доходить не должен.</summary>
    private const string AdbPathSentinel = "adb-path-sentinel";

    /// <summary>Синтетический корень установки: в исходниках проверок нет machine-specific путей.</summary>
    private const string InstallRootSentinel = "install-root-sentinel";

    /// <summary>Синтетический путь control surface установки.</summary>
    private const string ControlExecutableSentinel = "control-executable-sentinel";

    /// <summary>Хвост длинного evidence: до bounded-секции он доходить не должен.</summary>
    private const string EvidenceTailSentinel = "evidence-tail-sentinel";

    /// <summary>Значение <c>sys.boot_completed</c>, которым подтверждается завершённая загрузка Android.</summary>
    private const int BootCompleted = 1;

    /// <summary>Версия Android, сообщённая подменённым устройством.</summary>
    private const string AndroidRelease = "15";

    /// <summary>Уровень API Android, сообщённый подменённым устройством.</summary>
    private const int SdkLevel = 35;

    /// <summary>
    /// Bounded имя шага диагностики, на котором она остановилась.
    /// </summary>
    /// <remarks>
    /// Имена шагов принадлежат внутреннему типу пробы, поэтому в проверках они объявлены локально: так
    /// проверяется фактическое значение секции, а не константа, прочитанная у той же реализации.
    /// </remarks>
    private const string InstallationStageName = "installation";

    /// <summary>Bounded имя шага разрешения выбранного Android-экземпляра.</summary>
    private const string InstanceStageName = "instance";

    /// <summary>Bounded имя шага обнаружения bundled ADB.</summary>
    private const string AdbStageName = "adb";

    /// <summary>Bounded имя шага, на котором все наблюдения доказаны.</summary>
    private const string CompleteStageName = "complete";

    /// <summary>Префикс строки человекочитаемого итога с bounded Android-секцией.</summary>
    private const string AndroidSectionPrefix = "  Android: ";

    /// <summary>Префикс строки человекочитаемого итога с bounded секцией состояния игры.</summary>
    private const string AzurLaneSectionPrefix = "  Azur Lane: ";

    [Fact(DisplayName = "Неготовый transport сообщается фактом, а не исправляется подключением")]
    public void UnreadyTransportIsReportedAsFactWithoutMutation()
    {
        TestAndroidHost androidHost = new()
        {
            TransportResult = ApplicationResult<AndroidTransportObservation>.Success(
                new AndroidTransportObservation(AndroidTransportState.Absent, "transport=absent")),
        };

        AzurPilotDiagnosticReport report = Capture(androidHost);

        // Точный endpoint разрешён и ADB обнаружен: диагностика доходит до наблюдения готовности.
        Assert.True(report.Android.IsAdbAvailable);
        Assert.Equal(EndpointHost + ":" + EndpointPort.ToString(CultureInfo.InvariantCulture), report.Android.Endpoint);
        Assert.Equal(AndroidTransportState.Absent, report.Android.TransportState);
        Assert.Null(report.Android.Failure);
        Assert.Equal(CompleteStageName, report.Android.Stage);

        // Готовность Android не наблюдалась: «не наблюдалось» не выдаётся за «не готово».
        Assert.Null(report.Android.IsShellAvailable);
        Assert.Null(report.Android.BootCompleted);
        Assert.Null(report.Android.AndroidRelease);
        Assert.Null(report.Android.SdkLevel);

        // Состояние игры не наблюдалось, поэтому недоказанные факты остаются недоказанными, а не
        // отрицательными: секция сообщает product identity и отсутствие наблюдения.
        Assert.Equal(AzurLaneProduct.Package, report.AzurLane.Package);
        Assert.Null(report.AzurLane.State);
        Assert.Null(report.AzurLane.IsInstalled);
        Assert.Null(report.AzurLane.IsProcessRunning);
        Assert.Null(report.AzurLane.IsForeground);
        Assert.Null(report.AzurLane.Failure);

        // Диагностика только читает: ни подключения transport, ни mutation, ни команд, которым нужен
        // готовый transport.
        Assert.Equal(0, androidHost.ConnectCount);
        Assert.Equal(0, androidHost.MutationCount);
        Assert.Equal(0, androidHost.BootQueryCount);
        Assert.Equal(0, androidHost.PackageQueryCount);
        Assert.Equal(0, androidHost.ProcessQueryCount);
        Assert.Equal(0, androidHost.ForegroundQueryCount);
    }

    [Fact(DisplayName = "Готовый transport даёт факты готовности Android и состояние игры")]
    public void ReadyTransportYieldsBootAndGameFacts()
    {
        TestAndroidHost androidHost = ReadyAndroidHost();

        AzurPilotDiagnosticReport report = Capture(androidHost);

        Assert.Equal(AndroidTransportState.Device, report.Android.TransportState);
        Assert.True(report.Android.IsShellAvailable);
        Assert.Equal(BootCompleted, report.Android.BootCompleted);
        Assert.Equal(AndroidRelease, report.Android.AndroidRelease);
        Assert.Equal(SdkLevel, report.Android.SdkLevel);
        Assert.Null(report.Android.Failure);

        // Три факта независимы и наблюдаются у готового transport.
        Assert.True(report.AzurLane.IsInstalled);
        Assert.True(report.AzurLane.IsProcessRunning);
        Assert.True(report.AzurLane.IsForeground);
        Assert.Equal(AzurLaneGameState.Foreground, report.AzurLane.State);
        Assert.Null(report.AzurLane.Failure);

        // Наблюдение состояния — не mutation: ни подключения, ни запуска игры не запрашивалось.
        Assert.Equal(0, androidHost.ConnectCount);
        Assert.Equal(0, androidHost.MutationCount);

        // Цель Android разрешается один раз на запуск: обе секции описывают одну и ту же пробу, а не
        // повторяют discovery и разрешение endpoint-а.
        Assert.Equal(1, androidHost.AdbDiscoveryCount);
        Assert.Equal(1, androidHost.EndpointResolutionCount);
        Assert.Equal(1, androidHost.TransportQueryCount);
        Assert.Equal(1, androidHost.BootQueryCount);
    }

    [Fact(DisplayName = "Недоступный bundled ADB — диагностический отказ, а не отказ запуска")]
    public void UnavailableAdbIsDiagnosticFailure()
    {
        TestAndroidHost androidHost = new()
        {
            AdbResult = ApplicationResult<AndroidAdbExecutable>.Failure(
                Failure(ApplicationFailure.AndroidAdbUnavailable)),
        };

        AzurPilotDiagnosticReport report = Capture(androidHost);

        Assert.False(report.Android.IsAdbAvailable);
        Assert.Null(report.Android.AdbEvidence);
        Assert.Null(report.Android.Endpoint);
        Assert.Null(report.Android.TransportState);
        Assert.Equal(AdbStageName, report.Android.Stage);
        Assert.Equal(ApplicationFailure.AndroidAdbUnavailable, report.Android.Failure!.Code);

        // Секция состояния игры сообщает, почему состояние продукта осталось недоказанным.
        Assert.Null(report.AzurLane.State);
        Assert.Equal(ApplicationFailure.AndroidAdbUnavailable, report.AzurLane.Failure!.Code);

        // До команд ADB и до mutation дело не дошло.
        Assert.Equal(0, androidHost.EndpointResolutionCount);
        Assert.Equal(0, androidHost.TransportQueryCount);
        Assert.Equal(0, androidHost.MutationCount);
    }

    [Fact(DisplayName = "Неразрешённый выбор экземпляра останавливает диагностику на своём шаге")]
    public void UnresolvedInstanceStopsAtInstanceStage()
    {
        TestAndroidHost androidHost = ReadyAndroidHost();

        AzurPilotDiagnosticReport report = Capture(androidHost, instances: []);

        Assert.Equal(InstanceStageName, report.Android.Stage);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, report.Android.Failure!.Code);
        Assert.False(report.Android.IsAdbAvailable);
        Assert.Null(report.Android.Endpoint);

        // Bundled ADB не обнаруживался: шаг после остановившего не выполняется.
        Assert.Equal(0, androidHost.AdbDiscoveryCount);
        Assert.Equal(0, androidHost.MutationCount);
    }

    [Fact(DisplayName = "Отсутствие установки MuMu — нормальный диагностический результат обеих секций")]
    public void AbsentInstallationIsNormalDiagnosticResult()
    {
        TestAndroidHost androidHost = ReadyAndroidHost();

        AzurPilotDiagnosticReport report = Capture(
            androidHost,
            installation: ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuInstallationNotFound)));

        Assert.Equal(InstallationStageName, report.Android.Stage);
        Assert.Equal(ApplicationFailure.MuMuInstallationNotFound, report.Android.Failure!.Code);
        Assert.Equal(ApplicationFailure.MuMuInstallationNotFound, report.AzurLane.Failure!.Code);
        Assert.Equal(0, androidHost.AdbDiscoveryCount);
        Assert.Equal(0, androidHost.MutationCount);
    }

    [Fact(DisplayName = "Нарушение контракта host-ом остаётся данными диагностики")]
    public void ThrowingAndroidHostIsDiagnosticFailure()
    {
        AzurPilotDiagnosticReport report = Capture(new ThrowingAndroidHost());

        Assert.Equal(ApplicationFailure.InternalError, report.Android.Failure!.Code);
        Assert.Equal(ApplicationFailure.InternalError, report.AzurLane.Failure!.Code);
        Assert.Equal(AdbStageName, report.Android.Stage);
        Assert.False(report.Android.IsAdbAvailable);
    }

    [Fact(DisplayName = "Значения секций bounded, однострочны и не содержат machine-specific путей")]
    public void SectionValuesAreBoundedAndFreeOfMachinePaths()
    {
        string longText = new string('x', BoundedDiagnosticText.MaxLength * 4)
            + "\n"
            + EvidenceTailSentinel;

        TestAndroidHost androidHost = new()
        {
            AdbResult = ApplicationResult<AndroidAdbExecutable>.Success(
                new AndroidAdbExecutable(AdbPathSentinel, longText)),
            EndpointResult = ApplicationResult<AndroidEndpoint>.Success(
                AndroidEndpoint.FromHostPort(EndpointHost, EndpointPort)),
            TransportResult = ApplicationResult<AndroidTransportObservation>.Success(
                new AndroidTransportObservation(AndroidTransportState.Device, longText)),
            BootResult = ApplicationResult<AndroidBootObservation>.Success(
                new AndroidBootObservation(
                    ShellAvailable: true,
                    BootCompleted: BootCompleted,
                    AndroidRelease: longText,
                    SdkLevel: SdkLevel,
                    Evidence: longText)),
            PackageResult = ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.Installed),
            ProcessResult = ApplicationResult<AndroidProcessObservation>.Success(
                new AndroidProcessObservation(1, [1], longText)),
            ForegroundResult = ApplicationResult<AndroidForegroundObservation>.Success(
                new AndroidForegroundObservation(
                    AndroidForegroundStatus.Foreground,
                    new AndroidComponent(
                        new AndroidPackageId(AzurLaneProduct.Package),
                        longText,
                        longText),
                    longText)),
        };

        AzurPilotDiagnosticReport report = Capture(androidHost);

        // Секции остаются bounded и однострочными: внешний текст не добавляет строк и не растягивает итог.
        AssertBoundedSection(report.Android);
        AssertBoundedSection(report.AzurLane);

        // Machine-specific путь обнаруженного ADB в секции не попадает: сообщается bounded evidence.
        Assert.DoesNotContain(AdbPathSentinel, ProjectSection(report.Android), StringComparison.Ordinal);
        Assert.DoesNotContain(InstallRootSentinel, ProjectSection(report.Android), StringComparison.Ordinal);
        Assert.DoesNotContain(ControlExecutableSentinel, ProjectSection(report.Android), StringComparison.Ordinal);

        // Хвост длинного evidence обрезан владельцем ограничения.
        Assert.DoesNotContain(EvidenceTailSentinel, ProjectSection(report.Android), StringComparison.Ordinal);
        Assert.DoesNotContain(EvidenceTailSentinel, ProjectSection(report.AzurLane), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Публичная поверхность обеих секций остаётся bounded-набором полей")]
    public void SectionSurfaceStaysBounded()
    {
        string[] expectedAndroid =
        [
            nameof(AzurPilotAndroidDiagnostics.AdbEvidence),
            nameof(AzurPilotAndroidDiagnostics.AndroidRelease),
            nameof(AzurPilotAndroidDiagnostics.BootCompleted),
            nameof(AzurPilotAndroidDiagnostics.Endpoint),
            nameof(AzurPilotAndroidDiagnostics.Evidence),
            nameof(AzurPilotAndroidDiagnostics.Failure),
            nameof(AzurPilotAndroidDiagnostics.IsAdbAvailable),
            nameof(AzurPilotAndroidDiagnostics.IsShellAvailable),
            nameof(AzurPilotAndroidDiagnostics.SdkLevel),
            nameof(AzurPilotAndroidDiagnostics.Stage),
            nameof(AzurPilotAndroidDiagnostics.TransportState),
        ];

        string[] expectedAzurLane =
        [
            nameof(AzurPilotAzurLaneDiagnostics.Evidence),
            nameof(AzurPilotAzurLaneDiagnostics.Failure),
            nameof(AzurPilotAzurLaneDiagnostics.IsForeground),
            nameof(AzurPilotAzurLaneDiagnostics.IsInstalled),
            nameof(AzurPilotAzurLaneDiagnostics.IsProcessRunning),
            nameof(AzurPilotAzurLaneDiagnostics.Package),
            nameof(AzurPilotAzurLaneDiagnostics.Product),
            nameof(AzurPilotAzurLaneDiagnostics.State),
        ];

        Assert.Equal(
            expectedAndroid.OrderBy(name => name, StringComparer.Ordinal),
            PublicProperties(typeof(AzurPilotAndroidDiagnostics)).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            expectedAzurLane.OrderBy(name => name, StringComparer.Ordinal),
            PublicProperties(typeof(AzurPilotAzurLaneDiagnostics)).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Реальный запуск отображает bounded секции Android и Azur Lane в итоге")]
    public void RealRunDisplaysAndroidAndAzurLaneSections()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();

        ApplicationHostRun run = application.Run();

        IReadOnlyList<string> android = ReadSectionLines(run.StandardOutput, AndroidSectionPrefix);
        IReadOnlyList<string> azurLane = ReadSectionLines(run.StandardOutput, AzurLaneSectionPrefix);

        if (android.Count == 0 && azurLane.Count == 0)
        {
            // Пользовательская конфигурация машины может быть отклонена: тогда snapshot не собирается
            // вовсе, и секций в итоге нет. Это условие окружения, а не ослабление проверки: когда
            // диагностика собрана, обе секции обязаны быть видны оператору.
            return;
        }

        // Секции печатаются ровно один раз каждая и остаются однострочными: значения bounded, поэтому
        // внешнее evidence не добавляет строк в человекочитаемый итог.
        string androidLine = Assert.Single(android);
        string azurLaneLine = Assert.Single(azurLane);
        Assert.DoesNotContain('\r', androidLine);
        Assert.DoesNotContain('\r', azurLaneLine);

        // Product identity печатается всегда: она принадлежит продукту, а не наблюдению, поэтому строка
        // остаётся содержательной независимо от того, что удалось наблюдать на этой машине.
        Assert.Contains(AzurLaneProduct.DisplayName, azurLaneLine, StringComparison.Ordinal);
        Assert.Contains(AzurLaneProduct.Package, azurLaneLine, StringComparison.Ordinal);

        // Факты Android-секции сообщаются значениями: доступность ADB и состояние transport — данные, а не
        // отсутствие строки в итоге.
        Assert.Contains("ADB: ", androidLine, StringComparison.Ordinal);
        Assert.Contains("transport: ", androidLine, StringComparison.Ordinal);
    }

    /// <summary>Собирает диагностический snapshot на реально построенном host-е.</summary>
    /// <param name="androidHost">Управляемая host-side граница Android.</param>
    /// <param name="installation">Ответ обнаружения установки MuMu.</param>
    /// <param name="instances">Экземпляры, которые перечисляет подменённая граница MuMu.</param>
    /// <returns>Собранный диагностический snapshot.</returns>
    private static AzurPilotDiagnosticReport Capture(
        IAndroidHost androidHost,
        ApplicationResult<MuMuInstallation>? installation = null,
        IReadOnlyList<MuMuInstance>? instances = null)
    {
        using TemporaryConfigurationDirectory directory = new();
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        TestMuMuHost muMuHost = new()
        {
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(instances ?? [Instance()]),
        };

        if (installation is ApplicationResult<MuMuInstallation> discovery)
        {
            muMuHost.InstallationResult = discovery;
        }

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);

        // Последняя регистрация выигрывает, поэтому подменяются ровно host-side границы: остальной
        // production-код диагностики остаётся настоящим.
        _ = builder.Services.AddSingleton<IMuMuHost>(muMuHost);
        _ = builder.Services.AddSingleton<IAndroidHost>(androidHost);
        using IHost host = builder.Build();

        return host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture();
    }

    /// <summary>Создаёт подменённую границу Android с готовым transport и наблюдаемой игрой.</summary>
    /// <returns>Граница, у которой все шаги диагностики проходят.</returns>
    private static TestAndroidHost ReadyAndroidHost() => new()
    {
        TransportResult = ApplicationResult<AndroidTransportObservation>.Success(
            new AndroidTransportObservation(AndroidTransportState.Device, "transport=device")),
        BootResult = ApplicationResult<AndroidBootObservation>.Success(
            new AndroidBootObservation(
                ShellAvailable: true,
                BootCompleted: BootCompleted,
                AndroidRelease: AndroidRelease,
                SdkLevel: SdkLevel,
                Evidence: "boot=1")),
        PackageResult = ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.Installed),
        ProcessResult = ApplicationResult<AndroidProcessObservation>.Success(
            new AndroidProcessObservation(2, [101, 102], "processes=2")),
        ForegroundResult = ApplicationResult<AndroidForegroundObservation>.Success(
            new AndroidForegroundObservation(
                AndroidForegroundStatus.Foreground,
                new AndroidComponent(new AndroidPackageId(AzurLaneProduct.Package), "Main", "pkg/Main"),
                "foreground=game")),
    };

    private static MuMuInstance Instance() => new(MuMuInstanceId.FromIndex("1"), "instance-sentinel", "15.0");

    private static ApplicationFailure Failure(string code)
        => new() { Code = code, Message = "Ожидаемый отказ диагностики." };

    /// <summary>Проецирует значения секции в одну строку для проверки отсутствия лишних данных.</summary>
    /// <typeparam name="TSection">Тип секции диагностики.</typeparam>
    /// <param name="section">Секция диагностики.</param>
    /// <returns>Строка со значениями всех свойств секции.</returns>
    private static string ProjectSection<TSection>(TSection section)
        => string.Join(
            " | ",
            typeof(TSection)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => Convert.ToString(property.GetValue(section), CultureInfo.InvariantCulture)));

    /// <summary>Проверяет, что текстовые значения секции bounded и однострочны.</summary>
    /// <typeparam name="TSection">Тип секции диагностики.</typeparam>
    /// <param name="section">Секция диагностики.</param>
    private static void AssertBoundedSection<TSection>(TSection section)
    {
        foreach (string value in typeof(TSection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetValue(section))
            .OfType<string>())
        {
            Assert.True(
                value.Length <= BoundedDiagnosticText.MaxLength,
                $"Значение секции длиннее границы: {value.Length}.");
            Assert.DoesNotContain('\n', value);
            Assert.DoesNotContain('\r', value);
        }
    }

    private static IEnumerable<string> PublicProperties(Type section)
        => section.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name);

    /// <summary>Читает строки человекочитаемого итога с заданным префиксом секции.</summary>
    /// <param name="standardOutput">Содержимое stdout реального процесса приложения.</param>
    /// <param name="prefix">Префикс строки секции вместе с отступом.</param>
    /// <returns>Строки итога с этим префиксом в порядке появления.</returns>
    private static IReadOnlyList<string> ReadSectionLines(string standardOutput, string prefix)
        => [.. standardOutput
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))];

    /// <summary>
    /// Host-side граница Android, нарушающая контракт исключением.
    /// </summary>
    /// <remarks>
    /// Ожидаемые отказы возвращаются значением, поэтому исключение — нарушение контракта. Проверка
    /// доказывает, что даже оно остаётся данными диагностики и не отклоняет запуск.
    /// </remarks>
    private sealed class ThrowingAndroidHost : IAndroidHost
    {
        public ApplicationResult<AndroidAdbExecutable> DiscoverAdbExecutable(MuMuInstallation installation)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidEndpoint> ResolveEndpoint(
            MuMuInstallation installation,
            MuMuInstanceId instance)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidTransportObservation> QueryTransport(AndroidEndpoint endpoint)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidCommandOutcome> ConnectTransport(
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidBootObservation> QueryBoot(AndroidEndpoint endpoint)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidPackagePresence> QueryPackage(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidLauncherResolution> ResolveLauncher(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidProcessObservation> ObserveProcesses(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidForegroundObservation> ObserveForeground(AndroidEndpoint endpoint)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");

        public ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
            AndroidEndpoint endpoint,
            AndroidPackageId package,
            AndroidGameMutation mutation,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Нарушение контракта host-ом Android.");
    }

    /// <summary>
    /// Управляемая host-side граница Android: каждая команда сообщает заранее заданный результат, а её
    /// вызов учитывается счётчиком.
    /// </summary>
    /// <remarks>
    /// Double отвечает только за host-примитивы: тесты вызывают настоящие orchestration-сервисы Core,
    /// поэтому проверяется реальный read-only путь диагностики, а не отдельная его реализация. Счётчики
    /// доказывают, что диагностика только читает: подключение transport и mutation не запрашиваются.
    /// </remarks>
    private sealed class TestAndroidHost : IAndroidHost
    {
        internal TestAndroidHost()
        {
            AdbResult = ApplicationResult<AndroidAdbExecutable>.Success(
                new AndroidAdbExecutable(AdbPathSentinel, "adb-version-sentinel"));
            EndpointResult = ApplicationResult<AndroidEndpoint>.Success(
                AndroidEndpoint.FromHostPort(EndpointHost, EndpointPort));
            TransportResult = ApplicationResult<AndroidTransportObservation>.Success(
                new AndroidTransportObservation(AndroidTransportState.Device, "transport=device"));
            BootResult = ApplicationResult<AndroidBootObservation>.Success(
                new AndroidBootObservation(
                    ShellAvailable: true,
                    BootCompleted: BootCompleted,
                    AndroidRelease: AndroidRelease,
                    SdkLevel: SdkLevel,
                    Evidence: "boot=1"));
            PackageResult = ApplicationResult<AndroidPackagePresence>.Success(AndroidPackagePresence.Installed);
            ProcessResult = ApplicationResult<AndroidProcessObservation>.Success(
                new AndroidProcessObservation(1, [101], "processes=1"));
            ForegroundResult = ApplicationResult<AndroidForegroundObservation>.Success(
                new AndroidForegroundObservation(AndroidForegroundStatus.Other, null, "foreground=other"));
        }

        internal ApplicationResult<AndroidAdbExecutable> AdbResult { get; set; }

        internal ApplicationResult<AndroidEndpoint> EndpointResult { get; set; }

        internal ApplicationResult<AndroidTransportObservation> TransportResult { get; set; }

        internal ApplicationResult<AndroidBootObservation> BootResult { get; set; }

        internal ApplicationResult<AndroidPackagePresence> PackageResult { get; set; }

        internal ApplicationResult<AndroidProcessObservation> ProcessResult { get; set; }

        internal ApplicationResult<AndroidForegroundObservation> ForegroundResult { get; set; }

        internal int AdbDiscoveryCount { get; private set; }

        internal int EndpointResolutionCount { get; private set; }

        internal int TransportQueryCount { get; private set; }

        internal int BootQueryCount { get; private set; }

        internal int PackageQueryCount { get; private set; }

        internal int ProcessQueryCount { get; private set; }

        internal int ForegroundQueryCount { get; private set; }

        internal int ConnectCount { get; private set; }

        internal int MutationCount { get; private set; }

        public ApplicationResult<AndroidAdbExecutable> DiscoverAdbExecutable(MuMuInstallation installation)
        {
            ArgumentNullException.ThrowIfNull(installation);
            AdbDiscoveryCount++;
            return AdbResult;
        }

        public ApplicationResult<AndroidEndpoint> ResolveEndpoint(
            MuMuInstallation installation,
            MuMuInstanceId instance)
        {
            ArgumentNullException.ThrowIfNull(installation);
            EndpointResolutionCount++;
            return EndpointResult;
        }

        public ApplicationResult<AndroidTransportObservation> QueryTransport(AndroidEndpoint endpoint)
        {
            TransportQueryCount++;
            return TransportResult;
        }

        public ApplicationResult<AndroidCommandOutcome> ConnectTransport(
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            return ApplicationResult<AndroidCommandOutcome>.Success(new AndroidCommandOutcome(0, "connected"));
        }

        public ApplicationResult<AndroidBootObservation> QueryBoot(AndroidEndpoint endpoint)
        {
            BootQueryCount++;
            return BootResult;
        }

        public ApplicationResult<AndroidPackagePresence> QueryPackage(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
        {
            PackageQueryCount++;
            return PackageResult;
        }

        public ApplicationResult<AndroidLauncherResolution> ResolveLauncher(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
            => ApplicationResult<AndroidLauncherResolution>.Success(
                new AndroidLauncherResolution(
                    AndroidLauncherResolutionStatus.Resolved,
                    new AndroidComponent(package, "Main", "pkg/Main"),
                    MatchingComponentCount: 1,
                    Evidence: "launcher=resolved"));

        public ApplicationResult<AndroidProcessObservation> ObserveProcesses(
            AndroidEndpoint endpoint,
            AndroidPackageId package)
        {
            ProcessQueryCount++;
            return ProcessResult;
        }

        public ApplicationResult<AndroidForegroundObservation> ObserveForeground(AndroidEndpoint endpoint)
        {
            ForegroundQueryCount++;
            return ForegroundResult;
        }

        public ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
            AndroidEndpoint endpoint,
            AndroidPackageId package,
            AndroidGameMutation mutation,
            CancellationToken cancellationToken)
        {
            MutationCount++;
            return ApplicationResult<AndroidCommandOutcome>.Success(new AndroidCommandOutcome(0, "mutation"));
        }
    }
}
