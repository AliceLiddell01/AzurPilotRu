using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.Windows.Processes;

/// <summary>
/// Изоляция stdout процесса: сторонние native-компоненты (например, DLL эмулятора) пишут диагностику в CRT fd 1
/// или в STD_OUTPUT_HANDLE процесса. Исходный stdout дублируется для машинного вывода, а fd 1 и стандартный
/// handle перенаправляются в NUL до загрузки таких компонентов.
/// </summary>
public static partial class StandardOutputIsolation
{
    private const int StandardOutputDescriptor = 1;
    private const int StandardOutputHandle = -11;
    private const int WriteOnly = 0x0001;

    /// <summary>Поток исходного stdout; при недоступности CRT — обычный stdout без изоляции.</summary>
    public static Stream Isolate()
    {
        try
        {
            int saved = Duplicate(StandardOutputDescriptor);
            if (saved < 0)
            {
                return Console.OpenStandardOutput();
            }

            nint original = GetOsHandle(saved);
            int sink = OpenFile("NUL", WriteOnly);
            if (original == -1 || sink < 0 || DuplicateTo(sink, StandardOutputDescriptor) != 0)
            {
                return Console.OpenStandardOutput();
            }

            _ = Close(sink);
            _ = SetStdHandle(StandardOutputHandle, GetOsHandle(StandardOutputDescriptor));

            // Дубликат остаётся открытым до конца процесса: им владеет CRT, а не этот поток.
            return new FileStream(new SafeFileHandle(original, ownsHandle: false), FileAccess.Write, 1);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return Console.OpenStandardOutput();
        }
    }

    [LibraryImport("ucrtbase.dll", EntryPoint = "_dup")]
    private static partial int Duplicate(int descriptor);

    [LibraryImport("ucrtbase.dll", EntryPoint = "_dup2")]
    private static partial int DuplicateTo(int source, int target);

    [LibraryImport("ucrtbase.dll", EntryPoint = "_close")]
    private static partial int Close(int descriptor);

    [LibraryImport("ucrtbase.dll", EntryPoint = "_get_osfhandle")]
    private static partial nint GetOsHandle(int descriptor);

    [LibraryImport("ucrtbase.dll", EntryPoint = "_wopen", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int standardHandle, nint handle);
}
