using System.Runtime.InteropServices;

namespace AzurPilot.Windows;

/// <summary>Формат сырых пикселей источника захвата; значения повторяют нормативный заголовок ABI v6.</summary>
public enum NativeSourcePixelFormat : uint
{
    /// <summary>4 байта R, G, B, A.</summary>
    Rgba8888 = 1,
    /// <summary>4 байта B, G, R, A.</summary>
    Bgra8888 = 2,
    /// <summary>16 бит little-endian 5-6-5.</summary>
    Rgb565LittleEndian = 3,
    /// <summary>3 байта R, G, B.</summary>
    Rgb888 = 4,
    /// <summary>3 байта B, G, R.</summary>
    Bgr888 = 5,
}

/// <summary>Матрица YUV→RGB видеодекодера; значения повторяют нормативный заголовок ABI v6.</summary>
public enum NativeYuvMatrix : uint
{
    /// <summary>Матрица из типа потока, иначе BT.601.</summary>
    Auto = 0,
    /// <summary>BT.601.</summary>
    Bt601 = 1,
    /// <summary>BT.709.</summary>
    Bt709 = 2,
}

/// <summary>Отказ источника кадра ABI v6 с точным кодом native стороны и кодом провайдера.</summary>
internal sealed class NativeCaptureSourceException : AzurPilotNativeBoundaryException
{
    internal NativeCaptureSourceException(string operation, int statusCode, int providerStatus = 0)
        : base($"Native функция {operation} завершилась с кодом {statusCode}: "
            + $"{AzurPilotNativeBridge.DescribeStatus(statusCode)}"
            + (providerStatus != 0 ? $"; код провайдера {providerStatus}." : "."))
    {
        Operation = operation;
        StatusCode = statusCode;
        ProviderStatus = providerStatus;
    }

    /// <summary>Имя native функции.</summary>
    internal string Operation { get; }

    /// <summary>Код ABI.</summary>
    internal int StatusCode { get; }

    /// <summary>Код провайдера (nemu_ipc или HRESULT Media Foundation); 0 — не сообщался.</summary>
    internal int ProviderStatus { get; }

    /// <summary>Отказ относится к данным источника, а не к сбою границы.</summary>
    internal bool IsDataFailure => StatusCode is AzurPilotNativeBridge.StatusInvalidData
        or AzurPilotNativeBridge.StatusImageTooLarge;
}

public static partial class AzurPilotNativeBridge
{
    /// <summary>Код возврата: провайдер захвата (DLL эмулятора, системный кодек) недоступен.</summary>
    public const int StatusProviderUnavailable = 9;

    /// <summary>Код возврата: провайдер захвата вернул отказ.</summary>
    public const int StatusProviderFailure = 10;

    /// <summary>Код возврата: новых данных ещё нет.</summary>
    public const int StatusNotReady = 11;

    /// <summary>Код возврата: данные источника повреждены или противоречат заявленной геометрии.</summary>
    public const int StatusInvalidData = 12;

    /// <summary>Предел сырого входного буфера пикселей.</summary>
    public const int MaximumRawInputBytes = 67_108_864;

    /// <summary>Предел входного JPEG.</summary>
    public const int MaximumJpegInputBytes = 16_777_216;

    /// <summary>Предел одного пакета видеопотока.</summary>
    public const int MaximumVideoPacketBytes = 16_777_216;

    /// <summary>Бит пакета конфигурации кодека (SPS/PPS).</summary>
    public const uint VideoPacketConfig = 1;

    /// <summary>Бит пакета ключевого кадра.</summary>
    public const uint VideoPacketKeyFrame = 2;

    /// <summary>Сигнатура заголовка aScreenCap BMZ1 (little-endian).</summary>
    public const uint Bmz1Magic = 0x315A4D42;

    /// <summary>Размер заголовка BMZ1.</summary>
    public const int Bmz1HeaderBytes = 20;

    private const uint SourceFlagFlipVertical = 1;
    private const uint VideoCodecH264 = 1;

    /// <summary>Поворачивает кадр внутри DLL на четверть оборота по часовой стрелке (1..3).</summary>
    internal static NativeFrame RotateFrame(NativeFrameSafeHandle frame, int quarterTurns)
    {
        EnsureFrameAbiVersion();
        if (quarterTurns is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(quarterTurns), quarterTurns, "Поворот задаётся 1..3 четвертями.");
        }

        int status;
        NativeFrameSafeHandle rotated;
        try
        {
            status = RotateNative(frame, (uint)quarterTurns, out rotated);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_rotate");
        }

        return TakeFrame("azurpilot_native_frame_rotate", status, rotated);
    }

    /// <summary>Создаёт RGB8-кадр DLL из сырых пикселей с явной геометрией.</summary>
    /// <exception cref="NativeCaptureSourceException">Геометрия или данные отвергнуты.</exception>
    internal static unsafe NativeFrame CreateFrameFromPixels(ReadOnlySpan<byte> pixels, int width, int height,
        int strideBytes, NativeSourcePixelFormat format, bool flipVertical)
    {
        EnsureFrameAbiVersion();
        if (width <= 0 || height <= 0 || strideBytes <= 0)
        {
            throw new NativeCaptureSourceException("azurpilot_native_frame_create_from_pixels", StatusInvalidData);
        }

        int status;
        NativeFrameSafeHandle frame;
        try
        {
            fixed (byte* pointer = pixels)
            {
                status = CreateFromPixelsNative(pointer, (uint)pixels.Length, (uint)width, (uint)height,
                    (uint)strideBytes, (uint)format, flipVertical ? SourceFlagFlipVertical : 0, out frame);
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_create_from_pixels");
        }

        return TakeFrame("azurpilot_native_frame_create_from_pixels", status, frame);
    }

    /// <summary>Декодирует поток aScreenCap BMZ1 (LZ4 + BMP BGR снизу вверх) в RGB8-кадр.</summary>
    internal static unsafe NativeFrame DecodeBmz1(ReadOnlySpan<byte> data)
    {
        EnsureFrameAbiVersion();
        int status;
        NativeFrameSafeHandle frame;
        try
        {
            fixed (byte* pointer = data)
            {
                status = DecodeBmz1Native(pointer, (uint)data.Length, out frame);
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_decode_bmz1");
        }

        return TakeFrame("azurpilot_native_frame_decode_bmz1", status, frame);
    }

    /// <summary>Декодирует baseline/progressive JPEG в RGB8-кадр.</summary>
    internal static unsafe NativeFrame DecodeJpeg(ReadOnlySpan<byte> data)
    {
        EnsureFrameAbiVersion();
        int status;
        NativeFrameSafeHandle frame;
        try
        {
            fixed (byte* pointer = data)
            {
                status = DecodeJpegNative(pointer, (uint)data.Length, out frame);
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_frame_decode_jpeg");
        }

        return TakeFrame("azurpilot_native_frame_decode_jpeg", status, frame);
    }

    internal static NativeNemuSafeHandle OpenNemu(string dllPath, string installFolder, int instanceIndex)
    {
        EnsureFrameAbiVersion();
        int status;
        NativeNemuSafeHandle session;
        try
        {
            status = NemuOpenNative(dllPath, installFolder, instanceIndex, out session);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_nemu_open");
        }

        if (status != StatusOk || session.IsInvalid)
        {
            session.Dispose();
            throw new NativeCaptureSourceException("azurpilot_native_nemu_open", status == StatusOk ? StatusInternal : status);
        }

        return session;
    }

    internal static NativeFrame CaptureNemu(NativeNemuSafeHandle session, uint displayId,
        NativeSourcePixelFormat format, bool flipVertical)
    {
        int status;
        int provider;
        NativeFrameSafeHandle frame;
        try
        {
            status = NemuCaptureNative(session, displayId, (uint)format, flipVertical ? SourceFlagFlipVertical : 0,
                out frame, out provider);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_nemu_capture");
        }

        return TakeFrame("azurpilot_native_nemu_capture", status, frame, provider);
    }

    internal static bool ReleaseNemu(IntPtr session)
    {
        try
        {
            return NemuReleaseNative(session) == StatusOk;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    internal static NativeVideoDecoderSafeHandle CreateH264Decoder()
    {
        EnsureFrameAbiVersion();
        int status;
        NativeVideoDecoderSafeHandle decoder;
        try
        {
            status = VideoDecoderCreateNative(VideoCodecH264, out decoder);
        }
        catch (EntryPointNotFoundException)
        {
            throw MissingFrameApi("azurpilot_native_video_decoder_create");
        }

        if (status != StatusOk || decoder.IsInvalid)
        {
            decoder.Dispose();
            throw new NativeCaptureSourceException("azurpilot_native_video_decoder_create",
                status == StatusOk ? StatusInternal : status);
        }

        return decoder;
    }

    internal static unsafe int PushVideoPacket(NativeVideoDecoderSafeHandle decoder, ReadOnlySpan<byte> packet,
        long presentationMicroseconds, uint flags)
    {
        int status;
        uint decoded;
        int provider;
        fixed (byte* pointer = packet)
        {
            status = VideoDecoderPushNative(decoder, pointer, (uint)packet.Length, presentationMicroseconds, flags,
                out decoded, out provider);
        }

        if (status != StatusOk)
        {
            throw new NativeCaptureSourceException("azurpilot_native_video_decoder_push", status, provider);
        }

        return (int)Math.Min(decoded, int.MaxValue);
    }

    /// <summary>Возвращает последний декодированный кадр с номером больше <paramref name="afterSequence"/> или null.</summary>
    internal static NativeFrame? TryGetLatestVideoFrame(NativeVideoDecoderSafeHandle decoder, ulong afterSequence,
        int displayWidth, int displayHeight, NativeYuvMatrix matrix, out long presentationMicroseconds,
        out ulong sequence)
    {
        int status = VideoDecoderLatestFrameNative(decoder, afterSequence, (uint)displayWidth, (uint)displayHeight,
            (uint)matrix, out NativeFrameSafeHandle frame, out presentationMicroseconds, out sequence);
        if (status == StatusNotReady)
        {
            frame.Dispose();
            return null;
        }

        return TakeFrame("azurpilot_native_video_decoder_latest_frame", status, frame);
    }

    internal static bool ReleaseVideoDecoder(IntPtr decoder)
    {
        try
        {
            return VideoDecoderReleaseNative(decoder) == StatusOk;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    private static NativeFrame TakeFrame(string operation, int status, NativeFrameSafeHandle frame, int provider = 0)
    {
        if (status != StatusOk || frame.IsInvalid)
        {
            frame.Dispose();
            throw new NativeCaptureSourceException(operation, status == StatusOk ? StatusInternal : status, provider);
        }

        return new NativeFrame(frame);
    }

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_rotate")]
    private static partial int RotateNative(NativeFrameSafeHandle frame, uint quarterTurns, out NativeFrameSafeHandle rotated);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_create_from_pixels")]
    private static unsafe partial int CreateFromPixelsNative(byte* pixels, uint size, uint width, uint height,
        uint stride, uint sourceFormat, uint flags, out NativeFrameSafeHandle frame);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_decode_bmz1")]
    private static unsafe partial int DecodeBmz1Native(byte* data, uint size, out NativeFrameSafeHandle frame);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_decode_jpeg")]
    private static unsafe partial int DecodeJpegNative(byte* data, uint size, out NativeFrameSafeHandle frame);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_nemu_open", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NemuOpenNative(string dllPath, string installFolder, int instanceIndex,
        out NativeNemuSafeHandle session);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_nemu_capture")]
    private static partial int NemuCaptureNative(NativeNemuSafeHandle session, uint displayId, uint sourceFormat,
        uint flags, out NativeFrameSafeHandle frame, out int providerStatus);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_nemu_release")]
    private static partial int NemuReleaseNative(IntPtr session);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_video_decoder_create")]
    private static partial int VideoDecoderCreateNative(uint codec, out NativeVideoDecoderSafeHandle decoder);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_video_decoder_push")]
    private static unsafe partial int VideoDecoderPushNative(NativeVideoDecoderSafeHandle decoder, byte* data,
        uint size, long presentationMicroseconds, uint flags, out uint decoded, out int providerStatus);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_video_decoder_latest_frame")]
    private static partial int VideoDecoderLatestFrameNative(NativeVideoDecoderSafeHandle decoder,
        ulong afterSequence, uint displayWidth, uint displayHeight, uint yuvMatrix,
        out NativeFrameSafeHandle frame, out long presentationMicroseconds, out ulong sequence);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_video_decoder_release")]
    private static partial int VideoDecoderReleaseNative(IntPtr decoder);
}

/// <summary>Владелец сессии nemu_ipc; освобождение отключает соединение, DLL остаётся загруженной.</summary>
internal sealed class NativeNemuSafeHandle : SafeHandle
{
    public NativeNemuSafeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle() => AzurPilotNativeBridge.ReleaseNemu(handle);
}

/// <summary>Владелец H.264 декодера DLL.</summary>
internal sealed class NativeVideoDecoderSafeHandle : SafeHandle
{
    public NativeVideoDecoderSafeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc />
    protected override bool ReleaseHandle() => AzurPilotNativeBridge.ReleaseVideoDecoder(handle);
}
