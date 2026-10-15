namespace AzurPilot.App;

/// <summary>Передаёт этап в порядке вызовов orchestration без отложенных callbacks в UI context.</summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
