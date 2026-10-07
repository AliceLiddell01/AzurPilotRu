namespace AzurPilot.Core.MuMu;

/// <summary>
/// Результат однократного вызова control utility MuMu.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExitCode"/> — код выхода процесса control utility. Он не является доказательством успеха:
/// успех lifecycle-операции определяется только доказанным postcondition, а код выхода остаётся
/// bounded evidence. Ненулевой код выхода сам по себе тоже не отказ, если postcondition доказан.
/// </para>
/// <para>
/// <see cref="BoundedOutput"/> — ограниченный вывод control utility, пригодный для диагностики. Полный
/// stdout/stderr в него не попадает, и orchestration не пишет его в логи.
/// </para>
/// </remarks>
/// <param name="ExitCode">Код выхода процесса control utility.</param>
/// <param name="BoundedOutput">Bounded вывод control utility.</param>
public sealed record MuMuLifecycleCommandOutcome(int ExitCode, string BoundedOutput);
