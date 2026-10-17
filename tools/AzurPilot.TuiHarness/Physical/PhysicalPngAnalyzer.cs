using System.Buffers.Binary;
using System.IO.Compression;

namespace AzurPilot.TuiHarness;

internal readonly record struct PhysicalRgb(byte R, byte G, byte B)
{
    internal int Delta(PhysicalRgb other) => Math.Max(Math.Abs(R - other.R),
        Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));
}

internal readonly record struct PhysicalPixelRectangle(int X, int Y, int Width, int Height);

internal sealed record PhysicalPixelSample(
    string Name,
    PhysicalPixelRectangle Bounds,
    string Kind,
    PhysicalRgb? Expected,
    PhysicalRgb? ExpectedBackground = null);

internal sealed record PhysicalPixelMeasurement(
    string Name,
    string Kind,
    PhysicalPixelRectangle Bounds,
    PhysicalRgb? Expected,
    string Status,
    string MeasurementStatus,
    string ComparisonStatus,
    string Reason,
    PhysicalRgb? ActualRgb,
    int TotalPixels,
    int OpaquePixels,
    int EligiblePixels,
    int MatchingPixels,
    double Frequency,
    int? MaxChannelDelta);

internal sealed class PhysicalPngImage(int width, int height, byte[] rgba)
{
    internal int Width { get; } = width;
    internal int Height { get; } = height;

    internal bool TryGetOpaquePixel(int x, int y, out PhysicalRgb rgb)
    {
        int offset = ((y * Width) + x) * 4;
        rgb = new(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
        return rgba[offset + 3] == byte.MaxValue;
    }
}

/// <summary>Анализирует байты PNG независимо от WinApp, renderer и выбора геометрии.</summary>
internal static class PhysicalPngAnalyzer
{
    private const int MaximumPixels = 16_777_216;
    private const int MaximumPngBytes = 134_217_728;
    private const int MaximumDistinctColors = 4096;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static PhysicalPngImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length > MaximumPngBytes || png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Отсутствует полный поддерживаемый PNG.");
        }

        int width = 0;
        int height = 0;
        int channels = 0;
        int offset = 8;
        bool sawHeader = false;
        bool sawData = false;
        bool endedData = false;
        bool sawEnd = false;
        bool sawPalette = false;
        using MemoryStream compressed = new();
        while (offset < png.Length)
        {
            if (png.Length - offset < 12)
            {
                throw new InvalidDataException("Оборван заголовок PNG chunk.");
            }

            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (rawLength > (uint)(png.Length - offset - 12))
            {
                throw new InvalidDataException("PNG chunk выходит за границы файла.");
            }

            int length = (int)rawLength;
            ReadOnlySpan<byte> type = png.AsSpan(offset + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(offset + 8, length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            if (!IsChunkType(type) || ComputeCrc(png.AsSpan(offset + 4, length + 4)) != expectedCrc)
            {
                throw new InvalidDataException("Повреждены имя или CRC PNG chunk.");
            }

            if (!sawHeader && !type.SequenceEqual("IHDR"u8))
            {
                throw new InvalidDataException("Первым PNG chunk должен быть IHDR.");
            }

            if (sawData && !type.SequenceEqual("IDAT"u8))
            {
                endedData = true;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || length != 13)
                {
                    throw new InvalidDataException("Неверный или повторный IHDR.");
                }

                uint rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                uint rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (rawWidth is 0 or > 32768 || rawHeight is 0 or > 32768
                    || ((ulong)rawWidth * rawHeight) > MaximumPixels)
                {
                    throw new InvalidDataException("Размер PNG пуст или превышает ограничение памяти.");
                }

                if (data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("Поддерживается только 8-bit RGB/RGBA PNG без interlace.");
                }

                width = (int)rawWidth;
                height = (int)rawHeight;
                channels = data[9] == 2 ? 3 : 4;
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (endedData)
                {
                    throw new InvalidDataException("PNG IDAT chunks должны быть последовательными.");
                }

                compressed.Write(data);
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !sawData || offset + 12 != png.Length)
                {
                    throw new InvalidDataException("Неверное завершение PNG.");
                }

                sawEnd = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (sawPalette || sawData || length is 0 or > 768 || length % 3 != 0)
                {
                    throw new InvalidDataException("Неверный или повторный PLTE.");
                }

                sawPalette = true;
            }
            else if (type.SequenceEqual("tRNS"u8) || (type[0] & 32) == 0)
            {
                throw new InvalidDataException("PNG содержит неподдерживаемый critical chunk или tRNS.");
            }

            offset += length + 12;
        }

        if (!sawEnd || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG не содержит полного IDAT/IEND.");
        }

        ReadOnlySpan<byte> zlibBytes = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        if (zlibBytes.Length < 6 || (zlibBytes[0] & 15) != 8 || (zlibBytes[0] >> 4) > 7
            || (((zlibBytes[0] << 8) | zlibBytes[1]) % 31) != 0 || (zlibBytes[1] & 32) != 0)
        {
            throw new InvalidDataException("Повреждён или не поддерживается PNG zlib header.");
        }

        int stride = checked(width * channels);
        int inflatedLength = checked((stride + 1) * height);
        byte[] filtered = new byte[inflatedLength];
        compressed.Position = 0;
        try
        {
            using ZLibStream inflater = new(compressed, CompressionMode.Decompress, leaveOpen: true);
            inflater.ReadExactly(filtered);
            if (inflater.ReadByte() != -1)
            {
                throw new InvalidDataException("Декомпрессированный PNG превышает размер IHDR.");
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Декомпрессированный PNG короче размера IHDR.", exception);
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(zlibBytes[^4..]) != ComputeAdler(filtered))
        {
            throw new InvalidDataException("Повреждён PNG zlib Adler-32 trailer.");
        }

        byte[] pixels = new byte[checked(width * height * 4)];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * (stride + 1);
            byte filter = filtered[rowOffset];
            if (filter > 4)
            {
                throw new InvalidDataException("Неподдерживаемый PNG row filter.");
            }

            for (int index = 0; index < stride; index++)
            {
                int left = index >= channels ? current[index - channels] : 0;
                int above = previous[index];
                int upperLeft = index >= channels ? previous[index - channels] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    _ => Paeth(left, above, upperLeft),
                };
                current[index] = unchecked((byte)(filtered[rowOffset + index + 1] + predictor));
            }

            for (int x = 0; x < width; x++)
            {
                int source = x * channels;
                int target = ((y * width) + x) * 4;
                pixels[target] = current[source];
                pixels[target + 1] = current[source + 1];
                pixels[target + 2] = current[source + 2];
                pixels[target + 3] = channels == 4 ? current[source + 3] : byte.MaxValue;
            }

            (previous, current) = (current, previous);
        }

        return new(width, height, pixels);
    }

    internal static IReadOnlyList<PhysicalPixelMeasurement> Analyze(
        PhysicalPngImage image, IReadOnlyList<PhysicalPixelSample> samples)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count is 0 or > 256)
        {
            throw new ArgumentException("Нужно от 1 до 256 семантических областей.", nameof(samples));
        }

        long totalArea = 0;
        foreach (PhysicalPixelSample sample in samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            PhysicalPixelRectangle bounds = sample.Bounds;
            if (string.IsNullOrWhiteSpace(sample.Name) || sample.Name.Length > 128
                || sample.Kind is not ("background" or "foreground")
                || (sample.Kind == "foreground" && (sample.ExpectedBackground is null
                    || sample.Expected is not null && sample.ExpectedBackground == sample.Expected))
                || bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
                || ((long)bounds.X + bounds.Width) > image.Width
                || ((long)bounds.Y + bounds.Height) > image.Height)
            {
                throw new ArgumentException("Неверные семантика, цвет или границы pixel sample.", nameof(samples));
            }

            totalArea += (long)bounds.Width * bounds.Height;
        }

        if (totalArea > MaximumPixels * 4L)
        {
            throw new ArgumentException("Общая площадь pixel samples превышает ограничение анализа.", nameof(samples));
        }

        return [.. samples.Select(sample => Measure(image, sample))];
    }

    private static PhysicalPixelMeasurement Measure(PhysicalPngImage image, PhysicalPixelSample sample)
    {
        PhysicalPixelRectangle bounds = sample.Bounds;
        Dictionary<PhysicalRgb, int> colors = [];
        int opaque = 0;
        int eligible = 0;
        int matching = 0;
        bool tooManyColors = false;
        for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            for (int x = bounds.X; x < bounds.X + bounds.Width; x++)
            {
                if (!image.TryGetOpaquePixel(x, y, out PhysicalRgb rgb))
                {
                    continue;
                }

                opaque++;
                // Центр однородного блока не является сглаженным краем глифа.
                if (sample.Kind == "foreground" && (rgb == sample.ExpectedBackground
                    || !IsInterior(image, bounds, x, y, rgb)))
                {
                    continue;
                }

                eligible++;
                if (sample.Expected is PhysicalRgb expected && rgb == expected)
                {
                    matching++;
                }

                if (colors.TryGetValue(rgb, out int count))
                {
                    colors[rgb] = count + 1;
                }
                else if (colors.Count < MaximumDistinctColors)
                {
                    colors.Add(rgb, 1);
                }
                else
                {
                    tooManyColors = true;
                }
            }
        }

        PhysicalRgb? actual = null;
        int dominantCount = 0;
        bool tied = false;
        foreach (KeyValuePair<PhysicalRgb, int> color in colors)
        {
            if (color.Value > dominantCount)
            {
                dominantCount = color.Value;
                actual = color.Key;
                tied = false;
            }
            else if (color.Value == dominantCount)
            {
                tied = true;
            }
        }

        int total = bounds.Width * bounds.Height;
        string measurementStatus;
        string comparisonStatus;
        string reason;
        if (opaque != total)
        {
            measurementStatus = "NOT_TESTED";
            comparisonStatus = "NOT_TESTED";
            reason = "Область содержит прозрачные пиксели; конечный RGB после композиции неизвестен.";
        }
        else if (eligible == 0 || tied || tooManyColors)
        {
            measurementStatus = "NOT_TESTED";
            comparisonStatus = "NOT_TESTED";
            reason = "Нет однозначного внутреннего foreground RGB или преобладающего background RGB.";
        }
        else
        {
            measurementStatus = "PASS";
            comparisonStatus = sample.Expected is null ? "NOT_TESTED"
                : actual == sample.Expected ? "PASS" : "FAIL";
            reason = sample.Expected is null
                ? "Фактический RGB измерен; ожидаемый цвет не определён достоверно."
                : comparisonStatus == "PASS"
                    ? "Измеренный RGB совпадает с ожидаемым цветом семантической области."
                    : "Измеренный RGB семантической области отличается от ожидаемого.";
        }

        return new(sample.Name, sample.Kind, bounds, sample.Expected, comparisonStatus, measurementStatus,
            comparisonStatus, reason,
            tied || tooManyColors ? null : actual, total, opaque, eligible, matching,
            eligible == 0 ? 0 : (double)matching / eligible,
            tied || tooManyColors || sample.Expected is not PhysicalRgb expectedColor
                ? null : actual?.Delta(expectedColor));
    }

    private static bool IsInterior(PhysicalPngImage image, PhysicalPixelRectangle bounds,
        int x, int y, PhysicalRgb color)
    {
        if (x == bounds.X || y == bounds.Y || x == bounds.X + bounds.Width - 1
            || y == bounds.Y + bounds.Height - 1)
        {
            return false;
        }

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!image.TryGetOpaquePixel(x + dx, y + dy, out PhysicalRgb neighbor) || neighbor != color)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (byte value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return (type[2] & 32) == 0;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (int index = 0; index < table.Length; index++)
        {
            uint crc = (uint)index;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }

            table[index] = crc;
        }

        return table;
    }

    private static uint ComputeAdler(ReadOnlySpan<byte> data)
    {
        uint first = 1;
        uint second = 0;
        while (!data.IsEmpty)
        {
            int length = Math.Min(data.Length, 5552);
            foreach (byte value in data[..length])
            {
                first += value;
                second += first;
            }

            first %= 65521;
            second %= 65521;
            data = data[length..];
        }

        return (second << 16) | first;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int prediction = left + above - upperLeft;
        int leftDistance = Math.Abs(prediction - left);
        int aboveDistance = Math.Abs(prediction - above);
        int upperLeftDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }
}
