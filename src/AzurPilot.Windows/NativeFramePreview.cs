using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AzurPilot.Windows;

/// <summary>Вид диагностической аннотации превью; значения повторяют нормативный заголовок ABI.</summary>
public enum NativeAnnotationKind : uint
{
    /// <summary>Контур полуоткрытого прямоугольника.</summary>
    Rectangle = 1,
    /// <summary>Залитый полуоткрытый прямоугольник.</summary>
    FilledRectangle = 2,
    /// <summary>Окружность: центр Left/Top, радиус Right.</summary>
    Circle = 3,
    /// <summary>Круг: центр Left/Top, радиус Right.</summary>
    FilledCircle = 4,
    /// <summary>Косой крест: центр Left/Top, полуразмер Right.</summary>
    Cross = 5,
    /// <summary>ASCII-текст: базовая линия Left/Top, масштаб Right в процентах.</summary>
    Text = 6,
}

/// <summary>Одна аннотация диагностического превью в его собственных пикселях; цвет — RGB.</summary>
/// <param name="Kind">Вид примитива.</param>
/// <param name="Region">Геометрия по правилам вида (см. заголовок ABI).</param>
/// <param name="Red">Красный канал.</param>
/// <param name="Green">Зелёный канал.</param>
/// <param name="Blue">Синий канал.</param>
/// <param name="Thickness">Толщина линии 1..8.</param>
/// <param name="Dashed">Штриховой контур; допустим только для прямоугольника.</param>
/// <param name="Text">Печатный ASCII до 63 символов; только для текста.</param>
public readonly record struct NativeFrameAnnotation(NativeAnnotationKind Kind, PixelRegion Region,
    byte Red, byte Green, byte Blue, byte Thickness = 2, bool Dashed = false, string? Text = null);

public static partial class AzurPilotNativeBridge
{
    /// <summary>Предел стороны превью и кадра, кодируемого в JPEG, из нормативного заголовка ABI.</summary>
    public const int MaximumPreviewDimension = 1920;

    /// <summary>Предел числа аннотаций одного JPEG.</summary>
    public const int MaximumAnnotations = 512;

    /// <summary>Нижняя граница качества JPEG.</summary>
    public const int MinimumJpegQuality = 50;

    /// <summary>Верхняя граница качества JPEG.</summary>
    public const int MaximumJpegQuality = 95;

    /// <summary>Предел размера одного JPEG.</summary>
    public const int MaximumJpegBytes = 8_388_608;

    /// <summary>Размер буфера текста аннотации с завершающим NUL.</summary>
    internal const int AnnotationTextBytes = 64;

    /// <summary>Значение стиля сплошной линии.</summary>
    internal const uint SolidAnnotationStyle = 0;

    /// <summary>Значение стиля штриховой линии.</summary>
    internal const uint DashedAnnotationStyle = 1;
    // Типичный аннотированный JPEG логического размера заметно меньше; при нехватке выполняется одна повторная попытка.
    private const int InitialJpegBuffer = 768 * 1024;

    internal static NativeFrame CreatePreview(NativeFrameSafeHandle frame, int width, int height)
    {
        EnsureFrameAbiVersion();
        EnsureFramePreviewApi();
        if (width <= 0 || height <= 0 || width > MaximumPreviewDimension || height > MaximumPreviewDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Размер превью вне предела ABI.");
        }

        int status;
        NativeFrameSafeHandle preview;
        try
        {
            status = CreatePreviewNative(frame, (uint)width, (uint)height, out preview);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_create_preview");
        }

        if (status != StatusOk || preview.IsInvalid)
        {
            preview.Dispose();
            throw new NativeFrameAnalysisException(status == StatusOk ? StatusInternal : status);
        }

        return new NativeFrame(preview);
    }

    internal static unsafe byte[] EncodeJpeg(NativeFrameSafeHandle frame, int quality, ReadOnlySpan<NativeFrameAnnotation> annotations)
    {
        EnsureFrameAbiVersion();
        EnsureFramePreviewApi();
        if (quality is < MinimumJpegQuality or > MaximumJpegQuality)
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        if (annotations.Length > MaximumAnnotations)
        {
            throw new ArgumentOutOfRangeException(nameof(annotations), "Число аннотаций превышает предел ABI.");
        }

        NativeAnnotation[] native = new NativeAnnotation[annotations.Length];
        for (int index = 0; index < annotations.Length; index++)
        {
            native[index] = ToNative(annotations[index]);
        }

        byte[] buffer = new byte[InitialJpegBuffer];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int status;
            uint written;
            fixed (NativeAnnotation* annotationPointer = native)
            fixed (byte* bufferPointer = buffer)
            {
                status = EncodeJpegNative(frame, (uint)quality, native.Length == 0 ? null : annotationPointer,
                    (uint)native.Length, bufferPointer, (uint)buffer.Length, out written);
            }

            if (status == StatusOk && written > 0 && written <= buffer.Length)
            {
                return buffer.AsSpan(0, (int)written).ToArray();
            }

            if (status != StatusBufferTooSmall || attempt > 0 || written == 0 || written > MaximumJpegBytes)
            {
                throw new NativeFrameAnalysisException(status == StatusOk ? StatusInternal : status);
            }

            buffer = new byte[written];
        }

        throw new NativeFrameAnalysisException(StatusInternal);
    }

    /// <summary>Проверяет размер и смещения структуры аннотации из нормативного заголовка ABI.</summary>
    /// <returns>Фактический размер managed структуры.</returns>
    /// <exception cref="AzurPilotNativeBoundaryException">Раскладка не совпадает с ABI.</exception>
    public static unsafe int VerifyAnnotationStructLayout()
    {
        NativeAnnotation annotation = default;
        if (sizeof(NativeAnnotation) != 92
            || (byte*)&annotation.Style - (byte*)&annotation != 4
            || (byte*)&annotation.Region - (byte*)&annotation != 8
            || &annotation.Red - (byte*)&annotation != 24
            || &annotation.Thickness - (byte*)&annotation != 27
            || (byte*)&annotation.Text - (byte*)&annotation != 28)
        {
            throw new AzurPilotNativeBoundaryException("Раскладка managed структуры аннотации не совпадает с заголовком ABI.");
        }

        return sizeof(NativeAnnotation);
    }

    private static NativeAnnotation ToNative(NativeFrameAnnotation annotation)
    {
        string text = annotation.Text ?? string.Empty;
        if (text.Length >= AnnotationTextBytes || text.Any(c => c is < ' ' or > '~')
            || (annotation.Kind == NativeAnnotationKind.Text) != (text.Length > 0))
        {
            throw new ArgumentException("Текст аннотации должен быть печатным ASCII и только у текстового вида.", nameof(annotation));
        }

        NativeAnnotation native = new()
        {
            Kind = (uint)annotation.Kind,
            Style = annotation.Dashed ? DashedAnnotationStyle : SolidAnnotationStyle,
            Region = new()
            {
                Left = annotation.Region.Left,
                Top = annotation.Region.Top,
                Right = annotation.Region.Right,
                Bottom = annotation.Region.Bottom,
            },
            Red = annotation.Red,
            Green = annotation.Green,
            Blue = annotation.Blue,
            Thickness = annotation.Thickness,
        };
        for (int index = 0; index < text.Length; index++)
        {
            native.Text[index] = (byte)text[index];
        }

        return native;
    }

    private static void EnsureFramePreviewApi()
    {
        _ = VerifyAnnotationStructLayout();
        int status;
        try
        {
            status = ProbeCreatePreviewNative(IntPtr.Zero, 0, 0, out IntPtr preview);
            if (status != StatusInvalidArgument || preview != IntPtr.Zero)
            {
                throw new NativeAbiMismatchException("Нарушен NULL-контракт preview ABI.");
            }
        }
        catch (DllNotFoundException exception)
        {
            throw Unavailable(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw Unavailable(exception);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_create_preview");
        }

        try
        {
            status = ProbeEncodeJpegNative(IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, out uint written);
            if (status != StatusInvalidArgument || written != 0)
            {
                throw new NativeAbiMismatchException("Нарушен NULL-контракт JPEG ABI.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_encode_jpeg");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAnnotation
    {
        internal uint Kind;
        internal uint Style;
        internal NativeRegion Region;
        internal byte Red;
        internal byte Green;
        internal byte Blue;
        internal byte Thickness;
        internal AnnotationText Text;

        [InlineArray(AnnotationTextBytes)]
        internal struct AnnotationText
        {
            private byte _element0;
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_create_preview")]
    private static partial int CreatePreviewNative(NativeFrameSafeHandle frame, uint width, uint height,
        out NativeFrameSafeHandle preview);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_create_preview")]
    private static partial int ProbeCreatePreviewNative(IntPtr frame, uint width, uint height, out IntPtr preview);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_encode_jpeg")]
    private static unsafe partial int EncodeJpegNative(NativeFrameSafeHandle frame, uint quality,
        NativeAnnotation* annotations, uint count, byte* buffer, uint bufferSize, out uint written);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_encode_jpeg")]
    private static partial int ProbeEncodeJpegNative(IntPtr frame, uint quality, IntPtr annotations, uint count,
        IntPtr buffer, uint bufferSize, out uint written);
}
