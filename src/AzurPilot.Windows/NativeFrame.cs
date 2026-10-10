using System.Runtime.InteropServices;

namespace AzurPilot.Windows;

/// <summary>Неизменяемые метаданные RGB8-кадра, которым владеет DLL.</summary>
/// <param name="Width">Ширина кадра в пикселях.</param>
/// <param name="Height">Высота кадра в пикселях.</param>
/// <param name="StrideBytes">Расстояние между строками в байтах.</param>
/// <param name="ByteLength">Длина native буфера RGB8 в байтах.</param>
/// <param name="PixelFormat">Идентификатор формата пикселей из C ABI.</param>
public readonly record struct NativeFrameInfo(
    uint Width,
    uint Height,
    uint StrideBytes,
    uint ByteLength,
    uint PixelFormat);

/// <summary>Физический прямоугольник пикселей с невключённой правой и нижней границами.</summary>
/// <param name="Left">Включённая левая граница.</param>
/// <param name="Top">Включённая верхняя граница.</param>
/// <param name="Right">Невключённая правая граница.</param>
/// <param name="Bottom">Невключённая нижняя граница.</param>
public readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom);

/// <summary>Статистика пикселей native RGB8-области без передачи пиксельного буфера.</summary>
/// <param name="PixelCount">Число пикселей в области.</param>
/// <param name="MeanRed">Среднее красного канала.</param>
/// <param name="MeanGreen">Среднее зелёного канала.</param>
/// <param name="MeanBlue">Среднее синего канала.</param>
/// <param name="StandardDeviationRed">Стандартное отклонение красного канала по всей совокупности.</param>
/// <param name="StandardDeviationGreen">Стандартное отклонение зелёного канала по всей совокупности.</param>
/// <param name="StandardDeviationBlue">Стандартное отклонение синего канала по всей совокупности.</param>
public readonly record struct RegionRgbStatistics(
    ulong PixelCount,
    double MeanRed,
    double MeanGreen,
    double MeanBlue,
    double StandardDeviationRed,
    double StandardDeviationGreen,
    double StandardDeviationBlue);

/// <summary>Владелец декодированного кадра DLL с идемпотентным освобождением.</summary>
/// <remarks>
/// Тип не раскрывает дескриптор DLL и RGB-указатель. Проверка освобождения и весь вызов
/// <see cref="GetInfo"/> и <see cref="ScanRegion"/> синхронизированы с <see cref="Dispose"/>. Source-generated interop
/// дополнительно удерживает SafeHandle до возврата native-функции.
/// </remarks>
public sealed class NativeFrame : IDisposable
{
    private readonly NativeFrameSafeHandle _handle;
    private readonly System.Threading.Lock _lifetimeLock = new();
    private bool _disposed;

    internal NativeFrame(NativeFrameSafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    /// <summary>Читает неизменяемые метаданные, пока кадр не освобождён.</summary>
    /// <returns>Размеры, шаг строки, длину буфера и идентификатор RGB8.</returns>
    /// <exception cref="ObjectDisposedException">Кадр уже освобождён.</exception>
    /// <exception cref="NativeBoundaryUnavailableException">Native библиотека больше не загружается.</exception>
    /// <exception cref="NativeAbiMismatchException">Версия ABI не совпадает с ожидаемой.</exception>
    /// <exception cref="AzurPilotNativeBoundaryException">Native сторона нарушила контракт кадра.</exception>
    public NativeFrameInfo GetInfo()
    {
        lock (_lifetimeLock)
        {
            // Проверка предшествует всему interop, включая запрос версии ABI в AzurPilotNativeBridge.
            ObjectDisposedException.ThrowIf(_disposed, this);
            return AzurPilotNativeBridge.GetFrameInfo(_handle);
        }
    }

    /// <summary>Вычисляет RGB-статистику физической области внутри DLL без копирования пикселей.</summary>
    /// <param name="region">Непустая область, полностью находящаяся внутри кадра.</param>
    /// <returns>Число пикселей, средние и стандартные отклонения в порядке RGB.</returns>
    /// <exception cref="ObjectDisposedException">Кадр уже освобождён.</exception>
    /// <exception cref="NativeAbiMismatchException">Обязательный экспорт ABI недоступен.</exception>
    /// <exception cref="AzurPilotNativeBoundaryException">Native операция отвергла область или результат.</exception>
    public RegionRgbStatistics ScanRegion(PixelRegion region)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return AzurPilotNativeBridge.ScanFrameRegion(_handle, region);
        }
    }

    /// <summary>Освобождает кадр; повторный вызов безопасен.</summary>
    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Внутренний владелец непрозрачного handle, освобождаемого через DLL.</summary>
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
