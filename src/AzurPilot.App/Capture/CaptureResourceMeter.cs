using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzurPilot.App.Capture;

/// <summary>Ресурсы одного измерения: CPU текущего процесса, общая загрузка CPU системы и память процесса.</summary>
/// <param name="WallMs">Длительность измерения.</param>
/// <param name="ProcessCpuMs">Процессорное время процесса (user + kernel).</param>
/// <param name="ProcessCpuPercent">Процессорное время относительно длительности; 100 — одно ядро.</param>
/// <param name="SystemCpuPercent">Доля занятого времени всех ядер системы.</param>
/// <param name="WorkingSetBytes">Рабочий набор в конце измерения.</param>
/// <param name="PeakWorkingSetBytes">Пиковый рабочий набор процесса.</param>
/// <param name="WorkingSetDeltaBytes">Изменение рабочего набора за измерение.</param>
internal sealed record CaptureResourceUsage(double WallMs, double ProcessCpuMs, double ProcessCpuPercent,
    double SystemCpuPercent, long WorkingSetBytes, long PeakWorkingSetBytes, long WorkingSetDeltaBytes);

/// <summary>
/// Измеритель ресурсов. CPU процесса не включает эмулятор и внешние процессы ADB, поэтому рядом всегда
/// сообщается общая загрузка системы. Кадры и пиксели не измеряются и не сохраняются.
/// </summary>
internal sealed partial class CaptureResourceMeter
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _cpu;
    private readonly long _workingSet;
    private readonly (long Idle, long Kernel, long User)? _system;

    private CaptureResourceMeter()
    {
        using Process process = Process.GetCurrentProcess();
        _cpu = process.TotalProcessorTime;
        _workingSet = process.WorkingSet64;
        _system = SystemTimes();
    }

    internal static CaptureResourceMeter Start() => new();

    internal CaptureResourceUsage Stop()
    {
        double wall = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = (process.TotalProcessorTime - _cpu).TotalMilliseconds;
        double system = 0;
        if (_system is { } before && SystemTimes() is { } after)
        {
            // Время ядра GetSystemTimes включает простой: занятость = (kernel + user − idle) / (kernel + user).
            long idle = after.Idle - before.Idle;
            long total = (after.Kernel - before.Kernel) + (after.User - before.User);
            system = total <= 0 ? 0 : Math.Clamp((total - idle) * 100.0 / total, 0, 100);
        }

        return new(Math.Round(wall, 1), Math.Round(cpu, 1), wall <= 0 ? 0 : Math.Round(cpu * 100 / wall, 1),
            Math.Round(system, 1), process.WorkingSet64, process.PeakWorkingSet64, process.WorkingSet64 - _workingSet);
    }

    private static (long Idle, long Kernel, long User)? SystemTimes()
        => GetSystemTimes(out long idle, out long kernel, out long user) != 0 ? (idle, kernel, user) : null;

    [LibraryImport("kernel32.dll", SetLastError = false)]
    private static partial int GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
