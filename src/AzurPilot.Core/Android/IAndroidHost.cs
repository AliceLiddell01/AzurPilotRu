using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Android;

/// <summary>
/// Host-side поверхность Android: ADB readiness и lifecycle игры Azur Lane на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// Контракт платформенно-независим: он не знает ни о Win32, ни о файловой системе, ни о запуске
/// процессов, ни о конкретных путях установки. Реализация принадлежит платформенной boundary
/// <c>AzurPilot.Windows</c>.
/// </para>
/// <para>
/// Ожидаемый отказ возвращается значением через <see cref="ApplicationResult{T}"/>; исключение означает
/// ошибку программирования вызывающей стороны или нарушение контракта реализацией. Ожидаемые
/// Android-отказы, возвращённые host-ом, orchestration пробрасывает без изменений: это уже точные коды,
/// и подменять их общим отказом нельзя.
/// </para>
/// <para>
/// Target-explicit адресация обязательна: каждый метод получает конкретный <see cref="AndroidEndpoint"/>,
/// и наблюдение относится ровно к этому endpoint-у и запрошенному пакету, а не к «какому-то устройству
/// в списке ADB». Endpoint не выводится из окружения и не подставляется по умолчанию.
/// </para>
/// <para>
/// Методы синхронные: они сообщают результат одного действия, а bounded polling, deadline и координацию
/// выполняет orchestration Core. Реализация не добавляет к запрошенному действию ни повторных попыток,
/// ни ожиданий.
/// </para>
/// <para>
/// Токен отмены несёт только mutation: команды чтения ограничены собственным дедлайном границы, а отмена
/// проверяется orchestration между шагами операции.
/// </para>
/// <para>
/// Код выхода команды ADB не является доказательством успеха: он возвращается как evidence, а решение о
/// достигнутом состоянии принимает orchestration по наблюдению.
/// </para>
/// </remarks>
public interface IAndroidHost
{
    /// <summary>Обнаруживает исполняемый файл ADB для обнаруженной установки MuMuPlayer.</summary>
    /// <remarks>
    /// Обнаруживается именно ADB, принадлежащий установке (bundled ADB), а не произвольный ADB из
    /// окружения: подстановка «похожего» executable запрещена, а его отсутствие — ожидаемый отказ
    /// <see cref="ApplicationFailure.AndroidAdbUnavailable"/>.
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <returns>Успешный результат с обнаруженным ADB либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidAdbExecutable> DiscoverAdbExecutable(MuMuInstallation installation);

    /// <summary>Разрешает точный ADB endpoint выбранного Android-экземпляра.</summary>
    /// <remarks>
    /// Endpoint берётся из сведений установки об экземпляре и относится ровно к запрошенной identity:
    /// значение не подставляется по умолчанию и не берётся у другого экземпляра.
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity Android-экземпляра, для которого запрашивается endpoint.</param>
    /// <returns>Успешный результат с точным endpoint-ом либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidEndpoint> ResolveEndpoint(MuMuInstallation installation, MuMuInstanceId instance);

    /// <summary>Наблюдает состояние ADB transport точного endpoint-а.</summary>
    /// <param name="endpoint">Точный endpoint, состояние которого запрашивается.</param>
    /// <returns>Успешное наблюдение transport либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidTransportObservation> QueryTransport(AndroidEndpoint endpoint);

    /// <summary>Выполняет однократное подключение к ADB transport точного endpoint-а.</summary>
    /// <remarks>
    /// Метод выполняет ровно одно подключение: ожидание готовности transport и повторные попытки
    /// принадлежат orchestration. Код выхода команды ADB сообщается как evidence, а не как доказательство
    /// готовности.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, к которому выполняется подключение.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Успешный результат с кодом выхода ADB либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidCommandOutcome> ConnectTransport(
        AndroidEndpoint endpoint,
        CancellationToken cancellationToken);

    /// <summary>Наблюдает готовность Android на точном endpoint-е.</summary>
    /// <remarks>
    /// Наблюдение сообщает факты устройства (<c>sys.boot_completed</c>, версия Android, уровень SDK), а
    /// не решение о готовности: решение принимает orchestration по своим условиям.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, готовность которого запрашивается.</param>
    /// <returns>Успешное наблюдение готовности либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidBootObservation> QueryBoot(AndroidEndpoint endpoint);

    /// <summary>Наблюдает присутствие пакета на точном endpoint-е.</summary>
    /// <param name="endpoint">Точный endpoint, у которого запрашивается присутствие пакета.</param>
    /// <param name="package">Идентификатор запрашиваемого пакета.</param>
    /// <returns>Успешное наблюдение присутствия пакета либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidPackagePresence> QueryPackage(AndroidEndpoint endpoint, AndroidPackageId package);

    /// <summary>Разрешает launcher-компонент пакета на точном endpoint-е.</summary>
    /// <remarks>
    /// Неоднозначность разрешается статусом <see cref="AndroidLauncherResolutionStatus.Ambiguous"/>, а не
    /// выбором первого попавшегося компонента: адресовать mutation недоказанному компоненту запрещено.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, у которого запрашивается launcher-компонент.</param>
    /// <param name="package">Идентификатор пакета, launcher которого разрешается.</param>
    /// <returns>Успешный результат разрешения либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidLauncherResolution> ResolveLauncher(AndroidEndpoint endpoint, AndroidPackageId package);

    /// <summary>Наблюдает процессы пакета на точном endpoint-е.</summary>
    /// <remarks>
    /// Наблюдение относится к запрошенному пакету: список процессов устройства целиком не возвращается и
    /// в диагностику не попадает.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, у которого запрашиваются процессы пакета.</param>
    /// <param name="package">Идентификатор пакета, процессы которого запрашиваются.</param>
    /// <returns>Успешное наблюдение процессов либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidProcessObservation> ObserveProcesses(AndroidEndpoint endpoint, AndroidPackageId package);

    /// <summary>Наблюдает компонент переднего плана точного endpoint-а.</summary>
    /// <remarks>
    /// Наблюдение отвечает про передний план устройства, а не про запущенный процесс: наличие процесса
    /// foreground-ом не является.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, foreground которого запрашивается.</param>
    /// <returns>Успешное наблюдение foreground либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidForegroundObservation> ObserveForeground(AndroidEndpoint endpoint);

    /// <summary>Выполняет однократную mutation игры над точным endpoint-ом и пакетом.</summary>
    /// <remarks>
    /// <para>
    /// Метод выполняет ровно одну mutation: повторные вызовы, ожидание postcondition и deadline
    /// принадлежат orchestration. Реализация обязана ограничить собственный вывод и не управлять другими
    /// пакетами и чужими процессами.
    /// </para>
    /// <para>
    /// Код выхода команды ADB не является доказательством успеха: реализация сообщает его как факт, а
    /// достигнутое состояние доказывает orchestration наблюдением.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, над которым выполняется mutation.</param>
    /// <param name="package">Идентификатор пакета, над которым выполняется mutation.</param>
    /// <param name="mutation">Запрошенная mutation.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Успешный результат с кодом выхода ADB либо ожидаемый отказ.</returns>
    ApplicationResult<AndroidCommandOutcome> RequestGameMutation(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AndroidGameMutation mutation,
        CancellationToken cancellationToken);
}
