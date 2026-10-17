namespace AzurPilot.TuiHarness;

// Общие бюджеты CLI, IPC и внешнего проверяющего процесса. verify.ps1 читает их через help.
internal static class HarnessTimeouts
{
    internal const int StartupResponseMs = 1_000;
    internal const int ResponseGraceMs = 10_000;
    internal const int CleanupMs = 5_000;
    internal const int CommandProcessMs = 45_000;
    internal const int ConsoleProfileBootstrapMs = 10_000;
    internal const int ConsoleProfileReleaseMs = 2_000;
    internal const int LivePreflightProcessMs = 20_000;
    internal const int LivePreflightAgentMs = 15_000;
    internal const int PlaywrightDriverStartupMs = 10_000;
    internal const int ChromiumLaunchMs = 20_000;
    internal const int PlaywrightDefaultTimeoutMs = 10_000;
    internal const int RendererNewPageMs = 10_000;
    internal const int RendererRouteMs = 10_000;
    internal const int RendererSetContentMs = 10_000;
    internal const int RendererAddStyleMs = 10_000;
    internal const int RendererAddScriptMs = 10_000;
    internal const int RendererExposeFunctionMs = 10_000;
    internal const int RendererInitializeMs = 10_000;
    internal const int RendererSetViewportMs = 10_000;
    internal const int RendererStartupWaitMs = 15_000;
    internal const int SourceIdentityGitMs = 5_000;
    internal const int SourceIdentityGitCommandCount = 4;
    internal const int StartupMarginMs = 10_000;
    internal const int StartProcessMarginMs = 5_000;
    internal const int MinimumRequestTimeoutMs = 100;
    internal const int DefaultRequestTimeoutMs = 15_000;
    internal const int MaximumRequestTimeoutMs = 30_000;
    internal const int MinimumLifetimeSeconds = 30;
    internal const int DefaultLifetimeSeconds = 600;
    internal const int MaximumLifetimeSeconds = 900;

    internal const int ConsoleProfileBudgetMs = ConsoleProfileBootstrapMs + ConsoleProfileReleaseMs + CleanupMs;
    internal const int LivePreflightBudgetMs = LivePreflightProcessMs + CleanupMs;
    internal const int RendererCreationBudgetMs = PlaywrightDriverStartupMs + ChromiumLaunchMs
        + RendererNewPageMs + RendererRouteMs + RendererSetContentMs + RendererAddStyleMs
        + RendererAddScriptMs * 3 + RendererExposeFunctionMs + RendererInitializeMs + RendererSetViewportMs;
    internal const int SourceIdentityBudgetMs = SourceIdentityGitCommandCount * (SourceIdentityGitMs + CleanupMs);
    internal const int StartConnectionMs = ConsoleProfileBudgetMs + LivePreflightBudgetMs + RendererCreationBudgetMs
        + SourceIdentityBudgetMs + RendererStartupWaitMs + StartupMarginMs;
    internal const int StartupCompletionMs = StartConnectionMs + StartupResponseMs + ResponseGraceMs;
    internal const int StartProcessMs = StartupCompletionMs + CleanupMs + StartProcessMarginMs;
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
