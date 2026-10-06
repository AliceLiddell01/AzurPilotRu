namespace AzurPilot.Core.MuMu;

/// <summary>
/// Авторитетное наблюдение host-side состояния конкретного Android-экземпляра MuMu.
/// </summary>
/// <remarks>
/// Наблюдение относится к запрошенному экземпляру, а не к «какому-то запущенному процессу MuMu».
/// <see cref="Evidence"/> — bounded сведения о том, чем подтверждено наблюдение; полный вывод control
/// utility в них не попадает и в логи не пишется.
/// </remarks>
/// <param name="State">Доказанное состояние экземпляра.</param>
/// <param name="Evidence">Bounded evidence наблюдения, пригодный для диагностики.</param>
public sealed record MuMuInstanceState(MuMuLifecycleState State, string Evidence);
