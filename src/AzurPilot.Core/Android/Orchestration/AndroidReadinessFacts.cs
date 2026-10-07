namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Read-only наблюдение Android на точном endpoint-е: состояние transport и, если transport готов,
/// готовность Android.
/// </summary>
/// <remarks>
/// <para>
/// Наблюдение не выполняет mutation: ни <c>connect</c>, ни <c>reconnect</c>, ни любую другую команду,
/// меняющую состояние. Поэтому неготовый transport сообщается фактом своего состояния, а не
/// исправляется.
/// </para>
/// <para>
/// <see cref="Boot"/> заполнен только тогда, когда transport доказанно готов к командам: у неготового
/// transport готовность Android не наблюдается и не додумывается.
/// </para>
/// </remarks>
/// <param name="Endpoint">Точный endpoint, которому адресовано наблюдение.</param>
/// <param name="Transport">Наблюдение ADB transport.</param>
/// <param name="Boot">Наблюдение готовности Android либо <see langword="null"/>, если transport не готов.</param>
/// <param name="Evidence">Bounded однострочный evidence наблюдения.</param>
public sealed record AndroidReadinessFacts(
    AndroidEndpoint Endpoint,
    AndroidTransportObservation Transport,
    AndroidBootObservation? Boot,
    string Evidence);

/// <summary>
/// Итог доказанной готовности Android на точном endpoint-е.
/// </summary>
/// <remarks>
/// Значение возвращается только тогда, когда postcondition готовности доказан наблюдением: transport
/// находится в состоянии, готовом к командам, а устройство подтвердило завершение загрузки. Отдельного
/// утверждения о готовности UI здесь нет: доказано ровно то, что наблюдено.
/// </remarks>
/// <param name="Endpoint">Точный endpoint, готовность которого доказана.</param>
/// <param name="Boot">Наблюдение, которым доказана готовность Android.</param>
/// <param name="Evidence">Bounded однострочный evidence достигнутого postcondition.</param>
public sealed record AndroidReadinessOutcome(
    AndroidEndpoint Endpoint,
    AndroidBootObservation Boot,
    string Evidence);
