using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AzurPilot.TuiHarness;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class PhysicalPngAnalyzerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_ReconstructsIndependentRgbScanlineFixtures(int filter)
    {
        // Остатки заданы явно: тестовый образец не содержит копии продуктового кодировщика фильтров.
        byte[][] rows = filter switch
        {
            0 => [[10, 20, 30, 40, 50, 60], [70, 80, 90, 100, 110, 120]],
            1 => [[10, 20, 30, 30, 30, 30], [70, 80, 90, 30, 30, 30]],
            2 => [[10, 20, 30, 40, 50, 60], [60, 60, 60, 60, 60, 60]],
            3 => [[10, 20, 30, 35, 40, 45], [65, 70, 75, 45, 45, 45]],
            _ => [[10, 20, 30, 30, 30, 30], [60, 60, 60, 30, 30, 30]],
        };
        byte[] raw = [(byte)filter, .. rows[0], (byte)filter, .. rows[1]];

        PhysicalPngImage image = PhysicalPngAnalyzer.Decode(CreatePng(2, 2, raw));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.True(image.TryGetOpaquePixel(0, 0, out PhysicalRgb first));
        Assert.Equal(new PhysicalRgb(10, 20, 30), first);
        Assert.True(image.TryGetOpaquePixel(1, 0, out PhysicalRgb second));
        Assert.Equal(new PhysicalRgb(40, 50, 60), second);
        Assert.True(image.TryGetOpaquePixel(0, 1, out PhysicalRgb third));
        Assert.Equal(new PhysicalRgb(70, 80, 90), third);
        Assert.True(image.TryGetOpaquePixel(1, 1, out PhysicalRgb fourth));
        Assert.Equal(new PhysicalRgb(100, 110, 120), fourth);
    }

    [Fact]
    public void Decode_SubFilterWrapsAndUsesFourByteRgbaStride()
    {
        byte[] raw = [1, 250, 220, 200, 255, 16, 56, 76, 129];

        PhysicalPngImage image = PhysicalPngAnalyzer.Decode(CreatePng(2, 1, raw, colorType: 6));

        Assert.True(image.TryGetOpaquePixel(0, 0, out PhysicalRgb first));
        Assert.Equal(new PhysicalRgb(250, 220, 200), first);
        Assert.False(image.TryGetOpaquePixel(1, 0, out PhysicalRgb second));
        Assert.Equal(new PhysicalRgb(10, 20, 20), second);
    }

    [Fact]
    public void Decode_AcceptsConsecutiveDataChunksAndChecksTheirCombinedStream()
    {
        byte[] compressed = Compress([0, 7, 8, 9]);
        byte[] png = Assemble(
            ("IHDR", Header(1, 1)),
            ("IDAT", compressed[..3]),
            ("IDAT", compressed[3..]),
            ("IEND", []));

        PhysicalPngImage image = PhysicalPngAnalyzer.Decode(png);

        Assert.True(image.TryGetOpaquePixel(0, 0, out PhysicalRgb pixel));
        Assert.Equal(new PhysicalRgb(7, 8, 9), pixel);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(32768, 32768)]
    public void Decode_RejectsEmptyOversizedAndOverflowingDimensions(int width, int height)
    {
        byte[] png = CreatePng(width, height, [0, 1, 2, 3]);

        _ = Assert.Throws<InvalidDataException>(() => PhysicalPngAnalyzer.Decode(png));
    }

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(3, 8, 0)]
    [InlineData(2, 16, 0)]
    [InlineData(6, 8, 1)]
    public void Decode_RejectsUnsupportedColorDepthAndInterlace(int colorType, int depth, int interlace)
    {
        byte[] png = CreatePng(1, 1, [0, 1, 2, 3], (byte)colorType, (byte)depth, (byte)interlace);

        _ = Assert.Throws<InvalidDataException>(() => PhysicalPngAnalyzer.Decode(png));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("signature")]
    [InlineData("truncatedChunk")]
    [InlineData("oversizedChunk")]
    [InlineData("crc")]
    [InlineData("trailingData")]
    public void Decode_RejectsMalformedContainer(string fixture)
    {
        byte[] png = CreatePng(1, 1, [0, 1, 2, 3]);
        switch (fixture)
        {
            case "empty":
                png = [];
                break;
            case "signature":
                png[0] = 0;
                break;
            case "truncatedChunk":
                png = png[..^5];
                break;
            case "oversizedChunk":
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), uint.MaxValue);
                break;
            case "crc":
                png[29] ^= 1;
                break;
            default:
                png = [.. png, 0];
                break;
        }

        _ = Assert.Throws<InvalidDataException>(() => PhysicalPngAnalyzer.Decode(png));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("excess")]
    [InlineData("invalidFilter")]
    [InlineData("brokenZlib")]
    [InlineData("missingTrailer")]
    [InlineData("invalidAdler")]
    public void Decode_RejectsInvalidCompressedPixels(string fixture)
    {
        byte[] validData = Compress([0, 1, 2, 3]);
        byte[] data = fixture switch
        {
            "short" => Compress([0, 1, 2]),
            "excess" => Compress([0, 1, 2, 3, 4]),
            "invalidFilter" => Compress([5, 1, 2, 3]),
            "missingTrailer" => validData[..^4],
            "invalidAdler" => [.. validData[..^1], (byte)(validData[^1] ^ 1)],
            _ => [0, 0, 0, 0],
        };
        byte[] png = Assemble(("IHDR", Header(1, 1)), ("IDAT", data), ("IEND", []));

        _ = Assert.Throws<InvalidDataException>(() => PhysicalPngAnalyzer.Decode(png));
    }

    [Theory]
    [InlineData("unknownCritical")]
    [InlineData("transparentRgb")]
    [InlineData("nonConsecutiveData")]
    [InlineData("duplicateHeader")]
    [InlineData("missingEnd")]
    [InlineData("headerNotFirst")]
    public void Decode_RejectsUnsupportedChunksAndInvalidOrdering(string fixture)
    {
        byte[] compressed = Compress([0, 1, 2, 3]);
        byte[] header = Header(1, 1);
        byte[] png = fixture switch
        {
            "unknownCritical" => Assemble(("IHDR", header), ("ABCD", []),
                ("IDAT", compressed), ("IEND", [])),
            "transparentRgb" => Assemble(("IHDR", header), ("tRNS", [0, 1, 0, 2, 0, 3]),
                ("IDAT", compressed), ("IEND", [])),
            "nonConsecutiveData" => Assemble(("IHDR", header), ("IDAT", compressed[..3]),
                ("tEXt", []), ("IDAT", compressed[3..]), ("IEND", [])),
            "duplicateHeader" => Assemble(("IHDR", header), ("IHDR", header),
                ("IDAT", compressed), ("IEND", [])),
            "missingEnd" => Assemble(("IHDR", header), ("IDAT", compressed)),
            _ => Assemble(("tEXt", []), ("IHDR", header), ("IDAT", compressed), ("IEND", [])),
        };

        _ = Assert.Throws<InvalidDataException>(() => PhysicalPngAnalyzer.Decode(png));
    }

    [Fact]
    public void Analyze_ReportsActualDominantBackgroundAndExactPixelFrequency()
    {
        PhysicalPngImage image = RgbaFixture(5, 5,
            (x, y) => x == 2 && y == 2 ? (255, 255, 255, 255) : (10, 20, 30, 255));
        PhysicalPixelSample sample = new("history background", new(0, 0, 5, 5),
            "background", new(10, 20, 30));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("PASS", result.Status);
        Assert.Equal(new PhysicalRgb(10, 20, 30), result.ActualRgb);
        Assert.Equal(25, result.TotalPixels);
        Assert.Equal(25, result.OpaquePixels);
        Assert.Equal(25, result.EligiblePixels);
        Assert.Equal(24, result.MatchingPixels);
        Assert.Equal(0.96, result.Frequency, precision: 8);
        Assert.Equal(0, result.MaxChannelDelta);
    }

    [Fact]
    public void Analyze_ReportsMeasuredMismatchAndMaximumChannelDelta()
    {
        PhysicalPngImage image = RgbaFixture(3, 3, (_, _) => (10, 20, 30, 255));
        PhysicalPixelSample sample = new("selection", new(1, 1, 2, 2), "background", new(1, 2, 3));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("FAIL", result.Status);
        Assert.Equal(new PhysicalRgb(10, 20, 30), result.ActualRgb);
        Assert.Equal(4, result.TotalPixels);
        Assert.Equal(0, result.MatchingPixels);
        Assert.Equal(0, result.Frequency);
        Assert.Equal(27, result.MaxChannelDelta);
        Assert.Equal("PASS", result.MeasurementStatus);
        Assert.Equal("FAIL", result.ComparisonStatus);
    }

    [Fact]
    public void Analyze_ReportsActualRgbWithoutClaimingAComparisonWhenExpectedPaletteIsUnknown()
    {
        PhysicalPngImage image = RgbaFixture(5, 5, (_, _) => (30, 27, 24, 255));
        PhysicalPixelSample sample = new("current-profile background", new(0, 0, 5, 5), "background", Expected: null);

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("PASS", result.MeasurementStatus);
        Assert.Equal("NOT_TESTED", result.ComparisonStatus);
        Assert.Equal("NOT_TESTED", result.Status);
        Assert.Equal(new PhysicalRgb(30, 27, 24), result.ActualRgb);
        Assert.Null(result.Expected);
        Assert.Null(result.MaxChannelDelta);
    }

    [Fact]
    public void Analyze_ReportsUniformForegroundDifferenceWithoutMatchingExpectedColor()
    {
        PhysicalPngImage image = RgbaFixture(7, 7, (x, y) =>
            x is >= 1 and <= 5 && y is >= 1 and <= 5 ? (150, 160, 170, 255) : (10, 20, 30, 255));
        PhysicalPixelSample sample = new("primary text", new(0, 0, 7, 7),
            "foreground", new(200, 210, 220), new(10, 20, 30));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("FAIL", result.Status);
        Assert.Equal(new PhysicalRgb(150, 160, 170), result.ActualRgb);
        Assert.Equal(9, result.EligiblePixels);
        Assert.Equal(0, result.MatchingPixels);
        Assert.Equal(50, result.MaxChannelDelta);
    }

    [Fact]
    public void Analyze_UsesOnlyTheRequestedSemanticRectangle()
    {
        PhysicalPngImage image = RgbaFixture(6, 3,
            (x, _) => x < 3 ? (5, 6, 7, 255) : (80, 90, 100, 255));
        PhysicalPixelSample sample = new("right pane", new(3, 0, 3, 3), "background", new(80, 90, 100));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("PASS", result.Status);
        Assert.Equal(9, result.MatchingPixels);
        Assert.Equal(new PhysicalRgb(80, 90, 100), result.ActualRgb);
    }

    [Fact]
    public void Analyze_UsesOnlyOpaqueUniformInteriorForegroundPixels()
    {
        PhysicalPngImage image = RgbaFixture(7, 7, (x, y) =>
            x is >= 1 and <= 5 && y is >= 1 and <= 5 ? (200, 210, 220, 255) : (90, 100, 110, 255));
        PhysicalPixelSample sample = new("command text", new(0, 0, 7, 7),
            "foreground", new(200, 210, 220), new(90, 100, 110));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("PASS", result.Status);
        Assert.Equal(9, result.EligiblePixels);
        Assert.Equal(9, result.MatchingPixels);
        Assert.Equal(1, result.Frequency);
        Assert.Equal(0, result.MaxChannelDelta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_AntialiasOnlyOrIsolatedExactGlyphPixelIsNotTested(bool isolatedExactPixel)
    {
        PhysicalPngImage image = RgbaFixture(5, 5, (x, y) => isolatedExactPixel && x == 2 && y == 2
            ? (200, 210, 220, 255) : (100, 110, 120, 255));
        PhysicalPixelSample sample = new("thin glyph", new(0, 0, 5, 5),
            "foreground", new(200, 210, 220), new(100, 110, 120));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("NOT_TESTED", result.Status);
        Assert.Equal(0, result.MatchingPixels);
        Assert.Equal(0, result.Frequency);
    }

    [Fact]
    public void Analyze_BackgroundPixelsCannotProveForegroundColor()
    {
        PhysicalPngImage image = RgbaFixture(5, 5, (_, _) => (10, 20, 30, 255));
        PhysicalPixelSample sample = new("missing glyph", new(0, 0, 5, 5),
            "foreground", new(200, 210, 220), new(10, 20, 30));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("NOT_TESTED", result.Status);
        Assert.Equal(0, result.EligiblePixels);
        Assert.Null(result.ActualRgb);
        Assert.Null(result.MaxChannelDelta);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void Analyze_TransparentAndPartiallyTransparentPixelsCannotProveRgb(int alpha)
    {
        PhysicalPngImage image = RgbaFixture(3, 3,
            (x, y) => (10, 20, 30, x == 1 && y == 1 ? alpha : 255));
        PhysicalPixelSample sample = new("transparent background", new(0, 0, 3, 3),
            "background", new(10, 20, 30));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("NOT_TESTED", result.Status);
        Assert.Equal(8, result.OpaquePixels);
        Assert.Equal(8, result.MatchingPixels);
    }

    [Fact]
    public void Analyze_EquallyFrequentColorsAreAmbiguous()
    {
        PhysicalPngImage image = RgbaFixture(2, 2,
            (x, _) => x == 0 ? (10, 20, 30, 255) : (40, 50, 60, 255));
        PhysicalPixelSample sample = new("ambiguous region", new(0, 0, 2, 2),
            "background", new(10, 20, 30));

        PhysicalPixelMeasurement result = Assert.Single(PhysicalPngAnalyzer.Analyze(image, [sample]));

        Assert.Equal("NOT_TESTED", result.Status);
        Assert.Null(result.ActualRgb);
        Assert.Null(result.MaxChannelDelta);
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(2, 0, 2, 1)]
    [InlineData(0, 2, 1, 2)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 1)]
    public void Analyze_RejectsEmptyInvalidOrOverflowingSampleBounds(int x, int y, int width, int height)
    {
        PhysicalPngImage image = RgbaFixture(3, 3, (_, _) => (10, 20, 30, 255));
        PhysicalPixelSample sample = new("region", new(x, y, width, height),
            "background", new(10, 20, 30));

        _ = Assert.Throws<ArgumentException>(() => PhysicalPngAnalyzer.Analyze(image, [sample]));
    }

    [Fact]
    public void Analyze_RejectsEmptySamplesAndUnknownOrAmbiguousSemantics()
    {
        PhysicalPngImage image = RgbaFixture(3, 3, (_, _) => (10, 20, 30, 255));
        PhysicalPixelSample unknown = new("region", new(0, 0, 3, 3), "unknown", new(10, 20, 30));
        PhysicalPixelSample missingBackground = unknown with { Kind = "foreground" };
        PhysicalPixelSample sameColors = missingBackground with { ExpectedBackground = missingBackground.Expected };

        _ = Assert.Throws<ArgumentException>(() => PhysicalPngAnalyzer.Analyze(image, []));
        _ = Assert.Throws<ArgumentException>(() => PhysicalPngAnalyzer.Analyze(image, [unknown]));
        _ = Assert.Throws<ArgumentException>(() => PhysicalPngAnalyzer.Analyze(image, [missingBackground]));
        _ = Assert.Throws<ArgumentException>(() => PhysicalPngAnalyzer.Analyze(image, [sameColors]));
    }

    private static PhysicalPngImage RgbaFixture(int width, int height,
        Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        using MemoryStream rows = new();
        for (int y = 0; y < height; y++)
        {
            rows.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                (int red, int green, int blue, int alpha) = pixel(x, y);
                rows.Write([(byte)red, (byte)green, (byte)blue, (byte)alpha]);
            }
        }

        return PhysicalPngAnalyzer.Decode(CreatePng(width, height, rows.ToArray(), colorType: 6));
    }

    private static byte[] CreatePng(int width, int height, byte[] raw,
        byte colorType = 2, byte depth = 8, byte interlace = 0)
    {
        byte[] header = Header(width, height);
        header[8] = depth;
        header[9] = colorType;
        header[12] = interlace;
        return Assemble(("IHDR", header), ("IDAT", Compress(raw)), ("IEND", []));
    }

    private static byte[] Header(int width, int height)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        return header;
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] Assemble(params (string Type, byte[] Data)[] chunks)
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        foreach ((string type, byte[] data) in chunks)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            byte[] crcInput = [.. Encoding.ASCII.GetBytes(type), .. data];
            output.Write(crcInput);
            uint crc = uint.MaxValue;
            foreach (byte value in crcInput)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
                }
            }

            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            output.Write(length);
        }

        return output.ToArray();
    }
}
