namespace AzurPilot.TuiHarness;

// Общие бюджеты CLI, IPC и внешнего проверяющего процесса. verify.ps1 читает их через help.
internal static class HarnessTimeouts
{
    internal const int StartConnectionMs = 90_000;
    internal const int StartupResponseMs = 1_000;
    internal const int ResponseGraceMs = 10_000;
    internal const int CleanupMs = 5_000;
    internal const int CommandProcessMs = 45_000;
    internal const int MinimumRequestTimeoutMs = 100;
    internal const int DefaultRequestTimeoutMs = 15_000;
    internal const int MaximumRequestTimeoutMs = 30_000;
    internal const int MinimumLifetimeSeconds = 30;
    internal const int DefaultLifetimeSeconds = 600;
    internal const int MaximumLifetimeSeconds = 900;
    internal const int StartupCompletionMs = StartConnectionMs + StartupResponseMs + ResponseGraceMs;
    internal const int StartProcessMs = StartupCompletionMs + CleanupMs + 5_000;
    internal const int ScenarioProcessMs = StartProcessMs + MaximumLifetimeSeconds * 1000
        + MaximumRequestTimeoutMs + ResponseGraceMs + DefaultRequestTimeoutMs + ResponseGraceMs;

    // Часы подменяются только в тесте дедлайна; продуктовый код использует тот же автомат ожидания.
    internal static async Task<bool> StartupCompletedAsync(Task ready, Task clientExited, TimeProvider timeProvider)
    {
        using CancellationTokenSource stop = new();
        Task deadline = Task.Delay(TimeSpan.FromMilliseconds(StartupCompletionMs), timeProvider, stop.Token);
        Task completed = await Task.WhenAny(ready, clientExited, deadline);
        await stop.CancelAsync();
        return completed == ready && ready.IsCompletedSuccessfully;
    }
}
