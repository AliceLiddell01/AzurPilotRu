namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Результат фазы остановки игры: наблюдение, которым доказана остановка, и bounded evidence выполненных
/// mutation.
/// </summary>
/// <remarks>
/// <para>
/// Mutation evidence отделено от наблюдения, потому что код выхода команды ADB — evidence, а не
/// доказательство: остановку доказывает <see cref="Observation"/>, а <see cref="MutationEvidence"/>
/// сообщает, что именно было выполнено (или что mutation не выполнялась вовсе).
/// </para>
/// <para>
/// Значение внутреннее: фаза остановки — часть lifecycle-операции, а не самостоятельный публичный
/// результат. Наружу отдаётся <see cref="AzurLaneGameLifecycleOutcome"/> с доказанным postcondition.
/// </para>
/// </remarks>
/// <param name="Observation">Наблюдение, доказавшее остановку игры.</param>
/// <param name="MutationEvidence">Bounded evidence выполненных mutation остановки.</param>
/// <param name="MutationExitCode">
/// Код выхода выполненной mutation остановки; <c>0</c>, если mutation не выполнялась, потому что игра была
/// остановлена уже на входе. Значение сохраняется отдельно от evidence, потому что его читает отказ
/// исчерпанного бюджета перезапуска: сообщать там нулевой код вместо фактического значило бы утверждать,
/// что mutation прошла успешно.
/// </param>
internal sealed record AzurLaneStopPhase(
    AzurLaneGameObservation Observation,
    string MutationEvidence,
    int MutationExitCode);
