using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Android;

/// <summary>
/// Фабрики ожидаемых Android-отказов и отказов lifecycle игры с bounded structured details.
/// </summary>
/// <remarks>
/// <para>
/// Единственный владелец того, какие отказы синтезирует Core в этом слое: коды берутся из констант
/// <see cref="ApplicationFailure"/>, а не из строковых литералов, и ожидаемый Android-отказ никогда не
/// подменяется <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// <para>
/// Здесь синтезируются отказы, которые orchestration устанавливает сама по доказанному условию.
/// Ожидаемые отказы, возвращённые host-ом, не пересоздаются: они пробрасываются без изменений, поэтому
/// их точный код и details сохраняются.
/// </para>
/// <para>
/// Отдельного кода на каждый код выхода ADB не заводится: код выхода — evidence, а не причина отказа.
/// </para>
/// <para>
/// Отмена — контракт отмены Core, а не Android-код: отмена, замеченная на любой фазе, приводится к
/// <see cref="ApplicationFailure.OperationCancelled"/> с именем фазы в details.
/// </para>
/// <para>
/// Details содержат только bounded machine-stable факты: точный endpoint, идентификатор пакета,
/// наблюдённое и требуемое состояние, фазу операции, код выхода команды и bounded evidence наблюдения.
/// Ключ пакета присутствует только у тех форм, которые действительно относятся к пакету: форма отмены
/// операции готовности его не содержит, потому что готовность наблюдается до всякой работы с пакетом.
/// Абсолютные machine-пути (в том числе путь обнаруженного ADB), полный stdout/stderr и полный список
/// процессов устройства в details не попадают.
/// </para>
/// </remarks>
internal static class AndroidFailures
{
    private const string EndpointDetailKey = "endpoint";

    private const string PackageDetailKey = "package";

    private const string StateDetailKey = "state";

    private const string TargetStateDetailKey = "target_state";

    private const string PhaseDetailKey = "phase";

    private const string ExitCodeDetailKey = "exit_code";

    private const string MatchingComponentCountDetailKey = "matching_components";

    private const string ElapsedMillisecondsDetailKey = "elapsed_ms";

    private const string EvidenceDetailKey = "evidence";

    /// <summary>Создаёт отказ «ADB transport не готов к командам».</summary>
    /// <param name="endpoint">Точный endpoint, состояние которого наблюдалось.</param>
    /// <param name="state">Наблюдённое состояние transport.</param>
    /// <param name="phase">Имя фазы операции, в которой готовность не доказана.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidTransportNotReady"/>.</returns>
    internal static ApplicationFailure TransportNotReady(
        AndroidEndpoint endpoint,
        AndroidTransportState state,
        string phase)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [StateDetailKey] = AndroidNames.TransportStateName(state),
            [PhaseDetailKey] = phase,
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidTransportNotReady,
            Message = $"ADB transport endpoint-а {endpoint} не готов к командам: наблюдённое состояние — "
                + $"{AndroidNames.TransportStateName(state)}.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «готовность Android не доказана».</summary>
    /// <remarks>
    /// Отказ означает, что наблюдение не подтвердило готовность в пределах отведённого времени или
    /// условия: это не доказательство невозможности, поэтому повтор имеет смысл.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, готовность которого не доказана.</param>
    /// <param name="phase">Имя фазы операции, в которой готовность не доказана.</param>
    /// <param name="evidence">Bounded evidence последнего наблюдения.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidNotReady"/>.</returns>
    internal static ApplicationFailure NotReady(AndroidEndpoint endpoint, string phase, string evidence)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PhaseDetailKey] = phase,
            [EvidenceDetailKey] = AndroidEvidence.Truncate(evidence),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidNotReady,
            Message = $"Android на endpoint-е {endpoint} не достиг готовности: требуемое состояние не "
                + "доказано наблюдением.",
            IsRetryable = true,
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «пакет игры не установлен».</summary>
    /// <param name="endpoint">Точный endpoint, на котором пакет не найден.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLanePackageMissing"/>.</returns>
    internal static ApplicationFailure PackageMissing(AndroidEndpoint endpoint, AndroidPackageId package)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = AndroidNames.PackagePresenceName(AndroidPackagePresence.Absent),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLanePackageMissing,
            Message = $"Пакет {package} не установлен на endpoint-е {endpoint}: устройство подтвердило его "
                + "отсутствие.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «состояние игры не доказано».</summary>
    /// <remarks>
    /// Отказ синтезируется только тогда, когда наблюдение не доказало ни одного состояния игры: «не
    /// удалось наблюдать» не выдаётся ни за «остановлено», ни за «запущено».
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, состояние игры которого не доказано.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="phase">Имя фазы операции, в которой состояние не доказано.</param>
    /// <param name="evidence">Bounded evidence последнего наблюдения.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneStateUnknown"/>.</returns>
    internal static ApplicationFailure StateUnknown(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string phase,
        string evidence)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [PhaseDetailKey] = phase,
            [EvidenceDetailKey] = AndroidEvidence.Truncate(evidence),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneStateUnknown,
            Message = $"Состояние игры {package} на endpoint-е {endpoint} не доказано наблюдением.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «launcher-компонент пакета не разрешён».</summary>
    /// <remarks>
    /// <para>
    /// Форма принимает фактически наблюдённый статус разрешения, потому что «компонента нет» и «ответ не
    /// распознан» — разные факты, и выдавать один за другой запрещено: в details <c>state</c> попадает
    /// именно наблюдённый статус, а сообщение описывает именно его.
    /// </para>
    /// <para>
    /// Код отказа для обоих случаев один — <see cref="ApplicationFailure.AzurLaneLauncherUnresolved"/>:
    /// и отсутствие компонента, и нераспознанный ответ одинаково не позволяют адресовать mutation запуска,
    /// поэтому второй код и второй перечень кодов не заводятся. Неоднозначность разрешения сюда не
    /// попадает: у неё своя форма <see cref="LauncherAmbiguous"/>.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, у которого разрешался launcher.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="status">
    /// Наблюдённый статус разрешения: <see cref="AndroidLauncherResolutionStatus.Missing"/> или
    /// <see cref="AndroidLauncherResolutionStatus.QueryFailed"/>.
    /// </param>
    /// <param name="phase">Имя фазы операции, в которой launcher не разрешён.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLauncherUnresolved"/>.</returns>
    internal static ApplicationFailure LauncherUnresolved(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AndroidLauncherResolutionStatus status,
        string phase)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = AndroidNames.LauncherStatusName(status),
            [PhaseDetailKey] = phase,
        };

        string reason = status == AndroidLauncherResolutionStatus.QueryFailed
            ? "разрешение launcher-компонента не дало распознанного ответа"
            : "нет разрешимого launcher-компонента";

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLauncherUnresolved,
            Message = $"У пакета {package} на endpoint-е {endpoint} {reason}: запуск игры не может быть "
                + "адресован.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «launcher-компонент неоднозначен».</summary>
    /// <param name="endpoint">Точный endpoint, у которого разрешался launcher.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="matchingComponentCount">Число компонентов, совпавших с запросом.</param>
    /// <param name="phase">Имя фазы операции, в которой launcher не разрешён.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLauncherAmbiguous"/>.</returns>
    internal static ApplicationFailure LauncherAmbiguous(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        int matchingComponentCount,
        string phase)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = AndroidNames.LauncherStatusName(AndroidLauncherResolutionStatus.Ambiguous),
            [MatchingComponentCountDetailKey] = AndroidNames.Count(matchingComponentCount),
            [PhaseDetailKey] = phase,
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLauncherAmbiguous,
            Message = $"Launcher-компонент пакета {package} на endpoint-е {endpoint} не разрешён: "
                + $"подходящих компонентов {AndroidNames.Count(matchingComponentCount)}, а запуск "
                + "адресуется только одному.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «postcondition lifecycle игры не достигнут».</summary>
    /// <remarks>
    /// Отказ синтезируется только тогда, когда mutation была действительно выполнена, но следующее
    /// авторитетное наблюдение не показывает требуемое состояние: ждать deadline бессмысленно, потому
    /// что причина не во времени.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, на котором выполнялась mutation.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="state">Наблюдённое состояние игры, в котором postcondition не достигнут.</param>
    /// <param name="targetState">Требуемое состояние игры.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation игры.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLifecyclePostconditionNotMet"/>.</returns>
    internal static ApplicationFailure LifecyclePostconditionNotMet(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState state,
        AzurLaneGameState targetState,
        TimeSpan elapsed,
        int mutationExitCode)
    {
        Dictionary<string, string> details = LifecycleDetails(endpoint, package, state, elapsed);
        details[TargetStateDetailKey] = AndroidNames.GameStateName(targetState);
        details[ExitCodeDetailKey] = AndroidNames.Count(mutationExitCode);

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
            Message = $"Игра {package} на endpoint-е {endpoint}: mutation не привела к состоянию "
                + $"{AndroidNames.GameStateName(targetState)}, поэтому переход не доказан.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «deadline lifecycle игры достигнут без требуемого состояния».</summary>
    /// <remarks>
    /// Отказ означает, что mutation была выполнена, но требуемое состояние не доказано в пределах
    /// deadline: это не доказательство невозможности перехода, поэтому повтор имеет смысл.
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, на котором выполнялась mutation.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="state">Наблюдённое состояние игры на момент достижения deadline.</param>
    /// <param name="targetState">Требуемое состояние игры.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    /// <param name="mutationExitCode">Код выхода последней выполненной mutation игры.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLifecycleTimeout"/>.</returns>
    internal static ApplicationFailure LifecycleTimeout(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState state,
        AzurLaneGameState targetState,
        TimeSpan elapsed,
        int mutationExitCode)
    {
        Dictionary<string, string> details = LifecycleDetails(endpoint, package, state, elapsed);
        details[TargetStateDetailKey] = AndroidNames.GameStateName(targetState);
        details[ExitCodeDetailKey] = AndroidNames.Count(mutationExitCode);

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLifecycleTimeout,
            Message = $"Игра {package} на endpoint-е {endpoint} не достигла состояния "
                + $"{AndroidNames.GameStateName(targetState)} в пределах deadline.",
            IsRetryable = true,
            Details = details,
        };
    }

    /// <summary>Создаёт отказ отмены операции lifecycle игры.</summary>
    /// <param name="endpoint">Точный endpoint, на котором выполнялась операция.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="state">Наблюдённое состояние игры на момент отмены.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    /// <param name="phase">Имя фазы операции, в которой пришла отмена.</param>
    /// <returns>Отказ с существующим кодом <see cref="ApplicationFailure.OperationCancelled"/>.</returns>
    internal static ApplicationFailure Cancelled(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState state,
        TimeSpan elapsed,
        string phase)
    {
        Dictionary<string, string> details = LifecycleDetails(endpoint, package, state, elapsed);
        details[PhaseDetailKey] = phase;

        return new ApplicationFailure
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = $"Операция над игрой {package} на endpoint-е {endpoint} отменена.",
            Details = details,
        };
    }

    /// <summary>
    /// Создаёт отказ отмены операции Android, которая не относится к конкретному пакету.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Это та же форма отмены, что у <see cref="Cancelled"/>: тот же код
    /// <see cref="ApplicationFailure.OperationCancelled"/> и та же семантика. Новый код здесь не
    /// заводится, и второй перечень кодов не появляется — коды берутся из констант
    /// <see cref="ApplicationFailure"/>.
    /// </para>
    /// <para>
    /// Отдельная фабрика нужна потому, что у этой формы нет ключа <c>package</c>: операция готовности
    /// наблюдается до всякой работы с пакетом, поэтому подстановка «какого-нибудь» пакета сообщила бы в
    /// details недостоверный факт о том, что операция относилась к пакету игры. Состав ключей details
    /// поэтому ограничен точным endpoint-ом, недоказанным состоянием игры, фазой операции и затраченным
    /// временем.
    /// </para>
    /// <para>
    /// Форма отказа живёт здесь, у единственного владельца: orchestration только вызывает её, а не
    /// задаёт собственный перечень кодов, сообщений и ключей details.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">Точный endpoint, на котором выполнялась операция.</param>
    /// <param name="state">Недоказанное состояние игры на момент отмены.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    /// <param name="phase">Имя фазы операции, в которой пришла отмена.</param>
    /// <returns>Отказ с существующим кодом <see cref="ApplicationFailure.OperationCancelled"/>.</returns>
    internal static ApplicationFailure CancelledWithoutPackage(
        AndroidEndpoint endpoint,
        AzurLaneGameState state,
        TimeSpan elapsed,
        string phase)
    {
        // Пакет здесь намеренно не участвует: ключ package отсутствует, потому что операция
        // готовности пакета не запрашивала.
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [StateDetailKey] = AndroidNames.GameStateName(state),
            [PhaseDetailKey] = phase,
            [ElapsedMillisecondsDetailKey] = AndroidNames.Count((long)elapsed.TotalMilliseconds),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = $"Операция готовности Android на endpoint-е {endpoint} отменена.",
            Details = details,
        };
    }

    private static Dictionary<string, string> LifecycleDetails(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState state,
        TimeSpan elapsed)
        => new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = AndroidNames.GameStateName(state),
            [ElapsedMillisecondsDetailKey] = AndroidNames.Count((long)elapsed.TotalMilliseconds),
        };
}
