using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AzurPilot.Windows;
using AzurPilot.Windows.Game;
using Xunit;

namespace AzurPilot.Tests.Game;

[Collection(InteropCollection.Name)]
public sealed class GameProgressBarTests
{
    private static readonly GameProgressBar Bar = GameAssetCatalog.Default.GetAsset("loading.download").Progress!;

    [Theory]
    [InlineData("progress-real-full", 1.0)]
    [InlineData("progress-real-partial", 0.0)]
    public void RealBarsFromIndependentScreensMeasureFilledShare(string fixture, double expected)
    {
        // Полная полоса обычного входа и начало реального скачивания (граница внутри первого сегмента).
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", fixture + "-2160.png")));
        Assert.Equal(expected, GameUiWindowsHost.MeasureFill(frame, new(3840, 2160), Bar));
    }

    [Theory]
    [InlineData(720, 0.25)]
    [InlineData(1080, 0.5)]
    [InlineData(2160, 0.5)]
    [InlineData(2160, 0)]
    [InlineData(2160, 1)]
    public void MeasuredColorsGiveSegmentShareAtEveryScale(int height, double share)
    {
        int width = height * 16 / 9;
        using NativeFrame frame = Frame(width, height, share, track: 20);
        Assert.Equal(share, GameUiWindowsHost.MeasureFill(frame, new(width, height), Bar));
    }

    [Fact]
    public void BrightnessOfTrackAndBackgroundDoesNotChangeFilledShare()
    {
        foreach (byte track in new byte[] { 5, 30, 60 })
        {
            using NativeFrame frame = Frame(3840, 2160, 0.375, track);
            Assert.Equal(0.375, GameUiWindowsHost.MeasureFill(frame, new(3840, 2160), Bar));
        }
    }

    [Theory]
    [InlineData("alternating")]
    [InlineData("bright-background")]
    [InlineData("gap")]
    public void ImpossibleBarPictureIsUnknownRatherThanProgress(string pattern)
    {
        using NativeFrame frame = Frame(3840, 2160, 0.5, track: 20, pattern);
        Assert.Null(GameUiWindowsHost.MeasureFill(frame, new(3840, 2160), Bar));
    }

    /// <summary>
    /// Кадр с полосой из измеренных цветов настоящих 4K-кадров: заполнение — градиент (94,155,255)→(45,210,255),
    /// пустой трек — тёмный серо-синий. Вне полосы — однотонный фон.
    /// </summary>
    private static NativeFrame Frame(int width, int height, double share, byte track, string pattern = "")
    {
        int top = 700 * height / 720, bottom = (703 * height + 719) / 720;
        int filled = (int)Math.Round(share * width);
        byte[] raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0; y < height; y++)
        {
            int row = y * ((width * 3) + 1);
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = (60, 70, 90);
                if (y >= top && y < bottom)
                {
                    bool fill = pattern switch
                    {
                        "alternating" => x * 64 / width % 2 == 0,
                        "gap" => x < filled && (x * 64 / width) != 3,
                        _ => x < filled,
                    };
                    (r, g, b) = pattern == "bright-background" ? ((byte)150, (byte)150, (byte)150)
                        : fill ? ((byte)(94 - (49 * x / width)), (byte)(155 + (55 * x / width)), (byte)255)
                        : (track, track, (byte)(track + 10));
                }

                raw[row + 1 + (x * 3)] = r;
                raw[row + 2 + (x * 3)] = g;
                raw[row + 3 + (x * 3)] = b;
            }
        }

        return AzurPilotNativeBridge.DecodePng(Png(width, height, raw));
    }

    private static byte[] Png(int width, int height, byte[] raw)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        (string Type, byte[] Data)[] chunks = [("IHDR", header), ("IDAT", compressed.ToArray()), ("IEND", [])];
        foreach ((string type, byte[] data) in chunks)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            byte[] chunk = [.. Encoding.ASCII.GetBytes(type), .. data];
            output.Write(chunk);
            uint crc = uint.MaxValue;
            foreach (byte value in chunk)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
                }
            }

            byte[] checksum = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
            output.Write(checksum);
        }

        return output.ToArray();
    }
}
