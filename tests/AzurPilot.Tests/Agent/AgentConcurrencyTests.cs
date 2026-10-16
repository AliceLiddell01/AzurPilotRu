using System.Diagnostics;
using AzurPilot.App;
using Xunit;

namespace AzurPilot.Tests.Agent;

// Тесты используют production LocalApplicationData; пустые lock-файлы сохраняются намеренно.
// Случайная identity снижает риск совпадения в параллельных прогонах, но не исключает его.
[Trait("Category", "Agent")]
public sealed class AgentConcurrencyTests
{
    [Theory]
    [InlineData("cli", "success")]
    [InlineData("tui", "success")]
    [InlineData("cli", "observe")]
    [InlineData("tui", "postcondition")]
    public async Task RealProcessesExcludeSameTargetAndAllowIndependentTarget(string holderRole, string phase)
    {
        string identity = Random.Shared.Next(100000, int.MaxValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string independent = (int.Parse(identity, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using AgentTestDirectory holderDirectory = new();
        using AgentTestDirectory contenderDirectory = new();
        using AgentTestDirectory independentDirectory = new();
        using Process holder = holderDirectory.StartProbe(holderRole, identity, phase);
        using Process neighbor = independentDirectory.StartProbe("cli", independent);
        try
        {
            await holderDirectory.WaitForMutationAsync();
            await independentDirectory.WaitForMutationAsync();
            using Process contender = contenderDirectory.StartProbe("cli", identity);
            using var busy = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(contender), AzurPilotExitCode.TargetBusy);
            Assert.Equal("busy", busy.RootElement.GetProperty("status").GetString());
            Assert.Equal("none", busy.RootElement.GetProperty("effect").GetString());
            Assert.False(File.Exists(Path.Combine(contenderDirectory.Root, "entered")));
            holderDirectory.Release();
            independentDirectory.Release();
            using var completed = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(holder), 0);
            using var independentCompleted = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(neighbor), 0);
            contenderDirectory.Release();
            using Process retry = contenderDirectory.StartProbe("cli", identity);
            using var retried = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(retry), 0);
            Assert.Equal("Running", retried.RootElement.GetProperty("data").GetProperty("finalState").GetString());
        }
        finally
        {
            await StopProbeAsync(holder);
            await StopProbeAsync(neighbor);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseIsReleasedAfterCancellationOrProcessDeath(bool terminate)
    {
        string identity = Random.Shared.Next(100000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using AgentTestDirectory ownerDirectory = new();
        using AgentTestDirectory retryDirectory = new();
        using Process owner = ownerDirectory.StartProbe("cli", identity, "cancel");
        try
        {
            await ownerDirectory.WaitForMutationAsync();
            if (terminate)
            {
                owner.Kill();
                await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                ownerDirectory.Release();
                using var cancelled = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(owner), AzurPilotExitCode.OperationCancelled);
                Assert.Equal("cancelled", cancelled.RootElement.GetProperty("status").GetString());
                Assert.Equal("mayHaveChanged", cancelled.RootElement.GetProperty("effect").GetString());
            }

            retryDirectory.Release();
            using Process retry = retryDirectory.StartProbe("cli", identity);
            using var result = AgentProcessTests.AssertEnvelope(await AgentProcessTests.ReadAsync(retry), 0);
            Assert.Equal("changed", result.RootElement.GetProperty("effect").GetString());
        }
        finally
        {
            await StopProbeAsync(owner);
        }
    }

    private static async Task StopProbeAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }
}
