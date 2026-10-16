using System.Reflection;
using System.Xml.Linq;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Configuration;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки MuMu-состава построенного host-а: реальные сервисы приходят из DI, а публичная поверхность
/// приложение использует единственную composition root и не вводит новых managed boundaries.
/// </summary>
/// <remarks>
/// Проверки выполняются на реально построенном host-е: они доказывают, что MuMu-capability подключена как
/// сервисы composition root, а не создаётся вручную в точке входа или в диагностике.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MuMuHostCompositionTests
{
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

        // Граница запуска процесса общая для Windows-возможностей, а смысл её отказов принадлежит MuMu:
        // из DI приходят и общая граница, и MuMu-проекция, и обе остаются в Windows boundary.
        IWindowsProcessRunner processRunner = services.GetRequiredService<IWindowsProcessRunner>();
        _ = Assert.IsType<WindowsProcessRunner>(processRunner);
        Assert.Same(processRunner, services.GetRequiredService<IWindowsProcessRunner>());
        _ = Assert.IsType<MuMuProcessFailureProjection>(
            services.GetRequiredService<IProcessFailureProjection>());

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

    [Fact(DisplayName = "Managed-проекты и ссылки сборок соблюдают направление App → Windows → Core")]
    public void ManagedBoundariesAndDependencyDirectionArePreserved()
    {
        string sourceRoot = Path.Combine(PinnedVersions.RepositoryRoot, "src");
        string[] projects = Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories);
        Dictionary<string, string[]> allowed = new(StringComparer.Ordinal)
        {
            ["AzurPilot.App"] = ["AzurPilot.Windows", "AzurPilot.Core"],
            ["AzurPilot.Windows"] = ["AzurPilot.Core"],
            ["AzurPilot.Core"] = [],
        };
        Assert.Equal(allowed.Keys.Order(StringComparer.Ordinal),
            projects.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));

        foreach (string project in projects)
        {
            string owner = Path.GetFileNameWithoutExtension(project);
            foreach (XElement reference in XDocument.Load(project).Descendants("ProjectReference"))
            {
                string target = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value);
                Assert.Contains(target, allowed[owner]);
            }
        }

        foreach (Assembly assembly in new[] { typeof(AzurPilotHost).Assembly,
            typeof(MuMuWindowsHost).Assembly, typeof(MuMuLifecycleService).Assembly })
        {
            string owner = assembly.GetName().Name!;
            Assert.All(assembly.GetReferencedAssemblies().Where(reference => reference.Name!.StartsWith("AzurPilot.", StringComparison.Ordinal)),
                reference => Assert.Contains(reference.Name!, allowed[owner]));
        }
    }
}
