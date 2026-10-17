using System.Diagnostics;
using System.Text;
using AzurPilot.TuiHarness;
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
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
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
            Assert.False(File.Exists(marker), "Job должен блокировать child до исполнения команды записи.");
            Assert.Contains("CHILD_DENIED", output, StringComparison.Ordinal);
            Assert.DoesNotContain("unexpected-child\r\n", output, StringComparison.Ordinal);
        }

        _ = Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }
}
