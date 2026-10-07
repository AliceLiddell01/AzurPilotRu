using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Наблюдение состояния игры Azur Lane на точном endpoint-е: независимые факты и выведенное из них
/// состояние.
/// </summary>
/// <remarks>
/// <para>
/// Состояние не выдумывается: <see cref="AzurLaneGameState.Unknown"/> означает, что наблюдение не
/// доказало ни одного состояния, и это честный исход, а не «вероятно, остановлено».
/// </para>
/// <para>
/// Факты не смешиваются: наличие процесса не выдаётся за передний план, а наблюдённый передний план — за
/// наличие процесса. Наблюдение read-only: оно не запускает и не останавливает игру, не выполняет
/// <c>connect</c> и не исправляет неготовый transport.
/// </para>
/// </remarks>
public sealed class AzurLaneGameStateService
{
    private readonly IAndroidHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AzurLaneGameStateService> _logger;

    /// <summary>Создаёт наблюдение состояния игры Azur Lane.</summary>
    /// <param name="host">Host-side поверхность Android.</param>
    /// <param name="timeProvider">Источник времени для elapsed в диагностике.</param>
    /// <param name="logger">Логгер orchestration из существующего logging stack.</param>
    /// <exception cref="ArgumentNullException">Любая из зависимостей равна <see langword="null"/>.</exception>
    public AzurLaneGameStateService(
        IAndroidHost host,
        TimeProvider timeProvider,
        ILogger<AzurLaneGameStateService> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Идентификатор пакета игры Azur Lane Global/EN, которым адресуется наблюдение.</summary>
    public static AndroidPackageId Package { get; } = new(AzurLaneProduct.Package);

    /// <summary>Наблюдает факты об игре на точном endpoint-е, ничего не меняя.</summary>
    /// <remarks>
    /// <para>
    /// Порядок наблюдения: присутствие пакета, затем (только у доказанно отсутствующего или
    /// установленного пакета) процессы пакета и компонент переднего плана. У доказанно отсутствующего
    /// пакета процесс и передний план не наблюдаются, потому что факт об отсутствующем пакете не является
    /// фактом о его процессах.
    /// </para>
    /// <para>
    /// Недоказанное наблюдение не сворачивается в отрицательный факт и не подменяется отказом: оно
    /// остаётся наблюдением с фактом <see langword="null"/>, который означает «не доказано». Поэтому
    /// «не удалось спросить» никогда не выдаётся за «пакета нет», «процессов нет» или «игра не на
    /// переднем плане»: состояние, выведенное из таких фактов, остаётся недоказанным, а не доказанным
    /// остановленным.
    /// </para>
    /// <para>
    /// Ожидаемый отказ host-а пробрасывается без изменений, поэтому его точный код и details сохраняются.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, состояние игры на котором наблюдается.</param>
    /// <returns>Успешное наблюдение фактов либо ожидаемый отказ.</returns>
    public ApplicationResult<AzurLaneGameFacts> ObserveFactsAsync(AndroidEndpoint endpoint)
    {
        ApplicationResult<AndroidPackagePresence> presence = _host.QueryPackage(endpoint, Package);
        if (presence.IsFailure)
        {
            return ApplicationResult<AzurLaneGameFacts>.Failure(presence.FailureInfo!);
        }

        AndroidPackagePresence observedPresence = presence.Value!;

        if (observedPresence == AndroidPackagePresence.Absent)
        {
            // Доказанное отсутствие пакета: процессов и переднего плана у него быть не может, поэтому
            // дополнительные команды ADB не выполняются.
            return ApplicationResult<AzurLaneGameFacts>.Success(
                new AzurLaneGameFacts(Installed: false, ProcessRunning: false, Foreground: false));
        }

        if (observedPresence != AndroidPackagePresence.Installed)
        {
            // Присутствие пакета не доказано: «пакета нет» здесь не утверждается, и процесс с передним
            // планом не наблюдаются, потому что факт о недоказанной установке ничего о них не говорит.
            return ApplicationResult<AzurLaneGameFacts>.Success(
                new AzurLaneGameFacts(Installed: null, ProcessRunning: null, Foreground: null));
        }

        ApplicationResult<AndroidProcessObservation> processes = _host.ObserveProcesses(endpoint, Package);
        if (processes.IsFailure)
        {
            return ApplicationResult<AzurLaneGameFacts>.Failure(processes.FailureInfo!);
        }

        ApplicationResult<AndroidForegroundObservation> foreground = _host.ObserveForeground(endpoint);
        if (foreground.IsFailure)
        {
            return ApplicationResult<AzurLaneGameFacts>.Failure(foreground.FailureInfo!);
        }

        return ApplicationResult<AzurLaneGameFacts>.Success(
            new AzurLaneGameFacts(
                Installed: true,
                ProcessRunning: IsProcessRunning(processes.Value!),
                Foreground: IsForeground(foreground.Value!)));
    }

    /// <summary>Наблюдает состояние игры на точном endpoint-е, ничего не меняя.</summary>
    /// <remarks>
    /// Состояние выводится из независимых фактов тем же единственным правилом, что и
    /// <see cref="DeriveState"/>, поэтому отдельного пути вывода состояния в этом методе нет.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, состояние игры на котором наблюдается.</param>
    /// <returns>Успешное наблюдение состояния либо ожидаемый отказ.</returns>
    public ApplicationResult<AzurLaneGameObservation> ObserveAsync(AndroidEndpoint endpoint)
    {
        long started = _timeProvider.GetTimestamp();

        ApplicationResult<AzurLaneGameFacts> observed = ObserveFactsAsync(endpoint);
        if (observed.IsFailure)
        {
            return ApplicationResult<AzurLaneGameObservation>.Failure(observed.FailureInfo!);
        }

        AzurLaneGameFacts facts = observed.Value!;
        AzurLaneGameState state = DeriveState(facts);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        string evidence = AndroidEvidence.Compose(
            "state=" + AndroidNames.GameStateName(state),
            "installed=" + Lowercase(facts.Installed),
            "process=" + Lowercase(facts.ProcessRunning),
            "foreground=" + Lowercase(facts.Foreground));

        if (state == AzurLaneGameState.Unknown)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                AndroidLog.GameStateUnknown(
                    _logger,
                    endpoint.ToString(),
                    Package.ToString(),
                    AndroidNames.PollingPhase,
                    evidence);
            }
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            AndroidLog.GameStateObserved(
                _logger,
                endpoint.ToString(),
                Package.ToString(),
                AndroidNames.GameStateName(state),
                (long)elapsed.TotalMilliseconds,
                evidence);
        }

        return ApplicationResult<AzurLaneGameObservation>.Success(
            new AzurLaneGameObservation(state, facts, evidence));
    }

    /// <summary>Выводит состояние игры из независимых наблюдённых фактов.</summary>
    /// <remarks>
    /// <para>
    /// Единственный владелец вывода состояния: и наблюдение, и lifecycle-операции получают состояние
    /// отсюда, поэтому правила не расходятся между ними.
    /// </para>
    /// <para>
    /// Правило fail-closed и учитывает трёхзначность фактов, где <see langword="null"/> означает
    /// недоказанность. Доказанное отсутствие пакета (<see langword="false"/>) даёт
    /// <see cref="AzurLaneGameState.NotInstalled"/>, но только тогда, когда процесс и передний план не
    /// утверждаются: доказанный процесс или передний план у отсутствующего пакета — противоречивые факты
    /// и дают <see cref="AzurLaneGameState.Unknown"/>. Недоказанное присутствие пакета, недоказанное
    /// наблюдение процессов или переднего плана (<see langword="null"/>) дают
    /// <see cref="AzurLaneGameState.Unknown"/>, а не догадку. Дальше: игра на переднем плане —
    /// <see cref="AzurLaneGameState.Foreground"/>, наблюдённый процесс без переднего плана —
    /// <see cref="AzurLaneGameState.Background"/>, установка без наблюдённого процесса —
    /// <see cref="AzurLaneGameState.Stopped"/>.
    /// </para>
    /// </remarks>
    /// <param name="facts">Независимые наблюдённые факты об игре.</param>
    /// <returns>Состояние игры, доказанное этими фактами.</returns>
    public static AzurLaneGameState DeriveState(AzurLaneGameFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Installed is false)
        {
            // Доказанное отсутствие пакета: утверждение о его процессе или переднем плане было бы
            // противоречием, поэтому такие факты дают недоказанное состояние.
            return facts.ProcessRunning is true || facts.Foreground is true
                ? AzurLaneGameState.Unknown
                : AzurLaneGameState.NotInstalled;
        }

        if (facts.Installed is null)
        {
            return AzurLaneGameState.Unknown;
        }

        if (facts.ProcessRunning is null || facts.Foreground is null)
        {
            // Недоказанный факт не достраивается: состояние остаётся недоказанным.
            return AzurLaneGameState.Unknown;
        }

        if (facts.Foreground is true)
        {
            // Наблюдённый передний план игры доказан её собственным наблюдением, поэтому наличие процесса
            // здесь не требуется: это разные факты, а не один.
            return AzurLaneGameState.Foreground;
        }

        return facts.ProcessRunning is true ? AzurLaneGameState.Background : AzurLaneGameState.Stopped;
    }

    /// <summary>Проверяет, что наблюдение процессов доказало наличие процессов пакета игры.</summary>
    /// <remarks>
    /// <see langword="null"/> означает недоказанность: список <see langword="null"/> не является пустым
    /// списком и не выдаётся за доказанное отсутствие процессов.
    /// </remarks>
    /// <param name="observation">Наблюдение процессов пакета игры.</param>
    /// <returns>
    /// <see langword="true"/> — процессы наблюдены, <see langword="false"/> — доказанное отсутствие
    /// процессов, <see langword="null"/> — наблюдение не доказано.
    /// </returns>
    private static bool? IsProcessRunning(AndroidProcessObservation observation)
        => observation.ProcessIds is null ? null : observation.ProcessCount > 0;

    /// <summary>Проверяет, что наблюдение подтвердило передний план пакета игры.</summary>
    /// <remarks>
    /// Компонент наблюдения — evidence, а не запрошенный компонент: host сравнивает наблюдённый пакет
    /// переднего плана с пакетом игры и возвращает <see cref="AndroidForegroundStatus.Foreground"/> при
    /// равенстве. Сравнения компонентов здесь нет, потому что после запуска launcher-компонента на
    /// переднем плане может оказаться другая activity того же пакета.
    /// </remarks>
    /// <param name="observation">Наблюдение переднего плана.</param>
    /// <returns>
    /// <see langword="true"/> — пакет игры на переднем плане, <see langword="false"/> — на переднем плане
    /// другое, <see langword="null"/> — передний план не доказан.
    /// </returns>
    private static bool? IsForeground(AndroidForegroundObservation observation) => observation.Status switch
    {
        AndroidForegroundStatus.Foreground => true,
        AndroidForegroundStatus.Other => false,
        AndroidForegroundStatus.Unknown => null,
        _ => null,
    };

    private static string Lowercase(bool? value) => value switch
    {
        true => "true",
        false => "false",
        _ => "unknown",
    };
}
