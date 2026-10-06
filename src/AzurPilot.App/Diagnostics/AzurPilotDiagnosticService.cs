using System.Reflection;
using System.Runtime.InteropServices;
using AzurPilot.Core;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Runtime diagnostic operation приложения: собирает bounded snapshot приложения, конфигурации и
/// native boundary.
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
/// </remarks>
public sealed class AzurPilotDiagnosticService
{
    private readonly AzurPilotConfigurationSnapshot _configuration;
    private readonly Func<Exception, ApplicationFailure> _mapFailure;
    private readonly Func<string, ApplicationFailure> _mapIncompatibility;

    /// <summary>Создаёт диагностическую операцию приложения.</summary>
    /// <param name="configuration">Загруженный snapshot конфигурации.</param>
    /// <param name="mapFailure">Проекция ошибок native boundary в application-level отказ.</param>
    /// <param name="mapIncompatibility">
    /// Проекция несовместимости native boundary, подтверждённой значением, в application-level отказ.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Любой из аргументов равен <see langword="null"/>.
    /// </exception>
    public AzurPilotDiagnosticService(
        AzurPilotConfigurationSnapshot configuration,
        Func<Exception, ApplicationFailure> mapFailure,
        Func<string, ApplicationFailure> mapIncompatibility)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(mapFailure);
        ArgumentNullException.ThrowIfNull(mapIncompatibility);

        _configuration = configuration;
        _mapFailure = mapFailure;
        _mapIncompatibility = mapIncompatibility;
    }

    /// <summary>Собирает bounded диагностический snapshot текущего запуска.</summary>
    /// <returns>Snapshot приложения, конфигурации и native boundary.</returns>
    public AzurPilotDiagnosticReport Capture()
    {
        return new AzurPilotDiagnosticReport(
            CaptureApplication(),
            AzurPilotConfigurationDiagnostics.FromSnapshot(_configuration),
            CaptureNative());
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
}
