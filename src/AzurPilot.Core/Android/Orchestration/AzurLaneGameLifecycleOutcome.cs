namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Итог lifecycle-операции игры Azur Lane с доказанным postcondition.
/// </summary>
/// <remarks>
/// <para>
/// Значение возвращается только тогда, когда требуемое состояние доказано наблюдением:
/// <see cref="FinalState"/> — выведенное состояние игры, а не код выхода команды ADB. Код выхода
/// остаётся evidence в <see cref="Evidence"/> и доказательством не является.
/// </para>
/// <para>
/// <see cref="InitialState"/> описывает вход операции, а не промежуточный результат её фаз: у перезапуска
/// это состояние, наблюдённое до начала операции, поэтому оно не подменяется состоянием после остановки.
/// </para>
/// <para>
/// <see cref="MutationEvidence"/> — bounded evidence фактически выполненных mutation в порядке выполнения;
/// у перезапуска здесь перечислены все mutation обеих фаз, включая принудительную остановку, а у
/// операции, завершившейся без mutation, — <c>none</c>.
/// </para>
/// <para>
/// <see cref="Launcher"/> заполнен тогда, когда операция разрешила launcher-компонент пакета: у операции
/// запуска и у перезапуска запущенной игры это адрес mutation запуска, а у операции, которой компонент
/// не понадобился, он равен <see langword="null"/>. Компонент — адрес mutation и диагностический факт, а
/// не доказательство postcondition: после запуска launcher-компонента на переднем плане может оказаться
/// другая activity того же пакета, поэтому postcondition доказывается пакетом.
/// </para>
/// </remarks>
/// <param name="Operation">Запрошенная lifecycle-операция: <c>start</c>, <c>stop</c> или <c>restart</c>.</param>
/// <param name="Endpoint">Точный endpoint, над которым выполнена операция.</param>
/// <param name="Package">Идентификатор пакета игры, над которым выполнена операция.</param>
/// <param name="InitialState">Состояние игры, наблюдённое на входе операции.</param>
/// <param name="FinalState">Доказанное состояние игры, которым операция завершилась.</param>
/// <param name="MutationEvidence">
/// Bounded evidence всех фактически выполненных mutation в порядке выполнения.
/// </param>
/// <param name="Launcher">Разрешённый launcher-компонент либо <see langword="null"/>, если он не разрешался.</param>
/// <param name="Evidence">Bounded однострочный evidence достигнутого postcondition.</param>
/// <param name="Elapsed">Затраченное время операции.</param>
public sealed record AzurLaneGameLifecycleOutcome(
    string Operation,
    AndroidEndpoint Endpoint,
    AndroidPackageId Package,
    AzurLaneGameState InitialState,
    AzurLaneGameState FinalState,
    string MutationEvidence,
    AndroidComponent? Launcher,
    string Evidence,
    TimeSpan Elapsed)
{
    /// <summary>Собирает успешный итог lifecycle-операции игры.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <param name="endpoint">Точный endpoint, над которым выполнена операция.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="initialState">Состояние игры, наблюдённое на входе операции.</param>
    /// <param name="finalObservation">Наблюдение, доказавшее требуемое состояние игры.</param>
    /// <param name="mutationEvidence">Bounded evidence всех фактически выполненных mutation.</param>
    /// <param name="launcher">Разрешённый launcher-компонент либо <see langword="null"/>.</param>
    /// <param name="elapsed">Затраченное время операции.</param>
    /// <returns>Итог с доказанным состоянием игры.</returns>
    internal static AzurLaneGameLifecycleOutcome Proven(
        string operation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AzurLaneGameState initialState,
        AzurLaneGameObservation finalObservation,
        string mutationEvidence,
        AndroidComponent? launcher,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(finalObservation);

        return new AzurLaneGameLifecycleOutcome(
            operation,
            endpoint,
            package,
            initialState,
            finalObservation.State,
            mutationEvidence,
            launcher,
            AndroidEvidence.Compose(mutationEvidence, finalObservation.Evidence),
            elapsed);
    }
}
