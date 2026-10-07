namespace AzurPilot.Core.Android;

/// <summary>
/// Наблюдение готовности Android на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShellAvailable"/> сообщает, ответил ли shell устройства вообще: это признак работающего
/// ADB-соединения, а не готовности Android. Значения <see cref="BootCompleted"/>,
/// <see cref="AndroidRelease"/> и <see cref="SdkLevel"/> читаются у устройства и остаются
/// <see langword="null"/>, если устройство их не сообщило; отсутствие значения означает «не
/// наблюдалось», а не «готово» и не «не готово».
/// </para>
/// <para>
/// <see cref="Evidence"/> — bounded сведения о наблюдении: полный вывод shell в них не попадает.
/// </para>
/// </remarks>
/// <param name="ShellAvailable">Признак того, что shell устройства доступен.</param>
/// <param name="BootCompleted">Значение <c>sys.boot_completed</c> либо <see langword="null"/>, если не наблюдалось.</param>
/// <param name="AndroidRelease">Версия Android, сообщённая устройством, либо <see langword="null"/>.</param>
/// <param name="SdkLevel">Уровень Android SDK, сообщённый устройством, либо <see langword="null"/>.</param>
/// <param name="Evidence">Bounded evidence наблюдения, пригодный для диагностики.</param>
public sealed record AndroidBootObservation(
    bool ShellAvailable,
    int? BootCompleted,
    string? AndroidRelease,
    int? SdkLevel,
    string Evidence);
