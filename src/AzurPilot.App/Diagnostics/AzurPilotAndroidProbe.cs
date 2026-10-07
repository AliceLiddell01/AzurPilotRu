using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Разрешённая цель Android-диагностики: выбранный Android-экземпляр, bundled ADB и точный endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Цель описывает только identity и адрес: абсолютный путь обнаруженного ADB в неё не входит, потому что
/// диагностический snapshot не содержит machine-specific путей. Вместо пути сохраняется bounded evidence
/// обнаружения.
/// </para>
/// <para>
/// Endpoint берётся у host-side поверхности (<c>IAndroidHost.ResolveEndpoint</c>) и не вычисляется здесь
/// по номеру экземпляра и не подставляется значением по умолчанию.
/// </para>
/// </remarks>
/// <param name="Instance">Выбранный Android-экземпляр MuMu.</param>
/// <param name="AdbEvidence">Bounded evidence обнаружения bundled ADB.</param>
/// <param name="Endpoint">Точный ADB endpoint выбранного экземпляра.</param>
internal sealed record AzurPilotAndroidTarget(
    MuMuInstance Instance,
    string AdbEvidence,
    AndroidEndpoint Endpoint);

/// <summary>
/// Read-only результат Android-диагностики одного запуска: цель, готовность Android и состояние игры.
/// </summary>
/// <remarks>
/// <para>
/// Проба только читает. Ни одна mutation здесь не запрашивается: <c>ConnectTransport</c>,
/// <c>RequestGameMutation</c> и любой lifecycle-путь не вызываются, поэтому startup не подключает ADB,
/// не переподключает его, не запускает и не останавливает ни игру, ни эмулятор. Неготовый transport
/// сообщается фактом своего состояния, а не исправляется.
/// </para>
/// <para>
/// Шаги идут по порядку и останавливаются на первом отказе: <see cref="Stage"/> сообщает, на каком шаге
/// диагностика остановилась, а поля после него остаются недоказанными. Отказ шага — данные snapshot, а не
/// причина отказа запуска.
/// </para>
/// <para>
/// Наблюдение состояния игры выполняется только тогда, когда transport доказанно готов к командам:
/// у неготового transport запрос пакета, процессов и переднего плана не описывал бы игру. Поэтому
/// <see cref="Game"/> равен <see langword="null"/> и тогда, когда transport просто ещё не готов, — это
/// «не наблюдалось», а не «игра не установлена».
/// </para>
/// </remarks>
/// <param name="Stage">Bounded machine-stable имя шага, на котором диагностика остановилась.</param>
/// <param name="Failure">Отказ остановившего шага либо <see langword="null"/>, если отказов не было.</param>
/// <param name="IsAdbAvailable">
/// Признак того, что bundled ADB установки обнаружен: <see langword="true"/> — обнаружен,
/// <see langword="null"/> — обнаружение не выполнялось или не дало результата. Доказанного отсутствия ADB
/// здесь не сообщается: недостижимый шаг обнаружения не является наблюдением «ADB нет».
/// </param>
/// <param name="AdbEvidence">Bounded evidence обнаружения bundled ADB.</param>
/// <param name="Endpoint">Точный endpoint выбранного экземпляра, если он разрешён.</param>
/// <param name="Readiness">Read-only наблюдение готовности Android, если оно выполнено.</param>
/// <param name="Game">Наблюдение состояния игры, если transport был готов к командам.</param>
internal sealed record AzurPilotAndroidProbe(
    string Stage,
    ApplicationFailure? Failure,
    bool? IsAdbAvailable,
    string? AdbEvidence,
    AndroidEndpoint? Endpoint,
    AndroidReadinessFacts? Readiness,
    AzurLaneGameObservation? Game)
{
    /// <summary>Шаг: обнаружение установки MuMuPlayer.</summary>
    internal const string InstallationStage = "installation";

    /// <summary>Шаг: разрешение выбранного Android-экземпляра.</summary>
    internal const string InstanceStage = "instance";

    /// <summary>Шаг: обнаружение bundled ADB установки.</summary>
    internal const string AdbStage = "adb";

    /// <summary>Шаг: разрешение точного ADB endpoint выбранного экземпляра.</summary>
    internal const string EndpointStage = "endpoint";

    /// <summary>Шаг: read-only наблюдение готовности Android на точном endpoint-е.</summary>
    internal const string ReadinessStage = "readiness";

    /// <summary>Шаг: read-only наблюдение состояния игры Azur Lane.</summary>
    internal const string GameStage = "game";

    /// <summary>Шаг: все шаги прошли, наблюдения доказаны.</summary>
    internal const string CompleteStage = "complete";

    /// <summary>Собирает read-only пробу Android и состояния игры Azur Lane.</summary>
    /// <remarks>
    /// <para>
    /// Разрешение цели повторяет read-only путь MuMu-диагностики: обнаружение установки, разрешение
    /// выбранного экземпляра через orchestration MuMu (семантика выбора не дублируется здесь) и затем
    /// обнаружение bundled ADB. Мутации не запрашиваются ни на одном шаге.
    /// </para>
    /// <para>
    /// Обнаружение bundled ADB выполняется до первой команды ADB: это обязательное условие host-side
    /// поверхности, без которого команда ADB не адресуется, — и одновременно факт «ADB доступен» для
    /// секции. Точный endpoint разрешается владельцем этой операции
    /// (<see cref="AndroidReadinessService.ResolveEndpoint"/>), а не повторной реализацией формулы порта.
    /// </para>
    /// <para>
    /// Наблюдение готовности выполняется read-only путём orchestration
    /// (<see cref="AndroidReadinessService.Observe(AndroidEndpoint)"/>), а не <c>EnsureReadyAsync</c>:
    /// подключение transport остаётся операцией, которую запрашивает presentation-слой, а не startup.
    /// </para>
    /// <para>
    /// Непредвиденное исключение host-а остаётся диагностическим фактом: оно проецируется в
    /// application-отказ той же production-проекцией, что и остальные ошибки платформенной границы, а уже
    /// собранные наблюдения сохраняются в пробе.
    /// </para>
    /// </remarks>
    /// <param name="muMuHost">Host-side поверхность MuMu из DI.</param>
    /// <param name="muMuLifecycle">Orchestration MuMu из DI: владелец семантики выбора экземпляра.</param>
    /// <param name="androidHost">Host-side поверхность Android из DI.</param>
    /// <param name="readiness">Read-only наблюдение готовности Android из DI.</param>
    /// <param name="gameState">Read-only наблюдение состояния игры из DI.</param>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <param name="mapFailure">Проекция непредвиденного исключения границы в application-отказ.</param>
    /// <returns>Проба с фактическими наблюдениями либо с application-level отказом шага.</returns>
    /// <exception cref="ArgumentNullException">Любой из аргументов равен <see langword="null"/>.</exception>
    internal static AzurPilotAndroidProbe Capture(
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
        AndroidReadinessService readiness,
        AzurLaneGameStateService gameState,
        string configuredInstance,
        Func<Exception, ApplicationFailure> mapFailure)
    {
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(configuredInstance);
        ArgumentNullException.ThrowIfNull(mapFailure);

        string stage = InstallationStage;

        // Собранные факты живут вне try: нарушение контракта host-ом на следующем шаге не должно
        // обнулять то, что диагностика уже доказала — обнаруженный ADB и разрешённый endpoint.
        string? adbEvidence = null;
        AndroidEndpoint? resolved = null;
        AndroidReadinessFacts? facts = null;

        try
        {
            ApplicationResult<MuMuInstallation> discovery = muMuHost.DiscoverInstallation();
            if (discovery.IsFailure)
            {
                return Stopped(stage, discovery.FailureInfo!);
            }

            MuMuInstallation installation = discovery.Value!;

            stage = InstanceStage;
            MuMuInstanceResolution resolution = muMuLifecycle.Resolve(
                installation,
                SelectionFor(configuredInstance));
            if (resolution.IsFailed)
            {
                return Stopped(stage, resolution.Failure!);
            }

            MuMuInstance instance = resolution.Instance!;

            stage = AdbStage;
            ApplicationResult<AndroidAdbExecutable> adb = androidHost.DiscoverAdbExecutable(installation);
            if (adb.IsFailure)
            {
                return Stopped(stage, adb.FailureInfo!);
            }

            // Наружу сообщается только bounded evidence обнаружения: путь к ADB — machine-specific данные.
            adbEvidence = BoundedDiagnosticText.Bounded(adb.Value!.VersionEvidence);

            stage = EndpointStage;
            ApplicationResult<AndroidEndpoint> endpoint = readiness.ResolveEndpoint(installation, instance.Id);
            if (endpoint.IsFailure)
            {
                return new AzurPilotAndroidProbe(
                    stage,
                    endpoint.FailureInfo!,
                    IsAdbAvailable: true,
                    adbEvidence,
                    Endpoint: null,
                    Readiness: null,
                    Game: null);
            }

            // Разрешённый endpoint живёт в двух формах: точный адрес для команд и nullable факт для пробы,
            // который переживает нарушение контракта host-ом на следующем шаге.
            AndroidEndpoint target = endpoint.Value!;
            resolved = target;

            stage = ReadinessStage;
            ApplicationResult<AndroidReadinessFacts> observed = readiness.Observe(target);
            if (observed.IsFailure)
            {
                return new AzurPilotAndroidProbe(
                    stage,
                    observed.FailureInfo!,
                    IsAdbAvailable: true,
                    adbEvidence,
                    resolved,
                    Readiness: null,
                    Game: null);
            }

            facts = observed.Value!;
            AzurLaneGameObservation? game = null;

            if (facts.Transport.State == AndroidTransportState.Device)
            {
                stage = GameStage;
                ApplicationResult<AzurLaneGameObservation> observedGame = gameState.Observe(target);
                if (observedGame.IsFailure)
                {
                    return new AzurPilotAndroidProbe(
                        stage,
                        observedGame.FailureInfo!,
                        IsAdbAvailable: true,
                        adbEvidence,
                        resolved,
                        facts,
                        Game: null);
                }

                game = observedGame.Value!;
            }

            return new AzurPilotAndroidProbe(
                CompleteStage,
                Failure: null,
                IsAdbAvailable: true,
                adbEvidence,
                resolved,
                facts,
                game);
        }
        catch (Exception exception)
        {
            // Нарушение контракта host-ом не отклоняет запуск: оно остаётся данными диагностики, как и
            // ожидаемый отказ шага. Уже собранные факты при этом сохраняются, а состояние игры остаётся
            // ненаблюдённым: отказавший шаг не доказал его.
            return new AzurPilotAndroidProbe(
                stage,
                mapFailure(exception),
                IsAdbAvailable: adbEvidence is null ? null : true,
                adbEvidence,
                resolved,
                facts,
                Game: null);
        }
    }

    private static AzurPilotAndroidProbe Stopped(string stage, ApplicationFailure failure)
        => new(
            stage,
            failure,
            IsAdbAvailable: null,
            AdbEvidence: null,
            Endpoint: null,
            Readiness: null,
            Game: null);

    /// <summary>Переводит значение <c>mumu.instance</c> в семантику выбора экземпляра.</summary>
    /// <remarks>
    /// Синтаксис значения читается у его владельца <see cref="MuMuInstanceValue"/>: здесь не повторяются
    /// ни литерал автоматического выбора, ни префикс провайдерской формы, ни грамматика номера — её уже
    /// подтвердила строгая загрузка конфигурации. Отдельный экземпляр этого перевода нужен потому, что
    /// перевод MuMu-секции принадлежит её файлу, а Android-диагностика адресует тот же выбранный
    /// экземпляр своим read-only путём.
    /// </remarks>
    /// <param name="configuredInstance">Значение <c>mumu.instance</c> эффективной конфигурации.</param>
    /// <returns>Семантика автоматического либо явного выбора экземпляра.</returns>
    /// <exception cref="InvalidOperationException">
    /// Значение не соответствует синтаксису схемы: такое значение не может прийти из успешно загруженной
    /// конфигурации, поэтому это ошибка программирования, а не ожидаемый отказ Android.
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
