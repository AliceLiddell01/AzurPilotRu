namespace AzurPilot.Core.Android;

/// <summary>
/// Наблюдение ADB transport точного endpoint-а.
/// </summary>
/// <remarks>
/// Наблюдение адресуется endpoint-у, а не «списку устройств ADB». <see cref="Evidence"/> — bounded
/// сведения о том, чем подтверждено наблюдение: полный вывод ADB в них не попадает и в логи не
/// пишется.
/// </remarks>
/// <param name="State">Доказанное состояние transport.</param>
/// <param name="Evidence">Bounded evidence наблюдения, пригодный для диагностики.</param>
public sealed record AndroidTransportObservation(AndroidTransportState State, string Evidence);
