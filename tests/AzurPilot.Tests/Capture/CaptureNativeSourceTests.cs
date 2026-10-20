using System.Buffers.Binary;
using AzurPilot.Windows;
using AzurPilot.Windows.Capture;
using Xunit;

namespace AzurPilot.Tests.Capture;

/// <summary>Managed-граница ABI v6: сырые пиксели, BMZ1, JPEG, nemu_ipc и H.264 через production DLL.</summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "Capture")]
public sealed class CaptureNativeSourceTests
{
    [Theory(DisplayName = "Сырые форматы превращаются в RGB8 с правильным порядком каналов")]
    [InlineData(NativeSourcePixelFormat.Rgba8888, new byte[] { 200, 10, 30, 255 }, 4)]
    [InlineData(NativeSourcePixelFormat.Bgra8888, new byte[] { 30, 10, 200, 255 }, 4)]
    [InlineData(NativeSourcePixelFormat.Rgb888, new byte[] { 200, 10, 30 }, 3)]
    [InlineData(NativeSourcePixelFormat.Bgr888, new byte[] { 30, 10, 200 }, 3)]
    public void RawFormatsKeepChannelOrder(NativeSourcePixelFormat format, byte[] pixel, int bytesPerPixel)
    {
        // Строка 2×1 с выравниванием: лишние байты stride не должны попадать в кадр.
        int stride = (2 * bytesPerPixel) + 3;
        byte[] data = new byte[stride];
        pixel.CopyTo(data, 0);
        pixel.CopyTo(data, bytesPerPixel);
        data[^1] = 0xEE;

        using NativeFrame frame = AzurPilotNativeBridge.CreateFrameFromPixels(data, 2, 1, stride, format, false);

        Assert.Equal(new NativeFrameInfo(2, 1, 6, 6, AzurPilotNativeBridge.PixelFormatRgb8), frame.GetInfo());
        RegionRgbStatistics statistics = frame.ScanRegion(new PixelRegion(0, 0, 2, 1));
        Assert.Equal((200d, 10d, 30d), (statistics.MeanRed, statistics.MeanGreen, statistics.MeanBlue));
    }

    [Fact(DisplayName = "RGB565 расширяется до 8 бит, вертикальное отражение меняет порядок строк")]
    public void Rgb565AndVerticalFlip()
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x001F);

        using NativeFrame flipped = AzurPilotNativeBridge.CreateFrameFromPixels(data, 1, 2, 2,
            NativeSourcePixelFormat.Rgb565LittleEndian, true);

        RegionRgbStatistics top = flipped.ScanRegion(new PixelRegion(0, 0, 1, 1));
        RegionRgbStatistics bottom = flipped.ScanRegion(new PixelRegion(0, 1, 1, 2));
        Assert.Equal((0d, 0d, 255d), (top.MeanRed, top.MeanGreen, top.MeanBlue));
        Assert.Equal((255d, 0d, 0d), (bottom.MeanRed, bottom.MeanGreen, bottom.MeanBlue));
    }

    [Fact(DisplayName = "Поворот кадра в DLL: по часовой стрелке, исходный кадр не меняется")]
    public void RotationIsClockwiseAndCopies()
    {
        // Столбец 2×1: красный сверху, синий снизу; после поворота по часовой красный справа.
        byte[] data = [200, 0, 0, 0, 0, 200];
        using NativeFrame source = AzurPilotNativeBridge.CreateFrameFromPixels(data, 1, 2, 3, NativeSourcePixelFormat.Rgb888, false);
        using NativeFrame rotated = source.RotateClockwise(1);

        Assert.Equal((2u, 1u), (rotated.GetInfo().Width, rotated.GetInfo().Height));
        Assert.Equal(200, rotated.ScanRegion(new PixelRegion(1, 0, 2, 1)).MeanRed);
        Assert.Equal(200, rotated.ScanRegion(new PixelRegion(0, 0, 1, 1)).MeanBlue);
        Assert.Equal((1u, 2u), (source.GetInfo().Width, source.GetInfo().Height));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.RotateClockwise(4));
    }

    [Theory(DisplayName = "Неверная геометрия и короткий буфер отвергаются как INVALID_DATA")]
    [InlineData(0, 1, 4, 4)]
    [InlineData(1, 0, 4, 4)]
    [InlineData(2, 1, 4, 8)]
    [InlineData(2, 2, 8, 12)]
    public void InvalidRawGeometryIsRejected(int width, int height, int stride, int length)
    {
        NativeCaptureSourceException exception = Assert.Throws<NativeCaptureSourceException>(() =>
            AzurPilotNativeBridge.CreateFrameFromPixels(new byte[length], width, height, stride,
                NativeSourcePixelFormat.Rgba8888, false));
        Assert.Equal(AzurPilotNativeBridge.StatusInvalidData, exception.StatusCode);
        Assert.True(exception.IsDataFailure);
    }

    [Fact(DisplayName = "BMZ1 и JPEG: мусор и усечённые данные не становятся кадром")]
    public void CompressedFormatsRejectInvalidData()
    {
        byte[] bmz = new byte[AzurPilotNativeBridge.Bmz1HeaderBytes + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bmz, AzurPilotNativeBridge.Bmz1Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bmz.AsSpan(8), 1000);
        _ = Assert.Throws<NativeCaptureSourceException>(() => AzurPilotNativeBridge.DecodeBmz1(bmz));
        _ = Assert.Throws<NativeCaptureSourceException>(() => AzurPilotNativeBridge.DecodeBmz1([1, 2, 3]));
        _ = Assert.Throws<NativeCaptureSourceException>(() => AzurPilotNativeBridge.DecodeJpeg([0xFF, 0xD8, 0xFF, 0x00]));
        _ = Assert.Throws<NativeCaptureSourceException>(() => AzurPilotNativeBridge.DecodeJpeg([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact(DisplayName = "nemu_ipc: отсутствующая DLL — PROVIDER_UNAVAILABLE без сессии")]
    public void NemuMissingLibraryIsUnavailable()
    {
        string folder = Path.Combine(Path.GetTempPath(), "azurpilot-nemu-missing-" + Guid.NewGuid().ToString("N"));
        NativeCaptureSourceException exception = Assert.Throws<NativeCaptureSourceException>(() =>
            AzurPilotNativeBridge.OpenNemu(Path.Combine(folder, "external_renderer_ipc.dll"), folder, 0));
        Assert.Equal(AzurPilotNativeBridge.StatusProviderUnavailable, exception.StatusCode);
    }

    [Fact(DisplayName = "H.264: новый декодер до первого пакета не выдаёт кадр и освобождается")]
    public void VideoDecoderHasNoFrameBeforePackets()
    {
        NativeVideoDecoderSafeHandle decoder;
        try
        {
            decoder = AzurPilotNativeBridge.CreateH264Decoder();
        }
        catch (NativeCaptureSourceException exception)
            when (exception.StatusCode == AzurPilotNativeBridge.StatusProviderUnavailable)
        {
            // Media Foundation может отсутствовать в урезанных редакциях Windows: это UNSUPPORTED, не дефект.
            return;
        }

        using (decoder)
        {
            Assert.Null(AzurPilotNativeBridge.TryGetLatestVideoFrame(decoder, 0, 16, 16, NativeYuvMatrix.Auto, out _, out _));
        }

        Assert.True(decoder.IsClosed);
    }

    [Fact(DisplayName = "Сравнение кадров: совпадение, перестановка R/B и иная геометрия различимы")]
    public void FrameComparisonDetectsChannelSwapAndGeometry()
    {
        using NativeFrame reference = Pattern(64, 36, swap: false);
        using NativeFrame same = Pattern(64, 36, swap: false);
        using NativeFrame swapped = Pattern(64, 36, swap: true);
        using NativeFrame smaller = Pattern(32, 18, swap: false);

        ScreenshotFrameComparison match = ScreenshotFrameComparison.Compare(reference, same);
        Assert.True(match.Matches, match.FailureReason);
        Assert.True(match.ChannelOrderProven);

        ScreenshotFrameComparison swap = ScreenshotFrameComparison.Compare(reference, swapped);
        Assert.False(swap.Matches);
        Assert.Equal("channel_order_mismatch", swap.FailureReason);

        ScreenshotFrameComparison geometry = ScreenshotFrameComparison.Compare(reference, smaller);
        Assert.False(geometry.Matches);
        Assert.Equal("geometry_mismatch", geometry.FailureReason);
    }

    // Узор из четырёх насыщенных цветных полос даёт достаточно хроматических ячеек для доказательства порядка.
    private static NativeFrame Pattern(int width, int height, bool swap)
    {
        (byte R, byte G, byte B)[] colors = [(220, 20, 20), (20, 200, 40), (30, 40, 230), (230, 200, 20)];
        byte[] data = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = colors[(x * colors.Length) / width];
                int offset = ((y * width) + x) * 3;
                data[offset] = swap ? b : r;
                data[offset + 1] = g;
                data[offset + 2] = swap ? r : b;
            }
        }

        return AzurPilotNativeBridge.CreateFrameFromPixels(data, width, height, width * 3, NativeSourcePixelFormat.Rgb888, false);
    }
}
