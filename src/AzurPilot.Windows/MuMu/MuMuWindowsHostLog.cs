using Microsoft.Extensions.Logging;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Устойчивые project-owned события host-side поверхности MuMu.
/// </summary>
/// <remarks>
/// <para>
/// События оформлены source-generated <c>[LoggerMessage]</c> на существующем logging stack: сообщение и
/// его structured properties заданы статически, второй logger не заводится.
/// </para>
/// <para>
/// События bounded и не дублируют события orchestration Core: host сообщает только то, чего Core не
/// видит — итог обнаружения установки. Пути установки в логи не попадают: сообщается версия и счётчики.
/// </para>
/// </remarks>
internal static partial class MuMuWindowsHostLog
{
    /// <summary>Установка MuMu обнаружена.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="version">Версия обнаруженной установки.</param>
    /// <param name="rejectedCandidates">Сколько кандидатов установки не удалось разрешить.</param>
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "MuMu установка обнаружена: версия {Version}, отклонённых кандидатов {RejectedCandidates}")]
    public static partial void InstallationDiscovered(
        this ILogger logger,
        string version,
        int rejectedCandidates);

    /// <summary>Установка MuMu не обнаружена или не разрешена однозначно.</summary>
    /// <param name="logger">Логгер host-а.</param>
    /// <param name="failureCode">Стабильный код отказа обнаружения.</param>
    /// <param name="rejectedCandidates">Сколько кандидатов установки не удалось разрешить.</param>
    /// <param name="installationsFound">Сколько установок обнаружено.</param>
    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "MuMu установка не разрешена: код отказа {FailureCode}, установок найдено "
            + "{InstallationsFound}, отклонённых кандидатов {RejectedCandidates}")]
    public static partial void InstallationDiscoveryFailed(
        this ILogger logger,
        string failureCode,
        int installationsFound,
        int rejectedCandidates);
}
