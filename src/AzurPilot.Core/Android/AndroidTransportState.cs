namespace AzurPilot.Core.Android;

/// <summary>
/// Состояние ADB transport конкретного endpoint-а.
/// </summary>
/// <remarks>
/// Состояние относится к запрошенному endpoint-у и выводится только из ответа ADB про этот endpoint.
/// Наличие «какого-то устройства» в списке доказательством не является.
/// </remarks>
public enum AndroidTransportState
{
    /// <summary>Endpoint отсутствует в ответе ADB: устройства по этому адресу не видно.</summary>
    Absent = 0,

    /// <summary>Endpoint виден, но ADB сообщает его как offline: соединение не готово к командам.</summary>
    Offline = 1,

    /// <summary>Endpoint виден и готов к командам.</summary>
    Device = 2,

    /// <summary>Состояние не доказано: форма ответа ADB не распознана.</summary>
    Unknown = 3,
}
