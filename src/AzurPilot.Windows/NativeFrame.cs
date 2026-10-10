using System.Runtime.InteropServices;

namespace AzurPilot.Windows;

/// <summary>Неизменяемые metadata native-owned RGB8 frame.</summary>
/// <param name="Width">Ширина frame в пикселях.</param>
/// <param name="Height">Высота frame в пикселях.</param>
/// <param name="StrideBytes">Расстояние между строками в байтах.</param>
/// <param name="ByteLength">Длина native RGB8 payload в байтах.</param>
/// <param name="PixelFormat">Идентификатор формата пикселей из C ABI.</param>
public readonly record struct NativeFrameInfo(
    uint Width,
    uint Height,
    uint StrideBytes,
    uint ByteLength,
    uint PixelFormat);

/// <summary>Владелец native-owned PNG frame с идемпотентным освобождением.</summary>
/// <remarks>
/// Тип не раскрывает native handle и RGB-указатель. Каждый вызов <see cref="GetInfo"/> передаёт
/// SafeHandle в source-generated interop, который удерживает ресурс до возврата native-функции.
/// </remarks>
public sealed class NativeFrame : IDisposable
{
    private readonly NativeFrameSafeHandle _handle;

    internal NativeFrame(NativeFrameSafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    /// <summary>Читает неизменяемые metadata пока native frame остаётся жив.</summary>
    /// <returns>Размеры, stride, длину payload и идентификатор RGB8.</returns>
    /// <exception cref="ObjectDisposedException">Frame уже освобождён.</exception>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотека больше не загружается.</exception>
    /// <exception cref="NativeAbiMismatchException">Версия ABI не совпадает с ожидаемой.</exception>
    /// <exception cref="AzurPilotNativeBoundaryException">Native сторона нарушила контракт frame.</exception>
    public NativeFrameInfo GetInfo() => AzurPilotNativeBridge.GetFrameInfo(_handle);

    /// <summary>Освобождает native frame; повторный вызов безопасен.</summary>
    public void Dispose()
    {
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Внутренний владелец opaque handle, освобождаемый через DLL.</summary>
internal sealed class NativeFrameSafeHandle : SafeHandle
{
    /// <summary>Создаёт пустой handle для source-generated LibraryImport.</summary>
    public NativeFrameSafeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle() => AzurPilotNativeBridge.ReleaseFrame(handle);
}
