using System.Reflection;
using AzurPilot.App;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки Android-состава построенного host-а: реальные сервисы приходят из DI, а общая граница запуска
/// процесса остаётся одной реализацией с проекцией отказов своего владельца.
/// </summary>
/// <remarks>
/// Проверки выполняются на реально построенном host-е: они доказывают, что Android-capability подключена
/// как сервисы composition root, а не создаётся вручную в точке входа или в диагностике. Ни установленная
/// MuMu, ни установленный ADB для этих проверок не требуются: ни одна Android-операция при построении
/// host-а и разрешении сервисов не выполняется.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AndroidHostCompositionTests
{
    [Fact(DisplayName = "Android services резолвятся из DI существующего host-а как singleton-ы")]
    public void AndroidServicesAreResolvedFromContainer()
    {
        using IHost host = BuildHost();
        IServiceProvider services = host.Services;

        // Production host-side поверхность — реализация платформенной boundary, а не код App.
        IAndroidHost androidHost = services.GetRequiredService<IAndroidHost>();
        _ = Assert.IsType<AndroidWindowsHost>(androidHost);
        Assert.Same(androidHost, services.GetRequiredService<IAndroidHost>());

        // Граница запуска процесса остаётся одной реализацией, но у Android-возможности она подключена со
        // своей проекцией отказов: смысл отказов принадлежит владельцу возможности.
        IWindowsProcessRunner androidRunner = Assert.Single(
            services.GetKeyedServices<IWindowsProcessRunner>(KeyedService.AnyKey));
        _ = Assert.IsType<WindowsProcessRunner>(androidRunner);
        Assert.Same(
            androidRunner,
            Assert.Single(services.GetKeyedServices<IWindowsProcessRunner>(KeyedService.AnyKey)));

        // MuMu-регистрация не подменена: общая граница и MuMu-проекция остались на месте, а Android-граница
        // — отдельный экземпляр той же реализации.
        IWindowsProcessRunner muMuRunner = services.GetRequiredService<IWindowsProcessRunner>();
        _ = Assert.IsType<WindowsProcessRunner>(muMuRunner);
        Assert.NotSame(muMuRunner, androidRunner);

        AdbProcessFailureProjection adbProjection = services.GetRequiredService<AdbProcessFailureProjection>();
        Assert.Same(adbProjection, services.GetRequiredService<AdbProcessFailureProjection>());
        _ = Assert.IsType<MuMuProcessFailureProjection>(services.GetRequiredService<IProcessFailureProjection>());

        // Проекции действительно разные: одна и та же ошибка запуска процесса сообщается кодом своей
        // возможности, поэтому подмена одной проекции другой не осталась незамеченной.
        Assert.Equal(
            ApplicationFailure.AndroidAdbUnavailable,
            adbProjection.StartFailed("adb-sentinel", exception: null).Code);
        Assert.NotEqual(
            adbProjection.StartFailed("adb-sentinel", exception: null).Code,
            services.GetRequiredService<IProcessFailureProjection>()
                .StartFailed("control-sentinel", exception: null)
                .Code);

        // Orchestration и её обязательные зависимости: singleton-ы одного host-а. Второй экземпляр
        // координации mutation нарушил бы process-local гарантию, а второй источник времени — контракт
        // deadline.
        AndroidReadinessService readiness = services.GetRequiredService<AndroidReadinessService>();
        Assert.Same(readiness, services.GetRequiredService<AndroidReadinessService>());
        AzurLaneGameStateService gameState = services.GetRequiredService<AzurLaneGameStateService>();
        Assert.Same(gameState, services.GetRequiredService<AzurLaneGameStateService>());
        Assert.Same(
            services.GetRequiredService<AzurLaneGameLifecycleService>(),
            services.GetRequiredService<AzurLaneGameLifecycleService>());
        Assert.Same(
            services.GetRequiredService<AndroidGameMutationGate>(),
            services.GetRequiredService<AndroidGameMutationGate>());
        Assert.Same(TimeProvider.System, services.GetRequiredService<TimeProvider>());
        Assert.Same(AndroidLifecycleTimings.Default, services.GetRequiredService<AndroidLifecycleTimings>());

        // Диагностическая операция резолвится из DI и получает ту же host-side поверхность.
        AzurPilotDiagnosticService diagnostics = services.GetRequiredService<AzurPilotDiagnosticService>();
        Assert.Same(diagnostics, services.GetRequiredService<AzurPilotDiagnosticService>());
    }

    [Fact(DisplayName = "Секции Android и Azur Lane публичны, а read-only проба остаётся внутренней")]
    public void SectionSurfaceIsPublicAndProbeStaysInternal()
    {
        Assembly application = typeof(AzurPilotHost).Assembly;
        Type[] exported = application.GetExportedTypes();

        // Секции snapshot — часть публичной диагностической поверхности приложения.
        Assert.Contains(typeof(AzurPilotAndroidDiagnostics), exported);
        Assert.Contains(typeof(AzurPilotAzurLaneDiagnostics), exported);

        // Read-only проба и разрешённая цель — деталь реализации: они не расширяют публичную поверхность и
        // не становятся вторым способом получить состояние Android.
        Assert.DoesNotContain(exported, type => type.Name == "AzurPilotAndroidProbe");
        Assert.DoesNotContain(exported, type => type.Name == "AzurPilotAndroidTarget");

        // Новых boundaries в App не появилось: все публичные типы принадлежат пространству имён приложения.
        Assert.All(exported, type => Assert.Equal("AzurPilot.App", type.Namespace));
    }

    private static IHost BuildHost()
    {
        using TemporaryConfigurationDirectory directory = new();
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration =
            AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);
        Assert.True(configuration.IsSuccess, configuration.ToString());

        return AzurPilotHost.CreateBuilder(configuration).Build();
    }
}
