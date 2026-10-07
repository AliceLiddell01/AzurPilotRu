using System.Reflection;
using System.Runtime.InteropServices;
using AzurPilot.Core;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Runtime diagnostic operation приложения: собирает bounded snapshot приложения, конфигурации,
/// native boundary и MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Операция ничего не печатает и не решает, продолжать ли запуск: она возвращает данные, поэтому её
/// сможет переиспользовать будущее doctor-представление, не меняя состав диагностики.
/// </para>
/// <para>
/// Ошибка native boundary не выбрасывается наружу: она проецируется в application-level отказ
/// production-маппером, полученным из DI, и попадает в native-секцию snapshot как данные.
/// </para>
/// <para>
/// MuMu-секция собирается чтением host-side поверхности, полученной из DI, и её отказ тоже остаётся
/// данными: диагностика MuMu не выполняет mutation и не решает исход запуска.
/// </para>
/// </remarks>
public sealed class AzurPilotDiagnosticService
{
    private readonly AzurPilotConfigurationSnapshot _configuration;
    private readonly Func<Exception, ApplicationFailure> _mapFailure;
    private readonly Func<string, ApplicationFailure> _mapIncompatibility;
    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;

    /// <summary>Создаёт диагностическую операцию приложения.</summary>
    /// <param name="configuration">Загруженный snapshot конфигурации.</param>
    /// <param name="mapFailure">Проекция ошибок native boundary в application-level отказ.</param>
    /// <param name="mapIncompatibility">
    /// Проекция несовместимости native boundary, подтверждённой значением, в application-level отказ.
    /// </param>
    /// <param name="muMuHost">Host-side поверхность MuMu: production-реализация приходит из DI.</param>
    /// <param name="muMuLifecycle">
    /// Orchestration MuMu из DI: владелец семантики выбора экземпляра, который диагностика не повторяет.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Любой из аргументов равен <see langword="null"/>.
    /// </exception>
    public AzurPilotDiagnosticService(
        AzurPilotConfigurationSnapshot configuration,
        Func<Exception, ApplicationFailure> mapFailure,
        Func<string, ApplicationFailure> mapIncompatibility,
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(mapFailure);
        ArgumentNullException.ThrowIfNull(mapIncompatibility);
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);

        _configuration = configuration;
        _mapFailure = mapFailure;
        _mapIncompatibility = mapIncompatibility;
        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
    }

    /// <summary>Собирает bounded диагностический snapshot текущего запуска.</summary>
    /// <returns>Snapshot приложения, конфигурации, native boundary и MuMu.</returns>
    public AzurPilotDiagnosticReport Capture()
    {
        return new AzurPilotDiagnosticReport(
            CaptureApplication(),
            AzurPilotConfigurationDiagnostics.FromSnapshot(_configuration),
            CaptureNative(),
            CaptureMuMu());
    }

    /// <summary>Собирает сведения о сборке, runtime и процессе.</summary>
    /// <returns>Application-секция snapshot.</returns>
    private static AzurPilotApplicationDiagnostics CaptureApplication()
    {
        // Identity берётся у сборки application host, а не у entry assembly: диагностика должна
        // описывать приложение, а не процесс, который его запустил.
        Assembly assembly = typeof(AzurPilotDiagnosticService).Assembly;
        AssemblyName assemblyName = assembly.GetName();

        return new AzurPilotApplicationDiagnostics(
            assemblyName.Name ?? string.Empty,
            assemblyName.Version?.ToString() ?? string.Empty,
            assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? string.Empty,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessId,
            Environment.Is64BitProcess);
    }

    /// <summary>Запрашивает сведения о native boundary и проверяет её контракт.</summary>
    /// <returns>Native-секция snapshot с фактическими evidence либо с application-level отказом.</returns>
    private AzurPilotNativeDiagnostics CaptureNative()
    {
        try
        {
            NativeBoundaryInfo info = AzurPilotNativeBridge.Query();
            return AzurPilotNativeDiagnostics.FromBoundary(
                info,
                NativeBoundaryContract.Canonical.Check(info),
                _mapIncompatibility);
        }
        catch (Exception exception)
        {
            // Недоступная или несовместимая native boundary — ожидаемый исход диагностики, а не сбой
            // host-а: отказ возвращается данными, а решение о продолжении запуска принимает startup.
            return AzurPilotNativeDiagnostics.FromFailure(_mapFailure(exception));
        }
    }

    /// <summary>Собирает MuMu-секцию snapshot чтением host-side поверхности MuMu.</summary>
    /// <remarks>
    /// Разрешённый конфигурацией экземпляр берётся из эффективной конфигурации snapshot: legacy-вход уже
    /// нормализован к текущей схеме, поэтому значение приходит от владельца defaults, а не из догадки.
    /// </remarks>
    /// <returns>MuMu-секция с фактическими evidence либо с application-level отказом.</returns>
    private AzurPilotMuMuDiagnostics CaptureMuMu()
        => AzurPilotMuMuDiagnostics.Capture(
            _muMuHost,
            _muMuLifecycle,
            _configuration.Configuration.MuMu.Instance,
            _mapFailure);
}
