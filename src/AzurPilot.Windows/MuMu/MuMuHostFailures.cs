using System.Globalization;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Ожидаемые отказы, которые синтезирует host-side реализация MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Единственный владелец того, какие ожидаемые MuMu-отказы синтезирует платформенная сторона: коды
/// берутся из констант <see cref="ApplicationFailure"/>, строковые литералы кодов здесь не дублируются,
/// и ожидаемый MuMu-отказ никогда не подменяется <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// <para>
/// Отказы, возвращённые адаптерами (недостижимая control surface, дедлайн команды, отмена, ошибка
/// чтения реестра или файловой системы), здесь не пересоздаются: host пробрасывает их без изменений,
/// поэтому точный код и details сохраняются.
/// </para>
/// <para>
/// Details содержат только bounded факты: причину, каноническую identity экземпляра, счётчики
/// обнаруженных установок и отклонённых кандидатов, набор причин отклонения и код ошибки провайдера.
/// Пути установки, имена каталогов и содержимое ответа провайдера в details не попадают.
/// </para>
/// </remarks>
internal static class MuMuHostFailures
{
    /// <summary>Создаёт отказ «установка MuMuPlayer не обнаружена».</summary>
    /// <remarks>
    /// Код сообщается только тогда, когда установка действительно не обнаружена: несколько найденных
    /// установок сообщаются кодом <see cref="ApplicationFailure.MuMuInstallationAmbiguous"/>, а отсутствие
    /// поддерживаемой точки входа в найденной установке — кодом
    /// <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/>. Случаи различимы по коду, а не по
    /// тексту сообщения.
    /// </remarks>
    /// <param name="rejectedCandidateCount">Сколько кандидатов установки не удалось разрешить.</param>
    /// <param name="rejectionReasons">Причины отклонения кандидатов.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuInstallationNotFound"/>.</returns>
    internal static ApplicationFailure InstallationNotFound(
        int rejectedCandidateCount,
        IReadOnlyList<string> rejectionReasons)
    {
        ArgumentNullException.ThrowIfNull(rejectionReasons);

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.RejectedCandidates] = Count(rejectedCandidateCount),
        };

        if (rejectionReasons.Count > 0)
        {
            details[MuMuFailureDetailKeys.RejectionReasons] = MuMuBoundedText.Bounded(string.Join(",", rejectionReasons));
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuInstallationNotFound,
            Message = rejectedCandidateCount == 0
                ? "Установка MuMuPlayer не обнаружена: ни один источник не сообщил о ней."
                : $"Установка MuMuPlayer не обнаружена: {Count(rejectedCandidateCount)} кандидатов не удалось "
                    + "разрешить, а доказанной установки нет.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «однозначная установка не определена».</summary>
    /// <remarks>
    /// Обнаружено несколько установок: доказуемого правила выбора между ними нет, а первая попавшаяся
    /// установка не выбирается. Код отказа — <see cref="ApplicationFailure.MuMuInstallationAmbiguous"/>:
    /// он симметричен коду неоднозначности экземпляра и не утверждает «MuMu не установлен», что было бы
    /// неверно при нескольких найденных установках. Причина и число найденных установок остаются в
    /// bounded details.
    /// </remarks>
    /// <param name="installationsFound">Сколько установок обнаружено.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuInstallationAmbiguous"/>.</returns>
    internal static ApplicationFailure InstallationAmbiguous(int installationsFound)
        => new()
        {
            Code = ApplicationFailure.MuMuInstallationAmbiguous,
            Message = $"Однозначная установка MuMuPlayer не определена: обнаружено "
                + $"{Count(installationsFound)} установок, а выбор между установками не определён.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.InstallationAmbiguous,
                [MuMuFailureDetailKeys.InstallationsFound] = Count(installationsFound),
            },
        };

    /// <summary>Создаёт отказ «установка есть, но поддерживаемой control surface в ней нет».</summary>
    /// <param name="rejectedCandidateCount">Сколько кандидатов установки не удалось разрешить.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/>.</returns>
    internal static ApplicationFailure ControlSurfaceMissing(int rejectedCandidateCount)
        => new()
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Установка MuMuPlayer есть, но ожидаемой точки входа control surface в ней нет: "
                + "раскладка установки не поддерживается.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.ControlSurfaceMissing,
                [MuMuFailureDetailKeys.RejectedCandidates] = Count(rejectedCandidateCount),
            },
        };

    /// <summary>Создаёт отказ «запрошенного экземпляра нет».</summary>
    /// <param name="instance">Identity запрошенного экземпляра.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuInstanceNotFound"/>.</returns>
    internal static ApplicationFailure InstanceNotFound(MuMuInstanceId instance)
        => new()
        {
            Code = ApplicationFailure.MuMuInstanceNotFound,
            Message = $"Android-экземпляр MuMu «{instance}» не найден в установке.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MuMuFailureDetailKeys.InstanceId] = instance.ToString(),
            },
        };

    /// <summary>Создаёт отказ «состояние экземпляра не удалось установить».</summary>
    /// <remarks>
    /// Провайдер отказал с кодом, смысл которого не доказан: host не имеет права ни вывести состояние, ни
    /// истолковать отказ как «экземпляра нет», поэтому отказ закрытый.
    /// </remarks>
    /// <param name="instance">Identity запрошенного экземпляра.</param>
    /// <param name="providerErrorCode">Код ошибки, который вернул провайдер.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/>.</returns>
    internal static ApplicationFailure InstanceStateUnavailable(MuMuInstanceId instance, int providerErrorCode)
        => new()
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = $"Состояние Android-экземпляра MuMu «{instance}» не установлено: провайдер отказал с "
                + $"кодом {Count(providerErrorCode)}, смысл которого не доказан.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.ProviderErrorUnrecognized,
                [MuMuFailureDetailKeys.InstanceId] = instance.ToString(),
                [MuMuFailureDetailKeys.ProviderErrorCode] = Count(providerErrorCode),
            },
        };

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
