namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Наблюдение игры Azur Lane на точном endpoint-е: выведенное состояние и факты, которыми оно доказано.
/// </summary>
/// <remarks>
/// <para>
/// Факты сохраняются рядом с состоянием, потому что разные lifecycle-операции доказывают разный
/// postcondition: остановка доказывается отсутствием процесса и отсутствием игры на переднем плане, а
/// запуск — наличием процесса и игрой на переднем плане. Вывод состояния из фактов живёт у своего
/// владельца и здесь не повторяется.
/// </para>
/// <para>
/// <see cref="Evidence"/> — bounded однострочные сведения о наблюдении: полный вывод ADB, полный
/// <c>dumpsys</c> и полный список процессов устройства в него не попадают.
/// </para>
/// </remarks>
/// <param name="State">Состояние игры, выведенное из наблюдённых фактов.</param>
/// <param name="Facts">Независимые факты, которыми доказано состояние.</param>
/// <param name="Evidence">Bounded однострочный evidence наблюдения.</param>
public sealed record AzurLaneGameObservation(
    AzurLaneGameState State,
    AzurLaneGameFacts Facts,
    string Evidence);
