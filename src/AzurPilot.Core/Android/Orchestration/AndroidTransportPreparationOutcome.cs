namespace AzurPilot.Core.Android.Orchestration;

/// <summary>Итог явной подготовки ADB transport выбранного endpoint-а.</summary>
/// <remarks>
/// Подготовка успешна только после независимого наблюдения готового transport, доступного shell и
/// завершённой загрузки Android. Значение исходного transport показывает, почему выполнялся или
/// пропускался единственный допустимый <c>connect</c>.
/// </remarks>
/// <param name="Endpoint">Точный endpoint, для которого доказана готовность.</param>
/// <param name="InitialTransport">Состояние endpoint-а до возможного подключения.</param>
/// <param name="FinalTransport">Состояние endpoint-а после подготовки.</param>
/// <param name="Boot">Наблюдение shell и завершения загрузки Android.</param>
/// <param name="ConnectAttempted">Выполнялась ли единственная разрешённая попытка подключения.</param>
/// <param name="Evidence">Bounded evidence исходного состояния и postconditions.</param>
public sealed record AndroidTransportPreparationOutcome(
    AndroidEndpoint Endpoint,
    AndroidTransportState InitialTransport,
    AndroidTransportState FinalTransport,
    AndroidBootObservation Boot,
    bool ConnectAttempted,
    string Evidence)
{
    /// <summary>Возвращает machine-readable имя исходного transport.</summary>
    public string InitialTransportName => AndroidNames.TransportStateName(InitialTransport);

    /// <summary>Возвращает machine-readable имя transport после подготовки.</summary>
    public string FinalTransportName => AndroidNames.TransportStateName(FinalTransport);
}
