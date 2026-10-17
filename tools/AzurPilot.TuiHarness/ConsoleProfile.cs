using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.TuiHarness;

// Выполняется коротким процессом начальной настройки внутри той же ConPTY до запуска продуктового EXE.
// Изменяет только её виртуальный консольный буфер, без рендеринга UI и внешних процессов.
internal static partial class ConsoleProfile
{
    private static readonly string[] WindowsColors = ["black", "blue", "green", "cyan", "red", "magenta", "yellow", "white",
        "brightBlack", "brightBlue", "brightGreen", "brightCyan", "brightRed", "brightMagenta", "brightYellow", "brightWhite"];

    internal static unsafe int Apply(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement theme = document.RootElement.GetProperty("theme");
        using SafeFileHandle output = new(GetStdHandle(-11), ownsHandle: false);
        BufferInfo info = new() { Size = (uint)sizeof(BufferInfo) };
        if (!GetConsoleScreenBufferInfoEx(output, ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        int foreground = -1;
        int background = -1;
        for (int index = 0; index < WindowsColors.Length; index++)
        {
            string colorText = theme.GetProperty(WindowsColors[index]).GetString()!;
            uint rgb = Convert.ToUInt32(colorText[1..], 16);
            info.Colors[index] = ((rgb & 255) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 255);
            if (colorText.Equals(theme.GetProperty("foreground").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                foreground = index;
            }

            if (colorText.Equals(theme.GetProperty("background").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                background = index;
            }
        }

        if (foreground < 0 || background < 0)
        {
            throw new HarnessBlockedException("Цвета консоли по умолчанию (fg/bg) должны присутствовать в палитре ANSI 16 профиля.");
        }

        info.Attributes = (ushort)((background << 4) | foreground);
        // API принимает эксклюзивные правую/нижнюю границы, Get возвращает включительные.
        info.Right++;
        info.Bottom++;
        return !SetConsoleScreenBufferInfoEx(output, ref info) ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : 0;
    }

    internal static void EnableVirtualTerminalOutput()
    {
        using SafeFileHandle output = new(GetStdHandle(-11), ownsHandle: false);
        if (!GetConsoleMode(output, out uint mode) || !SetConsoleMode(output, mode | 5u))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BufferInfo
    {
        internal uint Size;
        internal short Width, Height, CursorX, CursorY;
        internal ushort Attributes;
        internal short Left, Top, Right, Bottom, MaximumWidth, MaximumHeight;
        internal ushort PopupAttributes;
        internal int Fullscreen;
        internal fixed uint Colors[16];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfoEx(SafeFileHandle handle, ref BufferInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleScreenBufferInfoEx(SafeFileHandle handle, ref BufferInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(SafeFileHandle handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(SafeFileHandle handle, uint mode);
}
