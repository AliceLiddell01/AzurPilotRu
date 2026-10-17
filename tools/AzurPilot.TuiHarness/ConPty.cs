using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.TuiHarness;

// Только Windows x64. Дескрипторы принадлежат SafeHandle; чтение и запись имеют разные потоки.
internal sealed partial class ConPty : IAsyncDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;

    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly SafePseudoConsole _console;
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private readonly Channel<string> _inputQueue = Channel.CreateBounded<string>(256);
    private readonly Task _reader;
    private readonly Task _writer;
    private long _sequence;
    private long _lastOutput = Stopwatch.GetTimestamp();
    private long _lastInput = Stopwatch.GetTimestamp();
    private int _pendingInput;
    private bool _closing;
    private string? _fault;

    internal Channel<(long Sequence, byte[] Bytes)> Output { get; } =
        Channel.CreateBounded<(long, byte[])>(512);
    internal int Pid { get; }
    internal int? BootstrapPid { get; }
    internal long Sequence => Interlocked.Read(ref _sequence);
    internal TimeSpan OutputIdle => Stopwatch.GetElapsedTime(Math.Max(Interlocked.Read(ref _lastOutput), Interlocked.Read(ref _lastInput)));
    internal int PendingInput => Volatile.Read(ref _pendingInput);
    internal string? Fault => Volatile.Read(ref _fault);
    internal bool Alive => Native.GetExitCodeProcess(_process, out uint code) && code == 259;
    internal uint ExitCode => Native.GetExitCodeProcess(_process, out uint code) ? code : uint.MaxValue;

    internal static (int Coord, int Startup, int StartupEx, int JobLimits) NativeLayoutSizes()
    {
        return (Marshal.SizeOf<Coord>(), Marshal.SizeOf<Startup>(), Marshal.SizeOf<StartupEx>(), Marshal.SizeOf<JobLimits>());
    }

    internal ConPty(string exe, string arguments, string directory, int columns, int rows, bool safe, bool live = false,
        string? profilePath = null)
    {
        Check(Native.CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, 0, 0));
        using (inputRead)
        {
            try
            {
                Check(Native.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, 0, 0));
                using (outputWrite)
                {
                    try
                    {
                        Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new Coord((short)columns, (short)rows),
                            inputRead, outputWrite, 0, out _console));
                        _job = CreateJob(safe, childrenOutsideJob: live);
                        _output = new FileStream(outputRead, FileAccess.Read, 8192, isAsync: false);
                        _input = new FileStream(inputWrite, FileAccess.Write, 8192, isAsync: false);
                        _reader = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        _writer = Task.Factory.StartNew(Write, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        try
                        {
                            if (profilePath is not null)
                            {
                                string bootstrapExe = Environment.ProcessPath!;
                                string prefix = Path.GetFileNameWithoutExtension(bootstrapExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                                    ? $"\"{typeof(Program).Assembly.Location}\" " : "";
                                (SafeFileHandle bootstrap, int bootstrapPid) = Launch(bootstrapExe,
                                    $"{prefix}__console-profile \"{profilePath}\"", directory, _console, _job, breakAway: live);
                                using (bootstrap)
                                {
                                    if (Native.WaitForSingleObject(bootstrap, HarnessTimeouts.ConsoleProfileBootstrapMs) != 0
                                        || !Native.GetExitCodeProcess(bootstrap, out uint code) || code != 0)
                                    {
                                        throw new HarnessBlockedException("Не применён профиль виртуальной консоли; см. console-profile-error.json.");
                                    }
                                }

                                BootstrapPid = bootstrapPid;
                                Stopwatch released = Stopwatch.StartNew();
                                while (true)
                                {
                                    Check(Native.QueryInformationJobObject(_job, 1, out JobAccounting accounting,
                                        (uint)Marshal.SizeOf<JobAccounting>(), 0));
                                    if (accounting.ActiveProcesses == 0)
                                    {
                                        break;
                                    }

                                    if (released.ElapsedMilliseconds > HarnessTimeouts.ConsoleProfileReleaseMs)
                                    {
                                        throw new HarnessBlockedException("Job не освободил завершившийся console bootstrap.");
                                    }

                                    Thread.Sleep(10);
                                }
                            }

                            (_process, Pid) = Launch(exe, arguments, directory, _console, _job, breakAway: live);
                        }
                        catch
                        {
                            _job.Dispose();
                            _closing = true;
                            _ = _inputQueue.Writer.TryComplete();
                            _console.Dispose();
                            _ = Task.WhenAll(_reader, _writer).Wait(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
                            _output.Dispose();
                            _input.Dispose();
                            throw;
                        }
                    }
                    catch
                    {
                        outputRead.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                inputWrite.Dispose();
                throw;
            }
        }

    }

    internal void Send(string value)
    {
        _ = Interlocked.Increment(ref _pendingInput);
        _ = Interlocked.Exchange(ref _lastInput, Stopwatch.GetTimestamp());
        if (_closing || !_inputQueue.Writer.TryWrite(value))
        {
            _ = Interlocked.Decrement(ref _pendingInput);
            throw new InvalidOperationException("Ввод закрыт либо превышен бюджет очереди.");
        }
    }

    internal void Resize(int columns, int rows)
    {
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(_console, new Coord((short)columns, (short)rows)));
    }

    private void Read()
    {
        try
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = _output.Read(buffer)) > 0)
            {
                long sequence = Interlocked.Increment(ref _sequence);
                _ = Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp());
                if (!Output.Writer.TryWrite((sequence, buffer.AsSpan(0, count).ToArray())))
                {
                    // Продолжаем читать канал, но при потере VT-потока дальнейший захват недопустим.
                    Volatile.Write(ref _fault, "Превышен бюджет VT: renderer не успевает за ConPTY.");
                }
            }
        }
        catch (IOException exception)
        {
            if (!_closing)
            {
                Volatile.Write(ref _fault, exception.Message);
            }
        }
        finally
        {
            _ = Output.Writer.TryComplete();
        }
    }

    private void Write()
    {
        try
        {
            while (_inputQueue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_inputQueue.Reader.TryRead(out string? value))
                {
                    _input.Write(Encoding.UTF8.GetBytes(value));
                    _input.Flush();
                    _ = Interlocked.Exchange(ref _lastInput, Stopwatch.GetTimestamp());
                    _ = Interlocked.Decrement(ref _pendingInput);
                }
            }
        }
        catch (IOException exception)
        {
            if (!_closing)
            {
                Volatile.Write(ref _fault, exception.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _ = _inputQueue.Writer.TryComplete();
        _job.Dispose(); // Kill-on-close: только принадлежащий этой сессии процесс и его потомки.
        if (Native.WaitForSingleObject(_process, HarnessTimeouts.CleanupMs) != 0)
        {
            throw new TimeoutException("Процесс приложения не завершился после закрытия Job Object.");
        }

        await Task.Run(_console.Dispose).WaitAsync(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
        await Task.WhenAll(_reader, _writer).WaitAsync(TimeSpan.FromMilliseconds(HarnessTimeouts.CleanupMs));
        _input.Dispose();
        _output.Dispose();
        _process.Dispose();
    }

    internal static SafeFileHandle CreateJob(bool denyChildren, bool allowBreakaway = false, bool childrenOutsideJob = false)
    {
        SafeFileHandle job = Native.CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        JobLimits limits = new();
        limits.Basic.LimitFlags = 0x2000u | (denyChildren ? 8u : 0u)
            | (allowBreakaway ? 0x800u : 0u) | (childrenOutsideJob ? 0x1000u : 0u);
        limits.Basic.ActiveProcessLimit = denyChildren ? 1u : 0u;
        try
        {
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static void AssignOwnerJob(SafeFileHandle job)
    {
        using Process process = Process.GetCurrentProcess();
        Check(Native.AssignProcessToJobObject(job, process.SafeHandle));
    }

    internal static void AssignProcessToJob(SafeFileHandle job, SafeHandle process)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(process);
        Check(Native.AssignProcessToJobObject(job, process));
    }

    internal static bool IsProcessInJob(SafeHandle process, SafeFileHandle job)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(job);
        Check(Native.IsProcessInJob(process, job, out bool result));
        return result;
    }

    internal static Process StartInJob(string executable, IReadOnlyList<string> arguments, string directory,
        SafeFileHandle job)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(job);

        string fullExecutable = Path.GetFullPath(executable);
        StartupEx startup = new();
        startup.Startup.Size = (uint)Marshal.SizeOf<Startup>();
        var (processHandle, _, startedProcess) = StartProcessInJob(fullExecutable,
            BuildCommandLine(fullExecutable, arguments), directory, job, startup, CreateSuspended);
        using (processHandle)
        {
            return startedProcess;
        }
    }

    internal static Process StartOwner(string executable, string? assembly, string requestPath)
    {
        StartupEx startup = new();
        startup.Startup.Size = (uint)Marshal.SizeOf<Startup>();
        startup.Startup.Flags = 0x100;
        string prefix = assembly is null ? "" : $"\"{assembly}\" ";
        char[] command = $"\"{executable}\" {prefix}__owner \"{requestPath}\"\0".ToCharArray();
        Check(Native.CreateProcessW(executable, command, 0, 0, false, 0x08000200, 0,
            Environment.CurrentDirectory, ref startup, out ProcessInformation info));
        using SafeFileHandle process = new(info.Process, ownsHandle: true);
        using SafeFileHandle thread = new(info.Thread, ownsHandle: true);
        return Process.GetProcessById((int)info.ProcessId);
    }

    private static (SafeFileHandle Process, int Pid) Launch(
        string exe, string arguments, string directory, SafePseudoConsole console, SafeFileHandle job, bool breakAway)
    {
        nuint size = 0;
        _ = Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint attributes = Marshal.AllocHGlobal((nint)size);
        bool initialized = false;
        try
        {
            Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016,
                console.DangerousGetHandle(), (nuint)nint.Size, 0, 0));
            StartupEx startup = new();
            startup.Startup.Size = (uint)Marshal.SizeOf<StartupEx>();
            // Не копировать перенаправленные стандартные дескрипторы владельца. ConPTY создаёт дескрипторы консоли клиента.
            startup.Startup.Flags = 0x100;
            startup.Attributes = attributes;
            char[] command = $"\"{exe}\" {arguments}\0".ToCharArray();
            var (handle, processId, processObject) = StartProcessInJob(exe, command,
                directory, job, startup, CreateSuspended | ExtendedStartupInfoPresent
                    | (breakAway ? CreateBreakawayFromJob : 0u));
            using (processObject)
            {
                return (handle, processId);
            }
        }
        finally
        {
            if (initialized)
            {
                Native.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
        }
    }

    private static (SafeFileHandle Handle, int ProcessId, Process ProcessObject) StartProcessInJob(string executable, char[] command,
        string directory, SafeFileHandle job, StartupEx startup, uint creationFlags)
    {
        Check(Native.CreateProcessW(executable, command, 0, 0, false, creationFlags, 0, directory,
            ref startup, out ProcessInformation info));
        SafeFileHandle process = new(info.Process, ownsHandle: true);
        using SafeFileHandle thread = new(info.Thread, ownsHandle: true);
        Process? processObject = null;
        try
        {
            // Процесс остаётся приостановленным до подтверждения принадлежности Job.
            if (!Native.AssignProcessToJobObject(job, process))
            {
                int error = Marshal.GetLastPInvokeError();
                if (!IsProcessInJob(process, job))
                {
                    throw new Win32Exception(error, $"Процесс не назначен в принадлежащий сессии Job: {executable}.");
                }
            }

            processObject = Process.GetProcessById((int)info.ProcessId);
            // Открыть managed-дескриптор, пока процесс приостановлен и исходный handle ещё жив.
            _ = processObject.SafeHandle;
            return Native.ResumeThread(thread) == uint.MaxValue
                ? throw new Win32Exception(Marshal.GetLastPInvokeError())
                : ((SafeFileHandle Handle, int ProcessId, Process ProcessObject))
                    (process, (int)info.ProcessId, processObject);
        }
        catch
        {
            processObject?.Dispose();
            _ = Native.TerminateProcess(process, 1);
            process.Dispose();
            throw;
        }
    }

    private static char[] BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        StringBuilder command = new();
        AppendCommandLineArgument(command, executable);
        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("Аргументы процесса не могут содержать null.", nameof(arguments));
            }

            _ = command.Append(' ');
            AppendCommandLineArgument(command, argument);
        }

        _ = command.Append('\0');
        return command.ToString().ToCharArray();
    }

    private static void AppendCommandLineArgument(StringBuilder command, string argument)
    {
        bool quote = argument.Length == 0;
        foreach (char character in argument)
        {
            quote |= char.IsWhiteSpace(character) || character == '"';
        }

        if (!quote)
        {
            _ = command.Append(argument);
            return;
        }

        _ = command.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                _ = command.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            _ = command.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        _ = command.Append('\\', backslashes * 2).Append('"');
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Startup
    {
        internal uint Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal nint ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupEx
    {
        internal Startup Startup;
        internal nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    private sealed class SafePseudoConsole : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsole() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle()
        {
            Native.ClosePseudoConsole(handle);
            return true;
        }
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
        [LibraryImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")]
        internal static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out SafePseudoConsole console);
        [LibraryImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")]
        internal static partial int ResizePseudoConsole(SafePseudoConsole console, Coord size);
        [LibraryImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")]
        internal static partial void ClosePseudoConsole(nint console);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [LibraryImport("kernel32.dll")]
        internal static partial void DeleteProcThreadAttributeList(nint list);
        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateProcessW(string application, [In, Out] char[] command, nint processAttributes,
            nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
            nint environment, string directory, ref StartupEx startup, out ProcessInformation information);
        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits information, uint length);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting information, uint length, nint returnedLength);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeHandle process);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessInJob(SafeHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint ResumeThread(SafeFileHandle thread);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(SafeFileHandle process, uint exitCode);
    }
}
