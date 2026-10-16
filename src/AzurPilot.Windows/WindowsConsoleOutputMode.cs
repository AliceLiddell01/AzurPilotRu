using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.Windows;

/// <summary>Временно включает VT output до выбора терминального драйвера; затем восстанавливает режим.</summary>
/// <remarks>
/// Новый ConPTY-клиент может получить console handle без ENABLE_VIRTUAL_TERMINAL_PROCESSING.
/// Terminal.Gui в этом случае выбирает legacy output и квантует RGB до ANSI 16 ещё до записи в ConPTY.
/// Заимствованный stdout не закрывается. Неподдерживающая VT консоль сохраняет исходный режим.
/// </remarks>
public sealed partial class WindowsConsoleOutputMode : IDisposable
{
    private readonly SafeFileHandle _output;
    private readonly uint _original;
    private readonly bool _changed;
    private bool _disposed;

    private WindowsConsoleOutputMode(SafeFileHandle output, uint original, bool changed, int? backgroundRgb)
    {
        _output = output;
        _original = original;
        _changed = changed;
        BackgroundRgb = backgroundRgb;
    }

    /// <summary>Фактический RGB исходного console background либо отсутствие console handle.</summary>
    public int? BackgroundRgb { get; }

    /// <summary>Пытается включить VT для текущего stdout без подмены handles, кодировки и профиля.</summary>
    /// <returns>Scope, который освобождается после остановки терминального драйвера.</returns>
    public static unsafe WindowsConsoleOutputMode EnableVirtualTerminalProcessing()
    {
        SafeFileHandle output = new(GetStdHandle(-11), ownsHandle: false);
        uint original = 0;
        bool changed = false;
        if (!output.IsInvalid && GetConsoleMode(output, out original))
        {
            uint requested = original | 0x0001u | 0x0004u;
            changed = requested != original && SetConsoleMode(output, requested);
        }

        int? background = null;
        BufferInfo info = new() { Size = (uint)sizeof(BufferInfo) };
        if (!output.IsInvalid && GetConsoleScreenBufferInfoEx(output, ref info))
        {
            uint color = info.Colors[(info.Attributes >> 4) & 15];
            background = (int)(((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255));
        }

        return new(output, original, changed, background);
    }

    /// <summary>Восстанавливает режим; отключившаяся консоль не превращает shutdown в новый отказ.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_changed)
        {
            _ = SetConsoleMode(_output, _original);
        }

        _output.Dispose();
        GC.SuppressFinalize(this);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(SafeFileHandle handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(SafeFileHandle handle, uint mode);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfoEx(SafeFileHandle handle, ref BufferInfo info);
}
