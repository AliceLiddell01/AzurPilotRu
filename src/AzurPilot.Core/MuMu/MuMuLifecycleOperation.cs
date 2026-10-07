namespace AzurPilot.Core.MuMu;

/// <summary>
/// Запрошенная lifecycle-операция над Android-экземпляром MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Операция выражает требуемый postcondition, а не конкретную команду control utility: какой именно
/// набор mutation приводит к нему, определяет orchestration
/// <see cref="MuMuLifecycleService"/> через host-примитив <see cref="MuMuLifecycleMutation"/>.
/// </para>
/// <para>
/// <see cref="Restart"/> — операция сервиса, а не примитив host-а: её postcondition доказывается
/// композицией mutation, а не отдельной веткой реализации host-а.
/// </para>
/// </remarks>
public enum MuMuLifecycleOperation
{
    /// <summary>Запустить экземпляр и доказать состояние <see cref="MuMuLifecycleState.Running"/>.</summary>
    Start = 0,

    /// <summary>Остановить экземпляр и доказать состояние <see cref="MuMuLifecycleState.Stopped"/>.</summary>
    Stop = 1,

    /// <summary>Перезапустить экземпляр и доказать состояние <see cref="MuMuLifecycleState.Running"/>.</summary>
    Restart = 2,
}
