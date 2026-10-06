using System.Globalization;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Application-level доказательства, полученные в отдельном процессе из полностью управляемого каталога:
/// проба выполняет реальный startup приложения через composition root с явно переданным путём
/// конфигурации.
/// </summary>
/// <remarks>
/// <para>
/// Продукт не разбирает аргументы запуска, поэтому единственный способ продиктовать приложению
/// конкретную конфигурацию и конкретный состав каталога — вызвать composition кодом. Это делает
/// тестовая проба: <c>--application-composition &lt;путь&gt;</c> — её собственный режим, а не
/// пользовательская опция приложения.
/// </para>
/// <para>
/// Проверки не зависят от пользовательского <c>%LOCALAPPDATA%\AzurPilot\config.json</c>: путь
/// конфигурации всегда временный, файл пользователя не читается, не подменяется и не создаётся.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ApplicationCompositionProbeTests
{
    private const string InformationConfiguration =
        """
        {
          "schemaVersion": 1,
          "diagnostics": {
            "minimumLevel": "Information"
          }
        }
        """;

    private const string InvalidConfiguration =
        """
        {
          "schemaVersion": 1,
          "diagnostics": {
            "minimumLevel": "Verbose"
          }
        }
        """;

    private const string UnsupportedSchemaConfiguration =
        """
        {
          "schemaVersion": 3,
          "diagnostics": {
            "minimumLevel": "Information"
          }
        }
        """;

    /// <summary>
    /// Документ поддерживаемой схемы v2 без обязательной секции <c>mumu</c>: версия поддерживается, но
    /// документ не соответствует своей схеме.
    /// </summary>
    private const string CurrentSchemaWithoutMuMuSectionConfiguration =
        """
        {
          "schemaVersion": 2,
          "diagnostics": {
            "minimumLevel": "Information"
          }
        }
        """;

    /// <summary>Property structured diagnostics с версией ABI, полученной из native boundary.</summary>
    private const string AbiVersionProperty = "AbiVersion";

    /// <summary>Property structured diagnostics с версией OpenCV, полученной из native boundary.</summary>
    private const string OpencvVersionProperty = "OpencvVersion";

    /// <summary>Property structured diagnostics с поддержанными capability native boundary.</summary>
    private const string CapabilitiesProperty = "Capabilities";

    /// <summary>Property structured diagnostics с источником загруженной конфигурации.</summary>
    private const string ConfigurationSourceProperty = "ConfigurationSource";

    /// <summary>Property structured diagnostics с версией схемы конфигурации.</summary>
    private const string SchemaVersionProperty = "SchemaVersion";

    /// <summary>Фрагмент документа конфигурации, который не должен попадать в вывод диагностики.</summary>
    private const string ConfigurationDocumentFragment = "\"minimumLevel\": \"Information\"";

    /// <summary>Длина correlation identifier операции.</summary>
    private const int CorrelationIdLength = 32;

    [Fact(DisplayName = "Проба без native runtime сообщает application-level native_unavailable")]
    public void ProbeWithoutNativeRuntimeReportsNativeUnavailable()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateWithoutNativeRuntime();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(InformationConfiguration));

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.NativeUnavailable, run.ApplicationExitCode);
        Assert.Equal(ApplicationFailure.NativeUnavailable, run.ReadFailureCode());

        // Граница потоков не зависит от исхода: и в failure-пути stdout не содержит ни одной structured
        // записи — весь structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));

        // Отказ получен как application-level: он виден и в structured diagnostics, а не только в коде
        // выхода. Сведений native boundary в snapshot нет — запрос к ней не состоялся.
        Assert.Contains(
            run.Logs,
            log => string.Equals(log.FailureCode, ApplicationFailure.NativeUnavailable, StringComparison.Ordinal));
        Assert.DoesNotContain(run.Logs, log => log.State.ContainsKey(AbiVersionProperty));
    }

    [Fact(DisplayName = "Проба с fixture несовместимого ABI сообщает application-level native_incompatible")]
    public void ProbeWithIncompatibleAbiFixtureReportsNativeIncompatible()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateWithIncompatibleAbiFixture();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(InformationConfiguration));

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.NativeIncompatible, run.ApplicationExitCode);
        Assert.Equal(ApplicationFailure.NativeIncompatible, run.ReadFailureCode());

        // Граница потоков не зависит от исхода: и в failure-пути stdout не содержит ни одной structured
        // записи — весь structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));

        Assert.Contains(
            run.Logs,
            log => string.Equals(log.FailureCode, ApplicationFailure.NativeIncompatible, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Проба с рабочим native runtime сообщает здоровый snapshot с реальными ABI/OpenCV evidence")]
    public void ProbeWithNativeRuntimeReportsHealthySnapshot()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(InformationConfiguration));

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.Success, run.ApplicationExitCode);

        // stdout пробы содержит только её собственные строки: structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));
        Assert.NotEmpty(run.Logs);

        // stderr несёт только project-owned события: с реальной MuMu-capability проект владеет и
        // категориями MuMu-владельцев (discovery — Windows-адаптер, выбор экземпляра — Core
        // orchestration), но чужие категории этим не разрешаются.
        Assert.All(
            run.Logs,
            log => Assert.True(
                ProjectOwnedLogCategories.Contains(log),
                $"Чужая категория логирования в stderr: {log.Category}."));

        // Один correlation identifier связывает все события запуска: MuMu-владельцы несут его scope
        // записи, application host — ещё и явным structured property.
        string identifier = Assert.Single(
            run.Logs.Select(log => log.TraceId).OfType<string>().Distinct(StringComparer.Ordinal));
        Assert.Equal(CorrelationIdLength, identifier.Length);
        Assert.All(run.Logs, log => Assert.Equal(identifier, log.TraceId));
        Assert.All(
            run.Logs.Where(log => log.CorrelationId is not null),
            log => Assert.Equal(identifier, log.CorrelationId));

        // Здоровый snapshot несёт реальные evidence из native boundary: версии сверяются с владельцами
        // этих значений — заголовком ABI и manifest артефакта OpenCV.
        StructuredLogRecord verified = Assert.Single(run.Logs, log => log.State.ContainsKey(AbiVersionProperty));
        Assert.Equal(
            PinnedVersions.NativeAbi.ToString(CultureInfo.InvariantCulture),
            StructuredLogRecord.Read(verified.State, AbiVersionProperty));
        Assert.Equal(
            PinnedVersions.OpenCv.ToString(),
            StructuredLogRecord.Read(verified.State, OpencvVersionProperty));
        Assert.Equal("core, imgcodecs", StructuredLogRecord.Read(verified.State, CapabilitiesProperty));
    }

    [Fact(DisplayName = "Диагностика сообщает bounded сведения о конфигурации, а не её документ")]
    public void DiagnosticsReportBoundedConfigurationInsteadOfDocument()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();
        string configurationPath = directory.WriteConfiguration(InformationConfiguration);

        ApplicationProbeRun run = probe.Run(configurationPath);

        Assert.Equal(AzurPilotExitCode.Success, run.ApplicationExitCode);

        // Файл действительно содержит документ в этой форме, поэтому проверка ниже что-то доказывает.
        Assert.Contains(
            ConfigurationDocumentFragment,
            File.ReadAllText(configurationPath),
            StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigurationDocumentFragment, run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigurationDocumentFragment, run.StandardError, StringComparison.Ordinal);

        // Вместо документа диагностика сообщает bounded сведения о конфигурации.
        StructuredLogRecord configuration =
            Assert.Single(run.Logs, log => log.State.ContainsKey(ConfigurationSourceProperty));
        Assert.Equal("File", StructuredLogRecord.Read(configuration.State, ConfigurationSourceProperty));
        Assert.Equal(
            AzurPilotConfiguration.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
            StructuredLogRecord.Read(configuration.State, SchemaVersionProperty));
    }

    [Fact(DisplayName = "Проба с невалидной конфигурацией сообщает configuration_invalid без запроса native")]
    public void ProbeWithInvalidConfigurationReportsConfigurationInvalid()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(InvalidConfiguration));

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.ConfigurationInvalid, run.ApplicationExitCode);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, run.ReadFailureCode());

        // Граница потоков не зависит от исхода: и в failure-пути stdout не содержит ни одной structured
        // записи — весь structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));

        // Строгая валидация не подменяется built-in defaults: native boundary не запрашивается вовсе.
        Assert.DoesNotContain(run.Logs, log => log.State.ContainsKey(AbiVersionProperty));
    }

    [Fact(DisplayName = "Проба с неподдерживаемой версией схемы сообщает configuration_schema_unsupported")]
    public void ProbeWithUnsupportedSchemaReportsConfigurationSchemaUnsupported()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(UnsupportedSchemaConfiguration));

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.ConfigurationSchemaUnsupported, run.ApplicationExitCode);
        Assert.Equal(ApplicationFailure.ConfigurationSchemaUnsupported, run.ReadFailureCode());

        // Граница потоков не зависит от исхода: и в failure-пути stdout не содержит ни одной structured
        // записи — весь structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));
    }

    [Fact(DisplayName = "Проба с документом v2 без секции mumu сообщает configuration_invalid")]
    public void ProbeWithSupportedSchemaViolationReportsConfigurationInvalid()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(
            directory.WriteConfiguration(CurrentSchemaWithoutMuMuSectionConfiguration));

        // Версия поддерживается, поэтому это не configuration_schema_unsupported, а несоответствие схеме.
        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.ConfigurationInvalid, run.ApplicationExitCode);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, run.ReadFailureCode());

        // Граница потоков не зависит от исхода: и в failure-пути stdout не содержит ни одной structured
        // записи — весь structured runtime log идёт в stderr.
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));

        // Строгая валидация не подменяется built-in defaults: native boundary не запрашивается вовсе.
        Assert.DoesNotContain(run.Logs, log => log.State.ContainsKey(AbiVersionProperty));
    }
}
