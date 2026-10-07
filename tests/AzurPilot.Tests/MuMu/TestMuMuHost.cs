using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Tests.MuMu;

/// <summary>Один запрос mutation: экземпляр и примитив.</summary>
/// <param name="InstanceId">Identity экземпляра, для которого запрошена mutation.</param>
/// <param name="Mutation">Запрошенная mutation.</param>
internal sealed record MuMuMutationRequest(MuMuInstanceId InstanceId, MuMuLifecycleMutation Mutation);

/// <summary>
/// Управляемый test double host-side поверхности MuMu.
/// </summary>
/// <remarks>
/// Double отвечает только за host-примитивы: тесты вызывают настоящий
/// <see cref="MuMuLifecycleService"/>, поэтому проверяются реальный bounded polling, deadline,
/// композиция restart и process-local сериализация, а не отдельная реализация orchestration.
/// </remarks>
internal sealed class TestMuMuHost : IMuMuHost
{
    private readonly Lock _sync = new();
    private readonly List<MuMuMutationRequest> _mutations = [];

    private int _observationCount;
    private int _activeMutations;
    private int _maxConcurrentMutations;

    internal TestMuMuHost()
    {
        InstallationResult = ApplicationResult<MuMuInstallation>.Success(
            new MuMuInstallation("test-version", "test-install-root", "test-control-utility"));
        InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);
        ObservationResult = ApplicationResult<MuMuInstanceState>.Success(
            new MuMuInstanceState(MuMuLifecycleState.Unknown, "test-observation"));
    }

    /// <summary>Ответ обнаружения установки.</summary>
    internal ApplicationResult<MuMuInstallation> InstallationResult { get; set; }

    /// <summary>Ответ перечисления экземпляров.</summary>
    internal ApplicationResult<IReadOnlyList<MuMuInstance>> InstancesResult { get; set; }

    /// <summary>Ответ наблюдения состояния, если не задан <see cref="ObservationHandler"/>.</summary>
    internal ApplicationResult<MuMuInstanceState> ObservationResult { get; set; }

    /// <summary>Наблюдение состояния в зависимости от экземпляра.</summary>
    internal Func<MuMuInstanceId, ApplicationResult<MuMuInstanceState>>? ObservationHandler { get; set; }

    /// <summary>Управляемое поведение mutation; по умолчанию — успех с нулевым кодом выхода.</summary>
    internal Func<MuMuMutationRequest, ApplicationResult<MuMuLifecycleCommandOutcome>>? MutationHandler { get; set; }

    /// <summary>Сколько раз запрашивалось наблюдение состояния.</summary>
    internal int ObservationCount
    {
        get
        {
            lock (_sync)
            {
                return _observationCount;
            }
        }
    }

    /// <summary>Сколько раз запрашивалась mutation.</summary>
    internal int MutationCount
    {
        get
        {
            lock (_sync)
            {
                return _mutations.Count;
            }
        }
    }

    /// <summary>Наибольшее число одновременно выполнявшихся mutation.</summary>
    internal int MaxConcurrentMutations
    {
        get
        {
            lock (_sync)
            {
                return _maxConcurrentMutations;
            }
        }
    }

    /// <summary>Запрошенные mutation в порядке запроса.</summary>
    internal IReadOnlyList<MuMuMutationRequest> MutationRequests
    {
        get
        {
            lock (_sync)
            {
                return [.. _mutations];
            }
        }
    }

    public ApplicationResult<MuMuInstallation> DiscoverInstallation() => InstallationResult;

    public ApplicationResult<IReadOnlyList<MuMuInstance>> EnumerateInstances(MuMuInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return InstancesResult;
    }

    public ApplicationResult<MuMuInstanceState> ObserveInstanceState(
        MuMuInstallation installation,
        MuMuInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(installation);

        lock (_sync)
        {
            _observationCount++;
        }

        return ObservationHandler is null ? ObservationResult : ObservationHandler(instance);
    }

    public ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(
        MuMuInstallation installation,
        MuMuInstanceId instance,
        MuMuLifecycleMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        MuMuMutationRequest request = new(instance, mutation);
        lock (_sync)
        {
            _mutations.Add(request);
            _activeMutations++;
            _maxConcurrentMutations = Math.Max(_maxConcurrentMutations, _activeMutations);
        }

        try
        {
            return MutationHandler is null
                ? ApplicationResult<MuMuLifecycleCommandOutcome>.Success(
                    new MuMuLifecycleCommandOutcome(0, "test-control-output"))
                : MutationHandler(request);
        }
        finally
        {
            lock (_sync)
            {
                _activeMutations--;
            }
        }
    }
}
