using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AzurPilot.TuiHarness;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class PhysicalProcessLifetimeTests
{
    [Fact]
    public async Task ForcedOwnerExit_ActualHelperAndJobChildExitWhileUnrelatedProcessSurvives()
    {
        using SafeFileHandle ownerJob = ConPty.CreateJob(denyChildren: true);
        using Process owner = StartControlledProcess(ownerJob);
        using SafeFileHandle unrelatedJob = ConPty.CreateJob(denyChildren: true);
        using Process unrelated = StartControlledProcess(unrelatedJob);
        using SafeFileHandle helperJob = ConPty.CreateJob(denyChildren: false);
        string root = Path.Combine(Path.GetTempPath(), "azurpilot-owner-exit-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string recordPath = Path.Combine(root, "processes.json");
        string probe = Path.Combine(AppContext.BaseDirectory, "native-absence-probe", "AzurPilot.NativeAbsenceProbe.exe");
        using Process helper = ConPty.StartInJob(probe,
            ["--physical-owner-lifetime", owner.Id.ToString(CultureInfo.InvariantCulture),
                PhysicalSession.ReadProcessPath(owner), owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                recordPath], root, helperJob);
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!File.Exists(recordPath) && !helper.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(recordPath), "Helper должен запустить и сообщить принадлежащий Job процесс.");
            using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
            using Process app = Process.GetProcessById(record.RootElement.GetProperty("appPid").GetInt32());
            _ = app.SafeHandle;
            Assert.Equal(helper.Id, record.RootElement.GetProperty("helperPid").GetInt32());
            Assert.False(app.HasExited);

            owner.Kill(entireProcessTree: false);
            await helper.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await app.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(0, helper.ExitCode);
            Assert.True(app.HasExited);
            Assert.False(unrelated.HasExited);
            Assert.True(File.Exists(recordPath));
        }
        finally
        {
            helperJob.Dispose();
            ownerJob.Dispose();
            unrelatedJob.Dispose();
            await helper.WaitForExitAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("cancel")]
    [InlineData("capture-failure")]
    [InlineData("owner-exit")]
    public async Task OwnedAppLifetime_ClosesActualJobAndProcessOnEveryExitPath(string scenario)
    {
        using SafeFileHandle ownerJob = ConPty.CreateJob(denyChildren: true);
        using Process owner = StartControlledProcess(ownerJob);
        using SafeFileHandle appJob = ConPty.CreateJob(denyChildren: true);
        string exitSignal = Path.Combine(Path.GetTempPath(), "azurpilot-normal-exit-" + Guid.NewGuid().ToString("N"));
        using Process app = StartControlledProcess(appJob, scenario == "normal" ? exitSignal : null);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task lifetime = PhysicalSession.WaitForOwnedAppAsync(app, owner, appJob, cancellation.Token);
        Assert.False(app.HasExited);
        Assert.False(owner.HasExited);
        try
        {
            switch (scenario)
            {
                case "normal":
                    await File.WriteAllTextAsync(exitSignal, "exit", TestContext.Current.CancellationToken);
                    break;
                case "owner-exit":
                    owner.Kill();
                    break;
                case "capture-failure":
                    // Ошибка управляющего пути закрывает Job, который уже наблюдает helper.
                    appJob.Dispose();
                    break;
                default:
                    await cancellation.CancelAsync();
                    break;
            }

            if (scenario == "cancel")
            {
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime);
            }
            else
            {
                await lifetime.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            Assert.True(app.HasExited);
            Assert.True(appJob.IsClosed);
            if (scenario == "normal") { Assert.Equal(23, app.ExitCode); }

            if (scenario != "owner-exit") { Assert.False(owner.HasExited); }
        }
        finally
        {
            appJob.Dispose();
            ownerJob.Dispose();
            await app.WaitForExitAsync(TestContext.Current.CancellationToken);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            File.Delete(exitSignal);
        }
    }

    [Fact]
    public void Cleanup_RejectsSameExecutableWithDifferentCreationIdentity()
    {
        using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
        using Process process = StartControlledProcess(job);

        string result = PhysicalSession.KillOwnedProcess(process.Id, PhysicalSession.ReadProcessPath(process),
            process.StartTime.ToUniversalTime().Ticks + 1);

        Assert.StartsWith("BLOCKED:", result, StringComparison.Ordinal);
        Assert.False(process.HasExited);
    }

    [Fact]
    public void Cleanup_RejectsUnknownIdentityAndWrongPathWithoutTerminatingProcess()
    {
        using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
        using Process process = StartControlledProcess(job);
        string path = PhysicalSession.ReadProcessPath(process);

        Assert.StartsWith("BLOCKED:", PhysicalSession.KillOwnedProcess(process.Id, path, 0), StringComparison.Ordinal);
        Assert.StartsWith("BLOCKED:", PhysicalSession.KillOwnedProcess(process.Id, Path.Combine(Path.GetTempPath(), "foreign.exe"),
            process.StartTime.ToUniversalTime().Ticks), StringComparison.Ordinal);
        Assert.Equal("NOT_TESTED", PhysicalSession.KillOwnedProcess(process.Id, "relative.exe", 0));
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task Cleanup_TerminatesOnlyConfirmedProcessAndAcceptsItsDisappearance()
    {
        using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
        using Process process = StartControlledProcess(job);
        int pid = process.Id;
        string path = PhysicalSession.ReadProcessPath(process);
        long startTime = process.StartTime.ToUniversalTime().Ticks;

        Assert.Equal("PASS", PhysicalSession.KillOwnedProcess(pid, path, startTime));
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.Dispose();
        Assert.Equal("PASS:already-exited", PhysicalSession.KillOwnedProcess(pid, path, startTime));
    }

    private static Process StartControlledProcess(SafeFileHandle job, string? exitSignal = null)
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = exitSignal is null ? "Start-Sleep -Seconds 60"
            : "while (-not [IO.File]::Exists('" + exitSignal.Replace("'", "''", StringComparison.Ordinal)
                + "')) { Start-Sleep -Milliseconds 20 }; exit 23";
        return ConPty.StartInJob(shell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            Path.GetTempPath(), job);
    }
}
