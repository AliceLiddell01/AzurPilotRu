using System.Globalization;

namespace AzurPilot.Core.Android;

/// <summary>
/// Bounded текстовые имена значений Android-контракта для логов и structured details.
/// </summary>
/// <remarks>
/// Имена machine-readable и не зависят от языка: сообщения для оператора принадлежат владельцу
/// конкретного отказа, а здесь задаются только стабильные значения полей. Каждое значение перечисления
/// перечислено явно, а неизвестное значение даёт <c>unknown</c>, а не исключение: диагностика не должна
/// падать из-за нового значения перечисления.
/// </remarks>
internal static class AndroidNames
{
    /// <summary>Имя фазы: ожидание готовности ADB transport.</summary>
    internal const string TransportPhase = "transport";

    /// <summary>Имя фазы: ожидание готовности Android.</summary>
    internal const string BootPhase = "boot";

    /// <summary>Имя фазы: наблюдение установки пакета.</summary>
    internal const string PackagePhase = "package";

    /// <summary>Имя фазы: разрешение launcher-компонента.</summary>
    internal const string LauncherPhase = "launcher";

    /// <summary>Имя фазы: выполнение mutation игры.</summary>
    internal const string MutationPhase = "mutation";

    /// <summary>Имя фазы: bounded polling наблюдённого состояния игры.</summary>
    internal const string PollingPhase = "polling";

    /// <summary>Имя операции: достижение готовности ADB.</summary>
    internal const string ReadyOperation = "ready";

    /// <summary>Имя операции: запуск игры.</summary>
    internal const string StartOperation = "start";

    /// <summary>Имя операции: остановка игры.</summary>
    internal const string StopOperation = "stop";

    /// <summary>Возвращает machine-readable имя состояния ADB transport.</summary>
    /// <param name="state">Состояние transport.</param>
    /// <returns>Одно из значений <c>absent</c>, <c>offline</c>, <c>device</c>, <c>unknown</c>.</returns>
    internal static string TransportStateName(AndroidTransportState state) => state switch
    {
        AndroidTransportState.Absent => AbsentName,
        AndroidTransportState.Offline => OfflineName,
        AndroidTransportState.Device => DeviceName,
        AndroidTransportState.Unknown => UnknownName,
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя присутствия пакета.</summary>
    /// <param name="presence">Наблюдённое присутствие пакета.</param>
    /// <returns>Одно из значений <c>installed</c>, <c>absent</c>, <c>query_failed</c>.</returns>
    internal static string PackagePresenceName(AndroidPackagePresence presence) => presence switch
    {
        AndroidPackagePresence.Installed => InstalledName,
        AndroidPackagePresence.Absent => AbsentName,
        AndroidPackagePresence.QueryFailed => QueryFailedName,
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя статуса разрешения launcher-а.</summary>
    /// <param name="status">Статус разрешения.</param>
    /// <returns>Одно из значений <c>resolved</c>, <c>missing</c>, <c>ambiguous</c>, <c>query_failed</c>.</returns>
    internal static string LauncherStatusName(AndroidLauncherResolutionStatus status) => status switch
    {
        AndroidLauncherResolutionStatus.Resolved => ResolvedName,
        AndroidLauncherResolutionStatus.Missing => MissingName,
        AndroidLauncherResolutionStatus.Ambiguous => AmbiguousName,
        AndroidLauncherResolutionStatus.QueryFailed => QueryFailedName,
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя статуса foreground.</summary>
    /// <param name="status">Статус foreground.</param>
    /// <returns>Одно из значений <c>foreground</c>, <c>other</c>, <c>unknown</c>.</returns>
    internal static string ForegroundStatusName(AndroidForegroundStatus status) => status switch
    {
        AndroidForegroundStatus.Foreground => ForegroundName,
        AndroidForegroundStatus.Other => OtherName,
        AndroidForegroundStatus.Unknown => UnknownName,
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя состояния игры.</summary>
    /// <param name="state">Состояние игры.</param>
    /// <returns>
    /// Одно из значений <c>not_installed</c>, <c>stopped</c>, <c>background</c>, <c>foreground</c>,
    /// <c>unknown</c>.
    /// </returns>
    internal static string GameStateName(AzurLaneGameState state) => state switch
    {
        AzurLaneGameState.NotInstalled => NotInstalledName,
        AzurLaneGameState.Stopped => StoppedName,
        AzurLaneGameState.Background => BackgroundName,
        AzurLaneGameState.Foreground => ForegroundName,
        AzurLaneGameState.Unknown => UnknownName,
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя mutation игры.</summary>
    /// <param name="mutation">Выполненная mutation.</param>
    /// <returns>Одно из значений <c>start</c>, <c>force_stop</c>.</returns>
    internal static string MutationName(AndroidGameMutation mutation) => mutation switch
    {
        AndroidGameMutation.Start => StartOperation,
        AndroidGameMutation.ForceStop => ForceStopName,
        _ => UnknownName,
    };

    /// <summary>Возвращает bounded описание выполненной mutation.</summary>
    /// <param name="mutation">Выполненная mutation.</param>
    /// <param name="exitCode">Код выхода команды ADB.</param>
    /// <returns>Строка вида <c>start(exit=0)</c>.</returns>
    internal static string Mutation(AndroidGameMutation mutation, int exitCode)
        => MutationName(mutation)
            + "(exit="
            + exitCode.ToString(CultureInfo.InvariantCulture)
            + ")";

    /// <summary>Возвращает значение счётчика в инвариантной культуре.</summary>
    /// <param name="value">Числовое значение.</param>
    /// <returns>Текстовая форма значения, независимая от текущей культуры.</returns>
    internal static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    private const string AbsentName = "absent";

    private const string OfflineName = "offline";

    private const string DeviceName = "device";

    private const string InstalledName = "installed";

    private const string QueryFailedName = "query_failed";

    private const string ResolvedName = "resolved";

    private const string MissingName = "missing";

    private const string AmbiguousName = "ambiguous";

    private const string ForegroundName = "foreground";

    private const string OtherName = "other";

    private const string NotInstalledName = "not_installed";

    private const string StoppedName = "stopped";

    private const string BackgroundName = "background";

    private const string ForceStopName = "force_stop";

    private const string UnknownName = "unknown";
}
