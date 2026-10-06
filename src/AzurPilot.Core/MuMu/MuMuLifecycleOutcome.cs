namespace AzurPilot.Core.MuMu;

/// <summary>
/// Итог lifecycle-операции над Android-экземпляром MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Итог несёт ровно то, что нужно диагностике и логированию: операцию, наблюдённое начальное состояние,
/// доказанное конечное состояние, bounded evidence и затраченное время. Полный вывод control utility в
/// итог не попадает.
/// </para>
/// <para>
/// <see cref="InitialState"/> — состояние, наблюдённое до mutation; при идемпотентном успехе оно же
/// является <see cref="FinalState"/>. <see cref="FinalState"/> — состояние, доказанное наблюдением после
/// операции, а не следствие кода выхода control utility.
/// </para>
/// <para>
/// <see cref="Elapsed"/> измерено стандартным <see cref="TimeProvider"/> orchestration и не является
/// доказательством чего-либо: доказательство — только <see cref="FinalState"/> и
/// <see cref="Evidence"/>.
/// </para>
/// </remarks>
/// <param name="Operation">Запрошенная lifecycle-операция.</param>
/// <param name="InitialState">Наблюдённое состояние экземпляра до mutation.</param>
/// <param name="FinalState">Доказанное состояние экземпляра после операции.</param>
/// <param name="Evidence">Bounded evidence достигнутого postcondition.</param>
/// <param name="Elapsed">Затраченное на операцию время.</param>
public sealed record MuMuLifecycleOutcome(
    MuMuLifecycleOperation Operation,
    MuMuLifecycleState InitialState,
    MuMuLifecycleState FinalState,
    string Evidence,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Identity экземпляра, над которым выполнена операция.
    /// </summary>
    /// <value>
    /// Задаётся orchestration при построении итога; отсутствует только у итога, собранного вручную
    /// вызывающей стороной.
    /// </value>
    public MuMuInstanceId? InstanceId { get; init; }
}
