using System.Reflection;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Configuration;
using AzurPilot.Windows.MuMu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки MuMu-состава построенного host-а: реальные сервисы приходят из DI, а публичная поверхность
/// приложения не расширяется командной строкой, REPL, doctor-командой или новыми boundaries.
/// </summary>
/// <remarks>
/// Проверки выполняются на реально построенном host-е: они доказывают, что MuMu-capability подключена как
/// сервисы composition root, а не создаётся вручную в точке входа или в диагностике.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MuMuHostCompositionTests
{
    /// <summary>Ожидаемое множество публичных типов приложения.</summary>
    /// <remarks>
    /// Composition root остаётся единственной точкой сборки host-а, а presentation-поверхность — только
    /// диагностические данные и точка входа. Появление здесь CLI/REPL/doctor-типа или новой boundary
    /// означает, что состав приложения изменился, и проверка обязана это заметить.
    /// </remarks>
    private static readonly string[] ExpectedPublicTypes =
    [
        "AzurPilotApplicationDiagnostics",
        "AzurPilotConfigurationDiagnostics",
        "AzurPilotDiagnosticReport",
        "AzurPilotDiagnosticService",
        "AzurPilotExitCode",
        "AzurPilotHost",
        "AzurPilotMuMuDiagnostics",
        "AzurPilotNativeDiagnostics",
        "AzurPilotOperation",
        "AzurPilotStartupResult",
    ];

    [Fact(DisplayName = "MuMu services резолвятся из DI существующего host-а как singleton-ы")]
    public void MuMuServicesAreResolvedFromContainer()
    {
        using TemporaryConfigurationDirectory directory = new();
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);
        using IHost host = builder.Build();
        IServiceProvider services = host.Services;

        // Production host-side поверхность — реализация платформенной boundary, а не код App.
        IMuMuHost muMuHost = services.GetRequiredService<IMuMuHost>();
        _ = Assert.IsType<MuMuWindowsHost>(muMuHost);
        Assert.Equal(typeof(MuMuWindowsHost).FullName, muMuHost.GetType().FullName);

        // Узкие внешние границы адаптеров тоже приходят из DI и остаются в Windows boundary.
        _ = Assert.IsType<WindowsMuMuInstallationRegistrySource>(
            services.GetRequiredService<IMuMuInstallationRegistrySource>());
        _ = Assert.IsType<WindowsMuMuInstallMetadataSource>(
            services.GetRequiredService<IMuMuInstallMetadataSource>());
        _ = Assert.IsType<WindowsMuMuFileSystemProbe>(services.GetRequiredService<IMuMuFileSystemProbe>());
        _ = Assert.IsType<MuMuProcessRunner>(services.GetRequiredService<IMuMuProcessRunner>());

        // Orchestration и её обязательные зависимости: singleton-ы одного host-а. Второй экземпляр
        // координации mutation нарушил бы process-local гарантию, а второй источник времени — контракт
        // deadline.
        MuMuLifecycleService lifecycle = services.GetRequiredService<MuMuLifecycleService>();
        Assert.Same(lifecycle, services.GetRequiredService<MuMuLifecycleService>());
        Assert.Same(
            services.GetRequiredService<MuMuInstanceMutationGate>(),
            services.GetRequiredService<MuMuInstanceMutationGate>());
        Assert.Same(TimeProvider.System, services.GetRequiredService<TimeProvider>());
        Assert.Same(MuMuLifecycleTimings.Default, services.GetRequiredService<MuMuLifecycleTimings>());
        Assert.Same(muMuHost, services.GetRequiredService<IMuMuHost>());

        // Диагностическая операция резолвится из DI и получает ту же host-side поверхность.
        Assert.NotNull(services.GetRequiredService<AzurPilotDiagnosticService>());
    }

    [Fact(DisplayName = "Публичная поверхность App не расширена CLI/REPL/doctor и новыми boundaries")]
    public void PublicSurfaceStaysBounded()
    {
        Assembly application = typeof(AzurPilotHost).Assembly;
        Type[] exported = application.GetExportedTypes();

        Assert.Equal(
            ExpectedPublicTypes.OrderBy(name => name, StringComparer.Ordinal),
            exported.Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal));

        // Все публичные типы принадлежат пространству имён приложения: новых boundaries в App не появилось.
        Assert.All(exported, type => Assert.Equal("AzurPilot.App", type.Namespace));
    }
}
