using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AzurPilot.TuiHarness;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class ConPtyTests
{
    [Fact]
    public void NativeLayoutsMatchWindowsX64Abi()
    {
        Assert.Equal((4, 104, 112, 144), ConPty.NativeLayoutSizes());
    }

    [Fact]
    public async Task SafeJobDeniesChildCreationAndClosesProcess()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string shell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        string marker = Path.Combine(Path.GetTempPath(), "azurpilot-job-" + Guid.NewGuid().ToString("N"));
        int pid;
        string output;
        string script = "$p = New-Object System.Diagnostics.ProcessStartInfo; $p.FileName = '"
            + Path.Combine(system, "cmd.exe").Replace("'", "''", StringComparison.Ordinal)
            + "'; $p.UseShellExecute = $false; $p.Arguments = '/d /c echo unexpected-child>\""
            + marker.Replace("'", "''", StringComparison.Ordinal)
            + "\"'; try { $c = [Diagnostics.Process]::Start($p); $c.WaitForExit(); [Console]::Write('CHILD_STARTED') } catch { [Console]::Write('CHILD_DENIED') }; [Console]::ReadLine()";
        await using (ConPty pty = new(shell,
            "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            Path.GetTempPath(), 80, 25, safe: true))
        {
            pid = pty.Pid;
            StringBuilder text = new();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await foreach ((long _, byte[] bytes) in pty.Output.Reader.ReadAllAsync(deadline.Token))
                {
                    _ = text.Append(Encoding.UTF8.GetString(bytes));
                    if (text.ToString().Contains("CHILD_DENIED", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested) { }

            output = text.ToString();
            Assert.True(pty.Alive);
            Assert.False(File.Exists(marker), "Job должен блокировать дочерний процесс до исполнения команды записи.");
            Assert.Contains("CHILD_DENIED", output, StringComparison.Ordinal);
            Assert.DoesNotContain("unexpected-child\r\n", output, StringComparison.Ordinal);
        }

        _ = Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public void StartInJob_TerminatesSuspendedProcessWhenAssignmentFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpilot-job-start-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string processName = "azurpilot-job-start-" + Guid.NewGuid().ToString("N");
        string shell = Path.Combine(root, processName + ".exe");
        string marker = Path.Combine(root, "started.txt");

        try
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), shell);
            using SafeFileHandle invalidJob = new(new nint(-1), ownsHandle: false);

            Win32Exception exception = Assert.Throws<Win32Exception>(() => ConPty.StartInJob(shell,
                ["/d", "/c", "echo started>\"" + marker + "\""], root, invalidJob));

            Assert.Equal(6, exception.NativeErrorCode);
            Assert.False(File.Exists(marker), "Процесс не должен выполниться до назначения в Job.");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    process.Dispose();
                }

                return processes.Length == 0;
            }, TimeSpan.FromSeconds(5)), "При ошибке назначения приостановленный процесс должен быть завершён.");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartInJob_PreservesExitCodeForShortLivedProcess()
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpilot-job-exit-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        try
        {
            using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
            using Process process = ConPty.StartInJob(shell, ["/d", "/c", "exit 23"], root, job);

            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(23, process.ExitCode);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartInJob_AssignsProcessBeforeRunningAndPreservesQuotedArguments()
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpilot job start-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        string marker = Path.Combine(root, "started.txt");
        string script = Path.Combine(root, "write marker.ps1");
        File.WriteAllText(script, "[System.IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal)
            + "', 'started')");

        try
        {
            using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
            using Process app = ConPty.StartInJob(shell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script], root, job);

            Assert.True(ConPty.IsProcessInJob(app.SafeHandle, job));
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await app.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            Assert.Equal(0, app.ExitCode);
            Assert.Equal("started", File.ReadAllText(marker));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        IOException? lastFailure = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastFailure = new IOException(exception.Message, exception);
            }

            Thread.Sleep(50);
        }

        throw new IOException("Не удалось удалить временный каталог теста после завершения процесса.", lastFailure);
    }
}
