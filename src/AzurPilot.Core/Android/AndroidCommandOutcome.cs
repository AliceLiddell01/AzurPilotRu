namespace AzurPilot.Core.Android;

/// <summary>
/// Результат однократного вызова ADB.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExitCode"/> — код выхода процесса ADB. Он не является доказательством успеха действия:
/// успех подтверждается наблюдением требуемого состояния, а код выхода остаётся bounded evidence.
/// Ненулевой код выхода сам по себе тоже не отказ, если требуемое состояние доказано наблюдением.
/// </para>
/// <para>
/// <see cref="BoundedOutput"/> — ограниченный вывод ADB, пригодный для диагностики. Полный
/// stdout/stderr в него не попадает, и orchestration не пишет его в логи.
/// </para>
/// </remarks>
/// <param name="ExitCode">Код выхода процесса ADB.</param>
/// <param name="BoundedOutput">Bounded вывод ADB.</param>
public sealed record AndroidCommandOutcome(int ExitCode, string BoundedOutput);
