namespace AzurPilot.Core.MuMu;

/// <summary>
/// Доказанное host-side состояние Android-экземпляра MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Состояние относится к конкретному экземпляру MuMu, а не к эмулятору целиком: факт существования
/// «какого-то» процесса MuMu доказательством состояния экземпляра не является.
/// </para>
/// <para>
/// <see cref="Unknown"/> — это отсутствие доказательства, а не «вероятно остановлен»: lifecycle-операция,
/// требующая перехода, выполняет mutation и доказывает postcondition наблюдением авторитетного
/// состояния.
/// </para>
/// </remarks>
public enum MuMuLifecycleState
{
    /// <summary>Состояние экземпляра не доказано.</summary>
    Unknown = 0,

    /// <summary>Экземпляр доказанно остановлен.</summary>
    Stopped = 1,

    /// <summary>Экземпляр доказанно запущен.</summary>
    Running = 2,
}
