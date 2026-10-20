using System.Runtime.InteropServices;

namespace AzurPilot.Windows.Processes;

/// <summary>Накопленные времена CPU всей системы (<c>GetSystemTimes</c>) в 100-нс единицах.</summary>
public static partial class SystemCpuTimes
{
    /// <summary>Время простоя, ядра (включая простой) и пользовательского режима либо null при отказе API.</summary>
    public static (long Idle, long Kernel, long User)? Read()
        => GetSystemTimes(out long idle, out long kernel, out long user) != 0 ? (idle, kernel, user) : null;

    [LibraryImport("kernel32.dll", SetLastError = false)]
    private static partial int GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
