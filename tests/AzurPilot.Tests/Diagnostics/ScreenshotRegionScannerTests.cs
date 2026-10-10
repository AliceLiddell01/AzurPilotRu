using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using Xunit;

namespace AzurPilot.Tests.Diagnostics;

[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ScreenshotRegionScannerTests
{
    [Fact(DisplayName = "Scan возвращает полуоткрытые bounds и RGB population statistics")]
    public void ScanReturnsStructuredStatisticsForLocalSample()
    {
        byte[] png = CreatePatternPng(16, 9);
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
        ScreenshotAnalysisSource source = ScreenshotAnalysisSource.ForLocalSample((uint)png.Length);

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            source,
            ScreenshotDiagnosticRegion.Center);

        Assert.True(result.IsSuccess);
        ScreenshotRegionScanResult scan = result.Value!;
        Assert.Equal(source, scan.Source);
        Assert.Equal(ScreenshotAnalysisSourceKind.LocalSample, scan.Source.Kind);
        Assert.Null(scan.Source.InstanceId);
        Assert.Null(scan.Source.Endpoint);
        Assert.Null(scan.Source.CapturedAtUtc);
        Assert.Equal((uint)png.Length, scan.Source.EncodedPngByteCount);
        Assert.Equal(16u, scan.FrameWidth);
        Assert.Equal(9u, scan.FrameHeight);
        Assert.Equal("RGB8", scan.PixelFormat);
        Assert.Equal(ScreenshotDiagnosticRegion.Center, scan.Region);
        Assert.Equal("Центральная область", scan.RegionName);
        Assert.Equal(new ScreenshotLogicalBounds(427, 241, 853, 479), scan.RequestedLogicalBounds);
        Assert.Equal(new PixelRegion(5, 3, 11, 6), scan.PhysicalPixelBounds);
        Assert.Equal(18ul, scan.PixelCount);
        AssertClose(75, scan.MeanRed);
        AssertClose(80, scan.MeanGreen);
        AssertClose(11.5, scan.MeanBlue);
        AssertClose(Math.Sqrt(100 * 35d / 12), scan.StandardDeviationRed);
        AssertClose(Math.Sqrt(400 * 2d / 3), scan.StandardDeviationGreen);
        AssertClose(Math.Sqrt(43d / 12), scan.StandardDeviationBlue);
    }

    [Fact(DisplayName = "Android provenance сохраняет exact identity, endpoint, UTC и native dimensions")]
    public void CaptureSourcePreservesBoundedProvenanceInScanResult()
    {
        byte[] png = CreatePatternPng(16, 9);
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
        DateTimeOffset capturedAtUtc = new(2026, 10, 11, 12, 30, 0, TimeSpan.Zero);
        AndroidScreenshotCaptureInfo captureInfo = new(
            new MuMuInstanceId("3"),
            new AndroidEndpoint("127.0.0.1", 16384),
            capturedAtUtc,
            TimeSpan.FromMilliseconds(47),
            png.Length,
            0,
            new NativeFrameInfo(16, 9, 48, 432, AzurPilotNativeBridge.PixelFormatRgb8));
        ScreenshotAnalysisSource source = ScreenshotAnalysisSource.ForCapture(captureInfo);

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            source,
            ScreenshotDiagnosticRegion.FullFrame);

        Assert.True(result.IsSuccess);
        ScreenshotAnalysisSource scanSource = result.Value!.Source;
        Assert.Equal(ScreenshotAnalysisSourceKind.AndroidCapture, scanSource.Kind);
        Assert.Equal(captureInfo.InstanceId, scanSource.InstanceId);
        Assert.Equal(captureInfo.Endpoint, scanSource.Endpoint);
        Assert.Equal(capturedAtUtc, scanSource.CapturedAtUtc);
        Assert.Equal((uint)png.Length, scanSource.EncodedPngByteCount);
        Assert.Equal(captureInfo.FrameInfo, scanSource.CapturedFrameInfo);
        Assert.Equal(new PixelRegion(0, 0, 16, 9), result.Value.PhysicalPixelBounds);
        Assert.Equal(16u, result.Value.FrameWidth);
        Assert.Equal(9u, result.Value.FrameHeight);
        Assert.Equal(144ul, result.Value.PixelCount);
    }

    [Theory(DisplayName = "Portrait и неподдерживаемый aspect ratio отклоняются до native ROI")]
    [InlineData(9, 16)]
    [InlineData(16, 10)]
    public void ScannerRejectsUnsupportedAspectRatio(int width, int height)
    {
        byte[] png = CreatePatternPng(width, height);
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            ScreenshotAnalysisSource.ForLocalSample((uint)png.Length),
            ScreenshotDiagnosticRegion.FullFrame);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, result.FailureInfo!.Code);
        Assert.Equal("frame_aspect_ratio_unsupported", result.FailureInfo.Details!["reason"]);
        Assert.Equal(width.ToString(), result.FailureInfo.Details["frame_width"]);
        Assert.Equal(height.ToString(), result.FailureInfo.Details["frame_height"]);
    }

    [Fact(DisplayName = "Capture provenance с metadata другого кадра fail-closed отклоняется")]
    public void ScannerRejectsMismatchedCaptureFrameMetadata()
    {
        byte[] png = CreatePatternPng(16, 9);
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
        AndroidScreenshotCaptureInfo captureInfo = CreateCaptureInfo(
            png.Length,
            new NativeFrameInfo(32, 18, 96, 1728, AzurPilotNativeBridge.PixelFormatRgb8));

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            ScreenshotAnalysisSource.ForCapture(captureInfo),
            ScreenshotDiagnosticRegion.FullFrame);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, result.FailureInfo!.Code);
        Assert.Equal("capture_frame_metadata_mismatch", result.FailureInfo.Details!["reason"]);
    }

    [Fact(DisplayName = "Disposed frame даёт bounded отказ NativeFrameAnalysisFailed")]
    public void ScannerRejectsDisposedFrameWithoutNativeAccess()
    {
        byte[] png = CreatePatternPng(16, 9);
        NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
        frame.Dispose();

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            ScreenshotAnalysisSource.ForLocalSample((uint)png.Length),
            ScreenshotDiagnosticRegion.Center);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.NativeFrameAnalysisFailed, result.FailureInfo!.Code);
        Assert.Equal("frame_disposed", result.FailureInfo.Details!["reason"]);
        Assert.Equal(nameof(ObjectDisposedException), result.FailureInfo.Details["exception_type"]);
        Assert.DoesNotContain(png.Length.ToString(), result.FailureInfo.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Неизвестная область возвращается как ограниченный structured отказ")]
    public void ScannerRejectsUnknownRegion()
    {
        byte[] png = CreatePatternPng(16, 9);
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);

        ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(
            frame,
            ScreenshotAnalysisSource.ForLocalSample((uint)png.Length),
            (ScreenshotDiagnosticRegion)int.MaxValue);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, result.FailureInfo!.Code);
        Assert.Equal("region_unknown", result.FailureInfo.Details!["reason"]);
    }

    [Fact(DisplayName = "Source factories не принимают пустой PNG")]
    public void SourceFactoriesRejectZeroEncodedByteCount()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotAnalysisSource.ForLocalSample(0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotAnalysisSource.ForCapture(
            CreateCaptureInfo(0, new NativeFrameInfo(16, 9, 48, 432, AzurPilotNativeBridge.PixelFormatRgb8))));
    }

    private static AndroidScreenshotCaptureInfo CreateCaptureInfo(int encodedByteCount, NativeFrameInfo frameInfo)
        => new(
            new MuMuInstanceId("3"),
            new AndroidEndpoint("127.0.0.1", 16384),
            new DateTimeOffset(2026, 10, 11, 12, 30, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(47),
            encodedByteCount,
            0,
            frameInfo);

    private static void AssertClose(double expected, double actual)
        => Assert.InRange(Math.Abs(expected - actual), 0, 0.000001);

    private static byte[] CreatePatternPng(int width, int height)
        => ScreenshotPngFixture.CreateRgb(
            width,
            height,
            (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)(x + y)));
}

internal static class ScreenshotPngFixture
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] CreateRgb(
        int width,
        int height,
        Func<int, int, (byte Red, byte Green, byte Blue)> pixelAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixelAt);

        using MemoryStream png = new();
        png.Write(Signature);

        byte[] imageHeader = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(imageHeader.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(imageHeader.AsSpan(4, 4), checked((uint)height));
        imageHeader[8] = 8;
        imageHeader[9] = 2;
        WriteChunk(png, "IHDR", imageHeader);

        int rowLength = checked((width * 3) + 1);
        byte[] uncompressed = new byte[checked(rowLength * height)];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = checked(y * rowLength);
            uncompressed[rowOffset] = 0;
            for (int x = 0; x < width; x++)
            {
                (byte red, byte green, byte blue) = pixelAt(x, y);
                int pixelOffset = checked(rowOffset + 1 + (x * 3));
                uncompressed[pixelOffset] = red;
                uncompressed[pixelOffset + 1] = green;
                uncompressed[pixelOffset + 2] = blue;
            }
        }

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }

        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        WriteUInt32(output, checked((uint)data.Length));
        output.Write(typeBytes);
        output.Write(data);
        WriteUInt32(output, ComputeCrc(typeBytes, data));
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return crc ^ uint.MaxValue;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return crc;
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
