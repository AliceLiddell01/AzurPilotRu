using System.Runtime.InteropServices;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using AzurPilot.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки composition root: фактический состав построенного host-а и его runtime services.
/// </summary>
/// <remarks>
/// Проверки выполняются на реально построенном host-е, а не на исходном тексте composition: они
/// доказывают, что нежелательные providers не подключены, конфигурация попадает в DI как готовый
/// snapshot, а её минимальный уровень логирования действительно применяется.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AzurPilotHostCompositionTests
{
    private const string WarningLevelConfiguration = """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Warning"}}""";

    [Fact(DisplayName = "Host не подключает нежелательные configuration и logging providers")]
    public void HostDoesNotAttachUnwantedProviders()
    {
        using TemporaryConfigurationDirectory directory = new();

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(LoadDefaults(directory));
        using IHost host = builder.Build();

        // Пользовательская конфигурация — один строгий JSON snapshot, а не агрегатор providers: у host-а
        // нет ни appsettings.json, ни environment variables, ни command line, ни user secrets, и вообще
        // нет ни одного источника данных конфигурации.
        Assert.Empty(builder.Configuration.AsEnumerable());

        // Единственный подключённый источник — внутренний пустой memory source самого HostApplicationBuilder.
        Assert.All(
            builder.Configuration.Sources,
            source => Assert.Equal("MemoryConfigurationSource", source.GetType().Name));

        ILoggerProvider provider = Assert.Single(host.Services.GetServices<ILoggerProvider>());
        Assert.Equal("ConsoleLoggerProvider", provider.GetType().Name);

        // Единственный provider пишет встроенным JSON console formatter: Debug/EventSource/EventLog
        // не подключены, а действующий formatter — именно json.
        Assert.Equal(
            "json",
            host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
    }

    [Fact(DisplayName = "Config snapshot попадает в DI и его minimum level применяется к логированию")]
    public void ConfigurationSnapshotIsRegisteredAndAppliesMinimumLevel()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteConfiguration(WarningLevelConfiguration);
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.ConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);
        using IHost host = builder.Build();

        AzurPilotConfigurationSnapshot snapshot =
            host.Services.GetRequiredService<AzurPilotConfigurationSnapshot>();
        Assert.Same(configuration.Value, snapshot);
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);

        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzurPilot.Tests");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact(DisplayName = "Изменение файла конфигурации не меняет уже загруженный snapshot")]
    public void ConfigurationSnapshotIsNotReloadedAfterFileChange()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteConfiguration(WarningLevelConfiguration);
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.ConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);
        using IHost host = builder.Build();
        AzurPilotConfigurationSnapshot snapshot =
            host.Services.GetRequiredService<AzurPilotConfigurationSnapshot>();

        // Файл меняется после загрузки: ни snapshot, ни применённый уровень логирования не меняются,
        // то есть file watcher и hot reload отсутствуют.
        _ = directory.WriteConfiguration("""{"schemaVersion":1,"diagnostics":{"minimumLevel":"Critical"}}""");

        Assert.Same(snapshot, host.Services.GetRequiredService<AzurPilotConfigurationSnapshot>());
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);

        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzurPilot.Tests");
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact(DisplayName = "Runtime services host-а — snapshot, диагностика и маппер failures — резолвятся из DI")]
    public void RuntimeServicesAreResolvedFromContainer()
    {
        using TemporaryConfigurationDirectory directory = new();

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(LoadDefaults(directory));
        using IHost host = builder.Build();

        Func<Exception, ApplicationFailure> mapFailure =
            host.Services.GetRequiredService<Func<Exception, ApplicationFailure>>();
        Assert.Equal(
            ApplicationFailure.NativeUnavailable,
            mapFailure(new NativeBoundaryUnavailableException("Native библиотека недоступна.")).Code);

        AzurPilotDiagnosticReport report = host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture();

        // Диагностика сообщает bounded сведения о приложении и конфигурации.
        Assert.Equal("AzurPilot.App", report.Application.AssemblyName);
        Assert.False(string.IsNullOrWhiteSpace(report.Application.AssemblyVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Application.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Application.RuntimeFramework));
        Assert.Equal(Environment.ProcessId, report.Application.ProcessId);
        Assert.Equal(Environment.Is64BitProcess, report.Application.Is64BitProcess);
        Assert.Equal(
            RuntimeInformation.ProcessArchitecture.ToString(),
            report.Application.ProcessArchitecture);
        Assert.Equal(AzurPilotConfigurationSource.BuiltInDefaults, report.Configuration.Source);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, report.Configuration.SchemaVersion);
        Assert.Equal(LogLevel.Information, report.Configuration.MinimumLevel);

        // Здоровый snapshot содержит реальные evidence native boundary, а не заглушку.
        Assert.True(report.Native.IsAvailable);
        Assert.True(report.Native.IsAbiCompatible);
        Assert.Null(report.Native.Failure);
        Assert.Equal(PinnedVersions.NativeAbi, report.Native.AbiVersion ?? 0);

        Version? opencvVersion = report.Native.OpencvVersion;
        Assert.NotNull(opencvVersion);
        Assert.Equal(PinnedVersions.OpenCv, opencvVersion);

        Assert.Contains("core", report.Native.Capabilities);
        Assert.True(report.Native.OpencvExecuted);
        Assert.False(string.IsNullOrWhiteSpace(report.Native.BuildInfo));
    }

    private static ApplicationResult<AzurPilotConfigurationSnapshot> LoadDefaults(
        TemporaryConfigurationDirectory directory)
    {
        // Путь, по которому файла заведомо нет: загрузка даёт встроенные defaults.
        return AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);
    }
}
