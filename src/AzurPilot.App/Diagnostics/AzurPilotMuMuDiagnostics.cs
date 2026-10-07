using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Bounded MuMu-секция диагностического snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Секция отвечает на вопросы диагностики и не превращается в дамп: обнаружена ли установка, какая версия
/// обнаружена, поддерживается ли форма control surface, какой экземпляр разрешён конфигурацией, какой
/// экземпляр выбран и каково его доказанное host-side состояние. Полного дампа реестра, списка процессов,
/// полного stdout/stderr control utility, полного документа конфигурации и machine-specific путей
/// установки в секции нет.
/// </para>
/// <para>
/// Отказ MuMu — результат диагностики, а не исключение: он попадает в <see cref="Failure"/> как данные.
/// Startup не превращает его в ненулевой код выхода и не «исправляет» его: диагностика MuMu только
/// читает (обнаружение установки, перечисление экземпляров, наблюдение состояния) и никогда не запускает
/// и не останавливает экземпляр.
/// </para>
/// <para>
/// Текстовые значения секции приводятся к bounded однострочной форме владельцем ограничения
/// <see cref="BoundedDiagnosticText"/>: отображаемое имя и evidence приходят извне, поэтому переносы строк и
/// произвольная длина до секции не доходят.
/// </para>
/// </remarks>
/// <param name="IsInstallationDiscovered">Признак того, что установка MuMuPlayer обнаружена.</param>
/// <param name="Version">
/// Версия обнаруженной установки; <see langword="null"/>, если установка не обнаружена.
/// </param>
/// <param name="ControlSurfaceStatus">
/// Bounded статус control surface из <see cref="AzurPilotMuMuControlSurfaceStatus"/>.
/// </param>
/// <param name="ConfiguredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
/// <param name="SelectedInstanceId">
/// Каноническая identity выбранного экземпляра; <see langword="null"/>, если выбор не разрешён.
/// </param>
/// <param name="SelectedInstanceDisplayName">
/// Отображаемое имя выбранного экземпляра: сведения для оператора, а не identity.
/// </param>
/// <param name="LifecycleState">
/// Доказанное host-side состояние выбранного экземпляра; <see langword="null"/>, если состояние не
/// наблюдалось (экземпляр не выбран или наблюдение завершилось отказом).
/// </param>
/// <param name="Evidence">Bounded evidence последнего наблюдения состояния.</param>
/// <param name="Failure">Application-level отказ MuMu-диагностики; <see langword="null"/>, если отказов нет.</param>
public sealed record AzurPilotMuMuDiagnostics(
    bool IsInstallationDiscovered,
    string? Version,
    string ControlSurfaceStatus,
    string ConfiguredInstance,
    string? SelectedInstanceId,
    string? SelectedInstanceDisplayName,
    MuMuLifecycleState? LifecycleState,
    string? Evidence,
    ApplicationFailure? Failure)
{
    /// <summary>Собирает MuMu-секцию диагностики чтением host-side поверхности MuMu.</summary>
    /// <remarks>
    /// <para>
    /// Операция только читает: обнаружение установки, разрешение выбранного экземпляра (перечисление и
    /// семантика выбора принадлежат orchestration <see cref="MuMuLifecycleService"/>, а не этой секции) и
    /// наблюдение host-side состояния. Mutation не запрашивается ни на одном пути, поэтому запуск
    /// приложения не может незаметно запустить или остановить эмулятор.
    /// </para>
    /// <para>
    /// Ожидаемый отказ любого шага становится данными секции. Непредвиденное исключение host-а
    /// проецируется в application-отказ той же production-проекцией, что и остальные ошибки
    /// платформенной границы, поэтому нарушение контракта не превращается в отказ startup.
    /// </para>
    /// <para>
    /// Статус control surface описывает последний доказанный шаг и не противоречит отказу: когда отказ
    /// наблюдения сам сообщает о нераспознанной форме ответа, статус выводится из кода этого отказа, а не
    /// остаётся «поддержана».
    /// </para>
    /// </remarks>
    /// <param name="host">Host-side поверхность MuMu из DI.</param>
    /// <param name="lifecycle">Orchestration MuMu из DI: владелец семантики выбора экземпляра.</param>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <param name="mapFailure">Проекция непредвиденного исключения границы в application-отказ.</param>
    /// <returns>Секция с фактическими MuMu-evidence либо с application-level отказом.</returns>
    internal static AzurPilotMuMuDiagnostics Capture(
        IMuMuHost host,
        MuMuLifecycleService lifecycle,
        string configuredInstance,
        Func<Exception, ApplicationFailure> mapFailure)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(configuredInstance);
        ArgumentNullException.ThrowIfNull(mapFailure);

        try
        {
            ApplicationResult<MuMuInstallation> discovery = host.DiscoverInstallation();
            if (discovery.IsFailure)
            {
                ApplicationFailure discoveryFailure = discovery.FailureInfo!;
                return Failed(
                    configuredInstance,
                    version: null,
                    AzurPilotMuMuControlSurfaceStatus.ForDiscoveryFailure(discoveryFailure.Code),
                    discoveryFailure);
            }

            MuMuInstallation installation = discovery.Value!;
            MuMuInstanceResolution resolution = lifecycle.Resolve(installation, SelectionFor(configuredInstance));
            if (resolution.IsFailed)
            {
                ApplicationFailure selectionFailure = resolution.Failure!;
                return Failed(
                    configuredInstance,
                    installation.Version,
                    AzurPilotMuMuControlSurfaceStatus.ForSelectionFailure(selectionFailure.Code),
                    selectionFailure);
            }

            MuMuInstance instance = resolution.Instance!;
            ApplicationResult<MuMuInstanceState> observation =
                host.ObserveInstanceState(installation, instance.Id);

            // Статус не противоречит отказу наблюдения: код, которым host сообщает о нераспознанной форме
            // ответа control surface, не может стоять в одном snapshot рядом со статусом «поддержана».
            string controlSurfaceStatus = observation.IsSuccess
                ? AzurPilotMuMuControlSurfaceStatus.Supported
                : AzurPilotMuMuControlSurfaceStatus.ForObservationFailure(observation.FailureInfo!.Code);

            return new AzurPilotMuMuDiagnostics(
                IsInstallationDiscovered: true,
                Version: BoundedDiagnosticText.Bounded(installation.Version),
                ControlSurfaceStatus: controlSurfaceStatus,
                ConfiguredInstance: configuredInstance,
                SelectedInstanceId: instance.Id.ToString(),
                SelectedInstanceDisplayName: BoundedDiagnosticText.Bounded(instance.DisplayName),
                LifecycleState: observation.IsSuccess ? observation.Value!.State : null,
                Evidence: observation.IsSuccess ? BoundedDiagnosticText.Bounded(observation.Value!.Evidence) : null,
                Failure: observation.IsFailure ? observation.FailureInfo : null);
        }
        catch (Exception exception)
        {
            // Непредвиденное нарушение контракта host-ом остаётся диагностическим фактом: исход запуска
            // решает native boundary и конфигурация, а не MuMu-диагностика.
            return Failed(
                configuredInstance,
                version: null,
                AzurPilotMuMuControlSurfaceStatus.Unknown,
                mapFailure(exception));
        }
    }

    /// <summary>Создаёт секцию для неуспешного шага MuMu-диагностики.</summary>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <param name="version">Версия обнаруженной установки, если она уже известна.</param>
    /// <param name="controlSurfaceStatus">Bounded статус control surface.</param>
    /// <param name="failure">Отказ шага MuMu-диагностики.</param>
    /// <returns>Секция, описывающая, на каком шаге диагностика остановилась.</returns>
    private static AzurPilotMuMuDiagnostics Failed(
        string configuredInstance,
        string? version,
        string controlSurfaceStatus,
        ApplicationFailure failure)
        => new(
            IsInstallationDiscovered: version is not null,
            Version: version is null ? null : BoundedDiagnosticText.Bounded(version),
            ControlSurfaceStatus: controlSurfaceStatus,
            ConfiguredInstance: configuredInstance,
            SelectedInstanceId: null,
            SelectedInstanceDisplayName: null,
            LifecycleState: null,
            Evidence: null,
            Failure: failure);

    /// <summary>Переводит значение <c>mumu.instance</c> в семантику выбора экземпляра.</summary>
    /// <remarks>
    /// Синтаксис значения читается у его владельца <see cref="MuMuInstanceValue"/>: здесь не повторяются
    /// ни литерал автоматического выбора, ни префикс провайдерской формы, ни грамматика номера — её уже
    /// подтвердила строгая загрузка конфигурации.
    /// </remarks>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <returns>Семантика автоматического либо явного выбора экземпляра.</returns>
    /// <exception cref="InvalidOperationException">
    /// Значение не соответствует синтаксису схемы: такое значение не может прийти из успешно загруженной
    /// конфигурации, поэтому это ошибка программирования, а не ожидаемый отказ MuMu.
    /// </exception>
    private static MuMuInstanceSelection SelectionFor(string configuredInstance)
    {
        if (MuMuInstanceValue.IsAuto(configuredInstance))
        {
            return MuMuInstanceSelection.Auto();
        }

        if (configuredInstance.StartsWith(MuMuInstanceValue.ProviderPrefix, StringComparison.Ordinal))
        {
            return MuMuInstanceSelection.Explicit(
                MuMuInstanceId.FromIndex(configuredInstance[MuMuInstanceValue.ProviderPrefix.Length..]));
        }

        throw new InvalidOperationException(
            "Значение mumu.instance не соответствует синтаксису схемы: успешно загруженная конфигурация "
            + "такого значения не содержит.");
    }
}

/// <summary>
/// Bounded статус control surface обнаруженной установки MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Статус — machine-readable токен диагностики, а не второй каталог application-кодов отказа: причина
/// всегда сообщается application-отказом в <see cref="AzurPilotMuMuDiagnostics.Failure"/>, а статус
/// отвечает только на вопрос «работает ли форма control surface».
/// </para>
/// <para>
/// Статус не выводится из перечня кодов: он выводится из доказанного смысла отказа. Обнаружение установки
/// сообщает три кода с доказанным смыслом: <see cref="ApplicationFailure.MuMuInstallationNotFound"/>
/// (установки нет → <see cref="Absent"/>), <see cref="ApplicationFailure.MuMuInstallationAmbiguous"/>
/// (установок несколько, поэтому нельзя утверждать ни отсутствие, ни поддержку → <see cref="Unknown"/>)
/// и <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/> (форма не распознана →
/// <see cref="Unsupported"/>); разрешение экземпляра сообщает свои коды.
/// </para>
/// <para>
/// Перечень называет доказанные случаи, но не закрывает набор: любой иной код, включая добавленный позже,
/// не истолковывается и даёт <see cref="Unknown"/>, а не догадку. Ветка по умолчанию здесь — само правило,
/// поэтому новый код не может молча получить смысл, которого у него нет, и его добавление не требует
/// правки этого документа.
/// </para>
/// </remarks>
internal static class AzurPilotMuMuControlSurfaceStatus
{
    /// <summary>Control surface обнаруженной установки ответила в распознанной форме.</summary>
    internal const string Supported = "supported";

    /// <summary>Форма control surface установки не поддерживается.</summary>
    internal const string Unsupported = "unsupported";

    /// <summary>Установка MuMu не обнаружена, поэтому проверять форму control surface нечего.</summary>
    internal const string Absent = "absent";

    /// <summary>Поддержку формы control surface доказать не удалось.</summary>
    internal const string Unknown = "unknown";

    /// <summary>Определяет статус по отказу обнаружения установки.</summary>
    /// <remarks>
    /// <para>
    /// Явная ветка есть только у кодов с доказанным смыслом: установки нет
    /// (<see cref="ApplicationFailure.MuMuInstallationNotFound"/> → <see cref="Absent"/>), установок
    /// несколько, поэтому нельзя утверждать ни отсутствие, ни поддержку
    /// (<see cref="ApplicationFailure.MuMuInstallationAmbiguous"/> → <see cref="Unknown"/>), форма
    /// control surface не распознана (<see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/> →
    /// <see cref="Unsupported"/>).
    /// </para>
    /// <para>
    /// Ветка по умолчанию — не запасной вариант на случай забытого кода, а правило: недоказанный код не
    /// истолковывается и даёт <see cref="Unknown"/>. Поэтому явная ветка неоднозначности нужна не ради
    /// поведения (оно совпадает с веткой по умолчанию), а чтобы решение «статус здесь не выводится» было
    /// видно в коде, а не выводилось читателем из порядка ветвей.
    /// </para>
    /// </remarks>
    /// <param name="failureCode">Стабильный код отказа обнаружения.</param>
    /// <returns>Bounded статус control surface.</returns>
    internal static string ForDiscoveryFailure(string failureCode) => failureCode switch
    {
        ApplicationFailure.MuMuControlSurfaceUnsupported => Unsupported,
        ApplicationFailure.MuMuInstallationNotFound => Absent,
        ApplicationFailure.MuMuInstallationAmbiguous => Unknown,
        _ => Unknown,
    };

    /// <summary>Определяет статус по отказу разрешения выбранного экземпляра.</summary>
    /// <remarks>
    /// Отказы выбора экземпляра означают, что control surface ответила и её форма распознана: не разрешён
    /// выбор, а не поддержка установки.
    /// </remarks>
    /// <param name="failureCode">Стабильный код отказа разрешения выбора.</param>
    /// <returns>Bounded статус control surface.</returns>
    internal static string ForSelectionFailure(string failureCode) => failureCode switch
    {
        ApplicationFailure.MuMuInstanceNotFound => Supported,
        ApplicationFailure.MuMuInstanceAmbiguous => Supported,
        ApplicationFailure.MuMuControlSurfaceUnsupported => Unsupported,
        _ => Unknown,
    };

    /// <summary>Определяет статус по отказу наблюдения состояния выбранного экземпляра.</summary>
    /// <remarks>
    /// <para>
    /// Отказ наблюдения не оставляет статус <see cref="Supported"/> без доказательства: он выводится из
    /// кода отказа, поэтому один snapshot не может одновременно утверждать «форма control surface
    /// поддержана» и «форма control surface не распознана». Код
    /// <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/> покрывает и нераспознанный ответ
    /// на сведения об экземпляре, поэтому он даёт <see cref="Unsupported"/>.
    /// </para>
    /// <para>
    /// Отказ, доказывающий, что surface ответила (запрошенного экземпляра нет), сохраняет
    /// <see cref="Supported"/>: не разрешён выбранный экземпляр, а не поддержка формы. Любой иной код
    /// даёт <see cref="Unknown"/>: поддержку формы он не доказывает и не опровергает.
    /// </para>
    /// </remarks>
    /// <param name="failureCode">Стабильный код отказа наблюдения состояния.</param>
    /// <returns>Bounded статус control surface.</returns>
    internal static string ForObservationFailure(string failureCode) => failureCode switch
    {
        ApplicationFailure.MuMuInstanceNotFound => Supported,
        ApplicationFailure.MuMuControlSurfaceUnsupported => Unsupported,
        _ => Unknown,
    };
}
