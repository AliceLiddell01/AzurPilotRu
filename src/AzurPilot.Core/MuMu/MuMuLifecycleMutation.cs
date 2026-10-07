namespace AzurPilot.Core.MuMu;

/// <summary>
/// Примитив host-side поверхности MuMu: однократная mutation одного экземпляра.
/// </summary>
/// <remarks>
/// <para>
/// Тип описывает ровно то, что умеет выполнить host: запуск или остановку конкретного экземпляра. Он
/// используется только в <see cref="IMuMuHost"/> и не является публичной операцией сервиса.
/// </para>
/// <para>
/// Перезапуск примитивом не является: orchestration доказывает его композицией mutation, поэтому
/// реализация host-а не обязана поддерживать ветку, которую production никогда не вызывает.
/// </para>
/// </remarks>
public enum MuMuLifecycleMutation
{
    /// <summary>Запустить экземпляр.</summary>
    Start = 0,

    /// <summary>Остановить экземпляр.</summary>
    Stop = 1,
}
