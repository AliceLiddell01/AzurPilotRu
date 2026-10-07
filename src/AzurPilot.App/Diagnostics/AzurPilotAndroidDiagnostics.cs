using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Bounded Android-секция диагностического snapshot: bundled ADB, точный endpoint и готовность Android.
/// </summary>
/// <remarks>
/// <para>
/// Секция отвечает на вопросы диагностики и не превращается в дамп: доступен ли bundled ADB, какой
/// endpoint разрешён для выбранного экземпляра, каково наблюдённое состояние ADB transport, доступна ли
/// shell, завершена ли загрузка Android и какая версия Android отвечает. Полного списка устройств,
/// полного списка процессов, полного <c>dumpsys</c>, полного stdout/stderr команд ADB и machine-specific
/// путей в секции нет.
/// </para>
/// <para>
/// Секция описывает только чтение. Startup не подключает и не переподключает ADB: неготовый transport
/// сообщается фактом <see cref="TransportState"/>, а не исправляется. Ни одна mutation — запуск или
/// остановка игры и эмулятора — диагностикой не запрашивается.
/// </para>
/// <para>
/// <see cref="Stage"/> сообщает bounded machine-stable имя шага, на котором диагностика остановилась:
/// так отказ шага не выглядит отсутствием данных. Значения после остановившего шага остаются
/// <see langword="null"/> — «не наблюдалось», а не «доказано отсутствующим».
/// </para>
/// <para>
/// Текстовые значения секции приводятся к bounded однострочной форме владельцем ограничения
/// <see cref="BoundedDiagnosticText"/>: evidence обнаружения ADB, evidence наблюдения, версия Android и
/// текст endpoint-а приходят извне, поэтому переносы строк и произвольная длина до секции не доходят.
/// </para>
/// </remarks>
/// <param name="IsAdbAvailable">Признак того, что bundled ADB установки обнаружен.</param>
/// <param name="AdbEvidence">Bounded evidence обнаружения bundled ADB.</param>
/// <param name="Endpoint">
/// Точный ADB endpoint выбранного экземпляра в форме <c>host:port</c>; <see langword="null"/>, если
/// endpoint не разрешён.
/// </param>/// <param name="TransportState">
/// Наблюдённое состояние ADB transport; <see langword="null"/>, если наблюдение не выполнялось.
/// </param>
/// <param name="IsShellAvailable">
/// Признак доступности shell на точном endpoint-е; <see langword="null"/>, если готовность не наблюдалась.
/// </param>
/// <param name="BootCompleted">
/// Наблюдённое значение <c>sys.boot_completed</c>; <see langword="null"/>, если готовность не наблюдалась.
/// </param>
/// <param name="AndroidRelease">Bounded версия Android, сообщённая устройством.</param>
/// <param name="SdkLevel">Наблюдённый уровень API Android.</param>
/// <param name="Stage">Bounded machine-stable имя шага, на котором диагностика остановилась.</param>
/// <param name="Evidence">Bounded evidence read-only наблюдения готовности Android.</param>
/// <param name="Failure">
/// Application-level отказ Android-диагностики; <see langword="null"/>, если отказов не было.
/// </param>
public sealed record AzurPilotAndroidDiagnostics(
    bool IsAdbAvailable,
    string? AdbEvidence,
    string? Endpoint,
    AndroidTransportState? TransportState,
    bool? IsShellAvailable,
    int? BootCompleted,
    string? AndroidRelease,
    int? SdkLevel,
    string Stage,
    string? Evidence,
    ApplicationFailure? Failure)
{
    /// <summary>Собирает bounded Android-секцию из read-only пробы.</summary>
    /// <remarks>
    /// Секция не наблюдает ничего сама: все факты уже собраны пробой, поэтому повторных команд ADB при
    /// построении секции не выполняется. Отказ пробы становится <see cref="Failure"/> секции, а не
    /// причиной отказа запуска.
    /// </remarks>
    /// <param name="probe">Read-only результат Android-диагностики одного запуска.</param>
    /// <returns>Секция с bounded фактами либо с application-level отказом шага.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> равен <see langword="null"/>.</exception>
    internal static AzurPilotAndroidDiagnostics Capture(AzurPilotAndroidProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        AndroidReadinessFacts? readiness = probe.Readiness;
        AndroidBootObservation? boot = readiness?.Boot;

        return new AzurPilotAndroidDiagnostics(
            IsAdbAvailable: probe.IsAdbAvailable,
            AdbEvidence: probe.AdbEvidence,
            Endpoint: probe.Endpoint is AndroidEndpoint endpoint
                ? BoundedDiagnosticText.Bounded(endpoint.ToString())
                : null,
            TransportState: readiness?.Transport.State,
            IsShellAvailable: boot?.ShellAvailable,
            BootCompleted: boot?.BootCompleted,
            AndroidRelease: boot?.AndroidRelease is string release ? BoundedDiagnosticText.Bounded(release) : null,
            SdkLevel: boot?.SdkLevel,
            Stage: probe.Stage,
            Evidence: readiness is null ? null : BoundedDiagnosticText.Bounded(readiness.Evidence),
            Failure: probe.Failure);
    }
}
