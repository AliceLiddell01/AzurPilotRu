namespace AzurPilot.Core.Android;

/// <summary>
/// Статус разрешения launcher-компонента пакета.
/// </summary>
/// <remarks>
/// Разрешение не догадывается: отсутствие launcher-а, несколько launcher-ов и неудачный запрос — три
/// разных случая, потому что каждый из них требует своего действия оператора.
/// </remarks>
public enum AndroidLauncherResolutionStatus
{
    /// <summary>Launcher разрешён ровно одним компонентом.</summary>
    Resolved = 0,

    /// <summary>У пакета нет разрешимого launcher-компонента.</summary>
    Missing = 1,

    /// <summary>Launcher не разрешён: подходящих компонентов несколько.</summary>
    Ambiguous = 2,

    /// <summary>Разрешение не доказано: запрос к устройству не дал распознанного ответа.</summary>
    QueryFailed = 3,
}
