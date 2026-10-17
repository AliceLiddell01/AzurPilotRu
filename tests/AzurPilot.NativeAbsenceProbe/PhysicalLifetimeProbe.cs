using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AzurPilot.TuiHarness;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.NativeAbsenceProbe;

/// <summary>Проверяет продуктовый механизм времени жизни helper с настоящим Job и управляемым дочерним процессом.</summary>
internal static class PhysicalLifetimeProbe
{
    internal static async Task<int> RunAsync(string ownerPid, string ownerPath, string ownerStartTime, string recordPath)
    {
        using Process owner = PhysicalSession.OpenOwnedProcess(int.Parse(ownerPid, CultureInfo.InvariantCulture),
            ownerPath, long.Parse(ownerStartTime, CultureInfo.InvariantCulture));
        using SafeFileHandle job = ConPty.CreateJob(denyChildren: true);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        using Process app = ConPty.StartInJob(shell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"],
            Path.GetTempPath(), job);
        string temporary = recordPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { appPid = app.Id, helperPid = Environment.ProcessId }));
        File.Move(temporary, recordPath);
        await PhysicalSession.WaitForOwnedAppAsync(app, owner, job, CancellationToken.None);
        return app.HasExited ? 0 : 1;
    }
}
