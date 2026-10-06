using System.Globalization;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Фабрики ожидаемых MuMu-отказов с bounded structured details.
/// </summary>
/// <remarks>
/// <para>
/// Единственный владелец того, какие MuMu-отказы синтезирует Core: коды берутся из констант
/// <see cref="ApplicationFailure"/>, а не из строковых литералов, и ожидаемые MuMu-отказы никогда не
/// подменяются <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// <para>
/// Здесь синтезируются только отказы выбора экземпляра и отказы lifecycle, которые orchestration
/// устанавливает сама. Ожидаемые MuMu-отказы, возвращённые host-ом, не пересоздаются: они пробрасываются
/// без изменений, поэтому их точный код и details сохраняются.
/// </para>
/// <para>
/// Единственное исключение — отмена: это контракт отмены Core, а не MuMu-код. Отмена, замеченная
/// границей во время mutation, приводится к той же форме, что и отмена, проверенная orchestration до
/// mutation: код <see cref="ApplicationFailure.OperationCancelled"/> с фазой
/// <see cref="MutationPhase"/> и bounded details операции.
/// </para>
/// <para>
/// Details содержат только bounded полезное evidence: операцию, identity экземпляра, наблюдённое
/// состояние, целевое состояние, фазу, код выхода mutation и затраченное время. Полный stdout/stderr
/// control utility, пути установки и прочие machine-specific данные в details не попадают.
/// </para>
/// </remarks>
internal static class MuMuFailures
{
    /// <summary>Фаза отказа: ожидание process-local координации по identity экземпляра.</summary>
    internal const string GatePhase = "gate";

    /// <summary>Фаза отказа: начальное наблюдение состояния до mutation.</summary>
    internal const string InitialObservationPhase = "initial_observation";

    /// <summary>Фаза отказа: выполнение mutation.</summary>
    internal const string MutationPhase = "mutation";

    /// <summary>Фаза отказа: bounded polling авторитетного состояния.</summary>
    internal const string PollingPhase = "polling";

    private const string PostconditionObservationPhase = "postcondition_observation";

    private const string SelectionDetailKey = "selection";

    private const string InstanceIdDetailKey = "instance_id";

    private const string InstanceCountDetailKey = "instance_count";

    private const string CandidateIdsDetailKey = "candidate_ids";

    private const string OperationDetailKey = "operation";

    private const string StateDetailKey = "state";

    private const string TargetStateDetailKey = "target_state";

    private const string PhaseDetailKey = "phase";

    private const string ExitCodeDetailKey = "exit_code";

    private const string ElapsedMillisecondsDetailKey = "elapsed_ms";

    /// <summary>Создаёт отказ «экземпляр не найден».</summary>
    /// <param name="selectionMode">Режим выбора: <c>auto</c> или <c>explicit</c>.</param>
    /// <param name="requestedId">Явно запрошенная identity; для автоматического выбора — отсутствует.</param>
    /// <param name="instanceCount">Сколько экземпляров перечислила установка.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuInstanceNotFound"/>.</returns>
    internal static ApplicationFailure InstanceNotFound(
        string selectionMode,
        MuMuInstanceId? requestedId,
        int instanceCount)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [SelectionDetailKey] = selectionMode,
            [InstanceCountDetailKey] = Count(instanceCount),
        };

        if (requestedId is MuMuInstanceId id)
        {
            details[InstanceIdDetailKey] = id.ToString();
            return new ApplicationFailure
            {
                Code = ApplicationFailure.MuMuInstanceNotFound,
                Message = $"Android-экземпляр MuMu «{id}» не найден в установке: явный выбор по stable "
                    + "identity не разрешён.",
                Details = details,
            };
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuInstanceNotFound,
            Message = "В установке MuMu не найден ни один Android-экземпляр: автоматический выбор не "
                + "определён.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «автоматический выбор неоднозначен».</summary>
    /// <param name="selectionMode">Режим выбора: <c>auto</c>.</param>
    /// <param name="instances">Все экземпляры, перечисленные установкой.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuInstanceAmbiguous"/>.</returns>
    internal static ApplicationFailure InstanceAmbiguous(string selectionMode, IReadOnlyList<MuMuInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        string count = Count(instances.Count);
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [SelectionDetailKey] = selectionMode,
            [InstanceCountDetailKey] = count,
            [CandidateIdsDetailKey] = MuMuEvidence.Truncate(
                string.Join(",", instances.Select(static instance => instance.Id.ToString()))),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuInstanceAmbiguous,
            Message = $"Автоматический выбор Android-экземпляра MuMu не определён: в установке найдено "
                + $"{count} экземпляров, а автоматический выбор требует ровно один.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «deadline достигнут без нужного terminal state».</summary>
    /// <remarks>
    /// Отказ означает, что mutation была выполнена, но требуемое terminal state не наблюдалось за
    /// отведённое время: это не доказательство невозможности перехода, поэтому повтор имеет смысл.
    /// </remarks>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние на момент достижения deadline.</param>
    /// <param name="targetState">Требуемое terminal state.</param>
    /// <param name="elapsed">Затраченное время.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuLifecycleTimeout"/>.</returns>
    internal static ApplicationFailure LifecycleTimeout(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        MuMuLifecycleState targetState,
        TimeSpan elapsed,
        int mutationExitCode)
    {
        Dictionary<string, string> details = LifecycleDetails(operation, instanceId, state, elapsed);
        details[TargetStateDetailKey] = MuMuNames.StateName(targetState);
        details[ExitCodeDetailKey] = Count(mutationExitCode);

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuLifecycleTimeout,
            Message = $"MuMu instance {instanceId} не достиг состояния {MuMuNames.StateName(targetState)} "
                + $"в пределах deadline: операция {MuMuNames.OperationName(operation)} завершена без "
                + "доказанного postcondition.",
            IsRetryable = true,
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «postcondition не доказан».</summary>
    /// <remarks>
    /// Отказ синтезируется только тогда, когда mutation была действительно выполнена, но её код выхода
    /// сообщает отказ, а следующее авторитетное наблюдение не показывает требуемое terminal state:
    /// postcondition не доказан, и ждать deadline бессмысленно.
    /// </remarks>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние, в котором postcondition не достигнут.</param>
    /// <param name="elapsed">Затраченное время.</param>
    /// <param name="mutationExitCode">Код выхода выполненной mutation, сообщивший отказ.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.MuMuLifecyclePostconditionNotMet"/>.</returns>
    internal static ApplicationFailure LifecyclePostconditionNotMet(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        TimeSpan elapsed,
        int mutationExitCode)
    {
        Dictionary<string, string> details = LifecycleDetails(operation, instanceId, state, elapsed);
        details[PhaseDetailKey] = PostconditionObservationPhase;
        details[ExitCodeDetailKey] = Count(mutationExitCode);

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuLifecyclePostconditionNotMet,
            Message = $"MuMu instance {instanceId}: операция {MuMuNames.OperationName(operation)} не "
                + "привела к требуемому postcondition, поэтому переход не доказан.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ отмены lifecycle-операции.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="state">Наблюдённое состояние на момент отмены.</param>
    /// <param name="elapsed">Затраченное время.</param>
    /// <param name="phase">Фаза операции, в которой пришла отмена.</param>
    /// <returns>Отказ с существующим кодом <see cref="ApplicationFailure.OperationCancelled"/>.</returns>
    internal static ApplicationFailure Cancelled(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        TimeSpan elapsed,
        string phase)
    {
        Dictionary<string, string> details = LifecycleDetails(operation, instanceId, state, elapsed);
        details[PhaseDetailKey] = phase;

        return new ApplicationFailure
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = $"Операция {MuMuNames.OperationName(operation)} над MuMu instance {instanceId} отменена.",
            Details = details,
        };
    }

    private static Dictionary<string, string> LifecycleDetails(
        MuMuLifecycleOperation operation,
        MuMuInstanceId instanceId,
        MuMuLifecycleState state,
        TimeSpan elapsed)
        => new(StringComparer.Ordinal)
        {
            [OperationDetailKey] = MuMuNames.OperationName(operation),
            [InstanceIdDetailKey] = instanceId.ToString(),
            [StateDetailKey] = MuMuNames.StateName(state),
            [ElapsedMillisecondsDetailKey] = ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        };

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
