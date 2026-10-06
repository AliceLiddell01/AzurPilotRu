using System.Globalization;
using System.Reflection;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Configuration;
using AzurPilot.Tests.MuMu;
using AzurPilot.Windows.MuMu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки bounded MuMu-секции диагностического snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Проверки выполняются на реально построенном host-е: подменяется только host-side граница MuMu, поэтому
/// остальной production-код диагностики — разрешение экземпляра через Core orchestration, bounded-форма
/// секции и логирование — остаётся тем же. Ни установленная MuMu, ни реальный control utility для этих
/// проверок не требуются.
/// </para>
/// <para>
/// Секция не является исключением из правила «ожидаемый отказ — данные»: отсутствие установки,
/// неподдерживаемая control surface, неразрешённый выбор экземпляра и наблюдённый <c>Stopped</c> обязаны
/// возвращаться данными, а не отклонять запуск.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AzurPilotMuMuDiagnosticsTests
{
    /// <summary>Версия схемы, которую объявляет legacy-документ v1.</summary>
    private const int LegacySchemaVersion = 1;

    /// <summary>Bounded статус control surface: форма распознана.</summary>
    private const string SupportedStatus = "supported";

    /// <summary>Bounded статус control surface: форма не поддерживается.</summary>
    private const string UnsupportedStatus = "unsupported";

    /// <summary>Bounded статус control surface: установка не обнаружена.</summary>
    private const string AbsentStatus = "absent";

    /// <summary>Bounded статус control surface: поддержку доказать не удалось.</summary>
    private const string UnknownStatus = "unknown";

    /// <summary>Синтетический корень установки: в исходниках проверок нет machine-specific путей.</summary>
    private const string InstallRootSentinel = "install-root-sentinel";

    /// <summary>Синтетический путь control surface установки.</summary>
    private const string ControlExecutableSentinel = "control-executable-sentinel";

    /// <summary>Хвост длинного evidence: до bounded-секции он доходить не должен.</summary>
    private const string EvidenceTailSentinel = "evidence-tail-sentinel";

    /// <summary>Ожидаемое множество свойств bounded MuMu-секции.</summary>
    private static readonly string[] ExpectedSectionProperties =
    [
        nameof(AzurPilotMuMuDiagnostics.ControlSurfaceStatus),
        nameof(AzurPilotMuMuDiagnostics.ConfiguredInstance),
        nameof(AzurPilotMuMuDiagnostics.Evidence),
        nameof(AzurPilotMuMuDiagnostics.Failure),
        nameof(AzurPilotMuMuDiagnostics.IsInstallationDiscovered),
        nameof(AzurPilotMuMuDiagnostics.LifecycleState),
        nameof(AzurPilotMuMuDiagnostics.SelectedInstanceDisplayName),
        nameof(AzurPilotMuMuDiagnostics.SelectedInstanceId),
        nameof(AzurPilotMuMuDiagnostics.Version),
    ];

    [Fact(DisplayName = "Отсутствие установки MuMu — нормальный диагностический результат")]
    public void AbsentInstallationIsNormalDiagnosticResult()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuInstallationNotFound)),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Null(muMu.Version);
        Assert.Equal(AbsentStatus, muMu.ControlSurfaceStatus);
        Assert.Equal("auto", muMu.ConfiguredInstance);
        Assert.Null(muMu.SelectedInstanceId);
        Assert.Null(muMu.SelectedInstanceDisplayName);
        Assert.Null(muMu.LifecycleState);
        Assert.Null(muMu.Evidence);
        Assert.Equal(ApplicationFailure.MuMuInstallationNotFound, muMu.Failure!.Code);

        // Диагностика не наблюдала состояние и не запросила ни одной mutation: запуск эмулятора не является
        // частью startup.
        Assert.Equal(0, host.ObservationCount);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Неподдерживаемая control surface отображается как unsupported, а не как отсутствие MuMu")]
    public void UnsupportedControlSurfaceIsReportedAsUnsupported()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuControlSurfaceUnsupported)),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Equal(UnsupportedStatus, muMu.ControlSurfaceStatus);
        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, muMu.Failure!.Code);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Неоднозначность установки не истолковывается: статус unknown, а не absent и не unsupported")]
    public void AmbiguousInstallationIsReportedAsUnknown()
    {
        // Установок несколько, доказуемого выбора между ними нет: adapter сообщает
        // mumu_installation_ambiguous. Из этого нельзя вывести ни «установки нет», ни «форма control
        // surface не поддерживается», поэтому статус обязан остаться недоказанным.
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuInstallationAmbiguous)),
            // Mutation запрещена: если бы диагностика её запросила, проверка это увидит.
            MutationHandler = static _ => throw new InvalidOperationException(
                "MuMu mutation не является частью диагностики."),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Null(muMu.Version);
        Assert.Equal(UnknownStatus, muMu.ControlSurfaceStatus);

        // Недоказанный статус — не догадка: он не превращается ни в «установки нет», ни в «форма не
        // поддерживается».
        Assert.NotEqual(AbsentStatus, muMu.ControlSurfaceStatus);
        Assert.NotEqual(UnsupportedStatus, muMu.ControlSurfaceStatus);

        Assert.Null(muMu.SelectedInstanceId);
        Assert.Null(muMu.SelectedInstanceDisplayName);
        Assert.Null(muMu.LifecycleState);
        Assert.Null(muMu.Evidence);

        // Причина сообщается application-отказом как данные: запуск приложения не отклоняется.
        Assert.Equal(ApplicationFailure.MuMuInstallationAmbiguous, muMu.Failure!.Code);

        // Диагностика остановилась на обнаружении: ни наблюдения состояния, ни mutation.
        Assert.Equal(0, host.ObservationCount);
        Assert.Equal(0, host.MutationCount);
    }

    [Theory(DisplayName = "Код обнаружения без доказанного смысла даёт unknown, а не догадку")]
    [InlineData(ApplicationFailure.InternalError)]
    [InlineData(ApplicationFailure.ConfigurationInvalid)]
    [InlineData(ApplicationFailure.MuMuInstanceNotFound)]
    public void UndecidedDiscoveryFailureCodeIsReportedAsUnknown(string failureCode)
    {
        // Коды вне перечня доказанных отказов обнаружения не истолковываются: статус остаётся недоказанным,
        // поэтому добавление нового кода не может молча придать ему смысл отсутствия или неподдержки.
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(Failure(failureCode)),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Equal(UnknownStatus, muMu.ControlSurfaceStatus);
        Assert.Equal(failureCode, muMu.Failure!.Code);
        Assert.Equal(0, host.ObservationCount);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Машина без MuMu: production host и обнаружение сообщают отсутствие как данные")]
    public void RealHostWithoutInstallationReportsAbsence()
    {
        // Установки нет: ни uninstall-записи, ни install metadata не сообщают кандидата, поэтому
        // production-обнаружение возвращает mumu_installation_not_found, не обращаясь к файловой системе
        // и не запуская ни одного процесса. Подменены только узкие внешние границы адаптеров — код
        // обнаружения и host-side поверхности остаётся production-кодом платформенной boundary.
        MuMuWindowsHost host = new(
            new EmptyRegistrySource(),
            new EmptyMetadataSource(),
            new WindowsMuMuFileSystemProbe(),
            new MuMuProcessRunner(),
            NullLogger<MuMuWindowsHost>.Instance);

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Null(muMu.Version);
        Assert.Equal(AbsentStatus, muMu.ControlSurfaceStatus);
        Assert.Null(muMu.SelectedInstanceId);
        Assert.Null(muMu.LifecycleState);
        Assert.Equal(ApplicationFailure.MuMuInstallationNotFound, muMu.Failure!.Code);
    }

    [Fact(DisplayName = "Остановленный экземпляр — нормальное состояние диагностики без mutation")]
    public void StoppedInstanceIsReportedWithoutMutation()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(Installation()),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([Instance("1", "Тестовый экземпляр")]),
            ObservationResult = ApplicationResult<MuMuInstanceState>.Success(
                new MuMuInstanceState(MuMuLifecycleState.Stopped, "index=1;player_state=absent")),
            // Mutation запрещена: если бы диагностика её запросила, проверка это увидит.
            MutationHandler = static _ => throw new InvalidOperationException(
                "MuMu mutation не является частью диагностики."),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.True(muMu.IsInstallationDiscovered);
        Assert.Equal("6.8.0.0", muMu.Version);
        Assert.Equal(SupportedStatus, muMu.ControlSurfaceStatus);
        Assert.Equal("mumu:1", muMu.SelectedInstanceId);
        Assert.Equal("Тестовый экземпляр", muMu.SelectedInstanceDisplayName);
        Assert.Equal(MuMuLifecycleState.Stopped, muMu.LifecycleState);
        Assert.Equal("index=1;player_state=absent", muMu.Evidence);
        Assert.Null(muMu.Failure);

        // Ровно одно авторитетное наблюдение состояния и ни одной mutation.
        Assert.Equal(1, host.ObservationCount);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Неоднозначный автоматический выбор — отказ диагностики без выбранного экземпляра")]
    public void AmbiguousAutomaticSelectionIsReportedWithoutSelection()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(Installation()),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
                [Instance("1", "Первый"), Instance("2", "Второй")]),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.True(muMu.IsInstallationDiscovered);
        Assert.Equal(SupportedStatus, muMu.ControlSurfaceStatus);
        Assert.Null(muMu.SelectedInstanceId);
        Assert.Null(muMu.LifecycleState);
        Assert.Equal(ApplicationFailure.MuMuInstanceAmbiguous, muMu.Failure!.Code);

        // Состояние не наблюдается и mutation не запрашивается, пока экземпляр не разрешён.
        Assert.Equal(0, host.ObservationCount);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Явный выбор из конфигурации ищется по stable identity, а не по отображаемому имени")]
    public void ExplicitSelectionUsesStableIdentity()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(Installation()),
            // Одинаковые отображаемые имена: identity выбирается по номеру экземпляра.
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
                [Instance("7", "Одинаковое имя"), Instance("9", "Одинаковое имя")]),
            ObservationResult = ApplicationResult<MuMuInstanceState>.Success(
                new MuMuInstanceState(MuMuLifecycleState.Running, "index=7;player_state=start_finished")),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("mumu:7"), host).MuMu;

        Assert.Equal("mumu:7", muMu.ConfiguredInstance);
        Assert.Equal("mumu:7", muMu.SelectedInstanceId);
        Assert.Equal(MuMuLifecycleState.Running, muMu.LifecycleState);
        Assert.Null(muMu.Failure);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Наблюдение состояния отказало — секция остаётся данными, а запуск не отклоняется")]
    public void ObservationFailureIsReportedAsData()
    {
        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(Installation()),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([Instance("1", "Экземпляр")]),
            ObservationResult = ApplicationResult<MuMuInstanceState>.Failure(
                Failure(ApplicationFailure.MuMuInstanceNotFound)),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        Assert.True(muMu.IsInstallationDiscovered);
        Assert.Equal(SupportedStatus, muMu.ControlSurfaceStatus);
        Assert.Equal("mumu:1", muMu.SelectedInstanceId);
        Assert.Null(muMu.LifecycleState);
        Assert.Null(muMu.Evidence);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, muMu.Failure!.Code);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact(DisplayName = "Непредвиденное нарушение контракта host-ом не выходит наружу и не отклоняет запуск")]
    public void UnexpectedHostFailureStaysInsideDiagnostics()
    {
        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), new ThrowingMuMuHost()).MuMu;

        Assert.False(muMu.IsInstallationDiscovered);
        Assert.Equal(UnknownStatus, muMu.ControlSurfaceStatus);
        Assert.Equal(ApplicationFailure.InternalError, muMu.Failure!.Code);
    }

    [Fact(DisplayName = "MuMu-секция bounded: состав свойств фиксирован, внешние значения усечены")]
    public void MuMuSectionIsBounded()
    {
        string longVersion = new('9', 4096);
        string longEvidence = new string('e', 4096) + EvidenceTailSentinel;
        string longDisplayName = "Первая строка\nВторая строка\t" + new string('n', 4096);

        TestMuMuHost host = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(
                new MuMuInstallation(longVersion, InstallRootSentinel, ControlExecutableSentinel)),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
                [Instance("1", longDisplayName)]),
            ObservationResult = ApplicationResult<MuMuInstanceState>.Success(
                new MuMuInstanceState(MuMuLifecycleState.Running, longEvidence)),
        };

        AzurPilotMuMuDiagnostics muMu = Capture(CurrentSchemaConfiguration("auto"), host).MuMu;

        // Состав секции закрыт: полного дампа реестра, списка процессов, полного stdout/stderr control
        // utility, полного документа конфигурации и machine-specific путей в ней нет.
        string[] actualProperties =
            [.. typeof(AzurPilotMuMuDiagnostics)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)];
        Assert.Equal(
            [.. ExpectedSectionProperties.OrderBy(name => name, StringComparer.Ordinal)],
            actualProperties);

        string projection = ProjectSection(muMu);
        Assert.DoesNotContain(InstallRootSentinel, projection, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlExecutableSentinel, projection, StringComparison.Ordinal);
        Assert.DoesNotContain(EvidenceTailSentinel, projection, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", projection, StringComparison.Ordinal);

        // Внешние значения приведены к bounded однострочной форме владельцем ограничения.
        Assert.Equal(MuMuBoundedText.MaxLength, muMu.Version!.Length);
        Assert.Equal(MuMuBoundedText.MaxLength, muMu.SelectedInstanceDisplayName!.Length);
        Assert.Equal(MuMuBoundedText.MaxLength, muMu.Evidence!.Length);
        Assert.DoesNotContain('\n', muMu.SelectedInstanceDisplayName);
        Assert.DoesNotContain('\t', muMu.SelectedInstanceDisplayName);
    }

    [Fact(DisplayName = "Configuration-секция различает схему источника и эффективную схему без дампа конфигурации")]
    public void ConfigurationSectionDistinguishesSourceAndEffectiveSchema()
    {
        TestMuMuHost legacyHost = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuInstallationNotFound)),
        };
        AzurPilotConfigurationDiagnostics legacy =
            Capture(LegacyConfiguration, legacyHost).Configuration;

        Assert.Equal(AzurPilotConfigurationSource.File, legacy.Source);
        Assert.Equal(LegacySchemaVersion, legacy.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, legacy.SchemaVersion);
        Assert.True(legacy.IsLegacySchemaNormalized);

        TestMuMuHost currentHost = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Failure(
                Failure(ApplicationFailure.MuMuInstallationNotFound)),
        };
        AzurPilotConfigurationDiagnostics current =
            Capture(CurrentSchemaConfiguration("auto"), currentHost).Configuration;

        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, current.SourceSchemaVersion);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, current.SchemaVersion);
        Assert.False(current.IsLegacySchemaNormalized);

        // Нормализация видна по версиям схемы, а сам документ конфигурации в секцию не попадает.
        string projection = string.Join(
            " | ",
            typeof(AzurPilotConfigurationDiagnostics)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => Convert.ToString(property.GetValue(legacy), CultureInfo.InvariantCulture)));
        Assert.DoesNotContain("\"minimumLevel\"", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", projection, StringComparison.Ordinal);
    }

    /// <summary>Собирает диагностический snapshot на реально построенном host-е.</summary>
    /// <param name="configurationDocument">Документ пользовательской конфигурации.</param>
    /// <param name="muMuHost">Управляемая host-side граница MuMu.</param>
    /// <returns>Собранный диагностический snapshot.</returns>
    private static AzurPilotDiagnosticReport Capture(string configurationDocument, IMuMuHost muMuHost)
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteConfiguration(configurationDocument);
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.ConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);

        // Последняя регистрация выигрывает, поэтому подменяется ровно host-side граница MuMu: остальной
        // production-код диагностики остаётся настоящим.
        _ = builder.Services.AddSingleton<IMuMuHost>(muMuHost);
        using IHost host = builder.Build();

        return host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture();
    }

    /// <summary>Проецирует значения секции в одну строку для проверки отсутствия лишних данных.</summary>
    /// <param name="muMu">MuMu-секция диагностики.</param>
    /// <returns>Строка со значениями всех свойств секции.</returns>
    private static string ProjectSection(AzurPilotMuMuDiagnostics muMu)
        => string.Join(
            " | ",
            typeof(AzurPilotMuMuDiagnostics)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => Convert.ToString(property.GetValue(muMu), CultureInfo.InvariantCulture)));

    private static MuMuInstallation Installation()
        => new("6.8.0.0", InstallRootSentinel, ControlExecutableSentinel);

    private static MuMuInstance Instance(string index, string displayName)
        => new(MuMuInstanceId.FromIndex(index), displayName, "15.0");

    private static ApplicationFailure Failure(string code)
        => new() { Code = code, Message = "Ожидаемый отказ MuMu-диагностики." };

    /// <summary>Документ текущей схемы v2 с заданным значением <c>mumu.instance</c>.</summary>
    /// <param name="instance">Значение выбора экземпляра.</param>
    /// <returns>Документ конфигурации схемы v2.</returns>
    private static string CurrentSchemaConfiguration(string instance) => $$"""
        {
          "schemaVersion": {{AzurPilotConfiguration.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}},
          "diagnostics": {
            "minimumLevel": "Information"
          },
          "mumu": {
            "instance": "{{instance}}"
          }
        }
        """;

    /// <summary>
    /// Legacy-документ схемы v1: он нормализуется к v2 в памяти, и на диск ничего не пишется.
    /// </summary>
    /// <remarks>
    /// Номер версии в документе — часть legacy-контракта v1, а не значение, которым владеет этот файл,
    /// поэтому он присутствует в тексте документа и сверяется с <see cref="LegacySchemaVersion"/>.
    /// </remarks>
    private const string LegacyConfiguration = """
        {
          "schemaVersion": 1,
          "diagnostics": {
            "minimumLevel": "Information"
          }
        }
        """;

    /// <summary>
    /// Host-side граница MuMu, нарушающая контракт исключением.
    /// </summary>
    /// <remarks>
    /// Ожидаемые отказы возвращаются значением, поэтому исключение — нарушение контракта. Проверка
    /// доказывает, что даже оно остаётся данными диагностики и не отклоняет запуск.
    /// </remarks>
    private sealed class ThrowingMuMuHost : IMuMuHost
    {
        public ApplicationResult<MuMuInstallation> DiscoverInstallation()
            => throw new InvalidOperationException("Нарушение контракта host-ом MuMu.");

        public ApplicationResult<IReadOnlyList<MuMuInstance>> EnumerateInstances(MuMuInstallation installation)
            => throw new InvalidOperationException("Нарушение контракта host-ом MuMu.");

        public ApplicationResult<MuMuInstanceState> ObserveInstanceState(
            MuMuInstallation installation,
            MuMuInstanceId instance)
            => throw new InvalidOperationException("Нарушение контракта host-ом MuMu.");

        public ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(
            MuMuInstallation installation,
            MuMuInstanceId instance,
            MuMuLifecycleMutation mutation,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Нарушение контракта host-ом MuMu.");
    }

    /// <summary>Источник uninstall-записей машины без MuMu.</summary>
    private sealed class EmptyRegistrySource : IMuMuInstallationRegistrySource
    {
        public IReadOnlyList<MuMuRegistryCandidate> ReadCandidates() => [];
    }

    /// <summary>Источник install metadata машины без MuMu.</summary>
    private sealed class EmptyMetadataSource : IMuMuInstallMetadataSource
    {
        public IReadOnlyList<MuMuInstallMetadataDocument> ReadDocuments() => [];
    }
}
