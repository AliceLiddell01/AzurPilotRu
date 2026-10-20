using System.Buffers.Binary;
using AzurPilot.Core.Game;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>Interop-доказательство диагностического превью: отдельный native-кадр и JPEG без изменения источника.</summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeFramePreviewTests
{
    [Fact(DisplayName = "Раскладка аннотации совпадает с заголовком ABI")]
    public void AnnotationLayoutMatchesAbiHeader()
    {
        Assert.Equal(92, AzurPilotNativeBridge.VerifyAnnotationStructLayout());
    }

    [Fact(DisplayName = "Превью 4K-кадра — отдельный кадр логического размера, JPEG кодируется с аннотациями")]
    public void PreviewIsSeparateFrameAndEncodesAnnotatedJpeg()
    {
        using NativeFrame source = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(
            Path.Combine(PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", "main-2160.png")));
        NativeFrameInfo before = source.GetInfo();
        NativeFrameAnnotation[] annotations =
        [
            new(NativeAnnotationKind.Rectangle, new(10, 10, 210, 110), 0, 255, 0),
            new(NativeAnnotationKind.Rectangle, new(300, 300, 400, 400), 255, 128, 0, Dashed: true),
            new(NativeAnnotationKind.Cross, new(600, 300, 8, 0), 255, 0, 0),
            new(NativeAnnotationKind.Text, new(20, 650, 100, 0), 255, 255, 255, Text: "frame f0001 main/Unique"),
        ];

        byte[] jpeg;
        using (NativeFrame preview = source.CreatePreview(GameGeometry.LogicalWidth, GameGeometry.LogicalHeight))
        {
            NativeFrameInfo info = preview.GetInfo();
            Assert.Equal((uint)GameGeometry.LogicalWidth, info.Width);
            Assert.Equal((uint)GameGeometry.LogicalHeight, info.Height);
            jpeg = preview.EncodeJpeg(82, annotations);
            byte[] plain = preview.EncodeJpeg(82, []);
            Assert.NotEqual(plain, jpeg);
        }

        Assert.Equal(before, source.GetInfo());
        Assert.True(jpeg is [0xFF, 0xD8, ..] && jpeg[^2] == 0xFF && jpeg[^1] == 0xD9);
        Assert.Equal((GameGeometry.LogicalWidth, GameGeometry.LogicalHeight), SofSize(jpeg));
        Assert.True(jpeg.Length < AzurPilotNativeBridge.MaximumJpegBytes);
    }

    [Fact(DisplayName = "Кадр больше предела превью не кодируется в JPEG, неверные аргументы отклоняются")]
    public void PreviewContractRejectsInvalidUse()
    {
        using NativeFrame source = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(
            Path.Combine(PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", "main-2160.png")));
        _ = Assert.Throws<NativeFrameAnalysisException>(() => source.EncodeJpeg(82, []));
        _ = Assert.Throws<NativeFrameAnalysisException>(() => source.CreatePreview(GameGeometry.LogicalWidth, GameGeometry.LogicalHeight + 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CreatePreview(0, 0));

        using NativeFrame preview = source.CreatePreview(GameGeometry.LogicalWidth / 2, GameGeometry.LogicalHeight / 2);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => preview.EncodeJpeg(AzurPilotNativeBridge.MinimumJpegQuality - 1, []));
        _ = Assert.Throws<ArgumentException>(() => preview.EncodeJpeg(82,
            [new(NativeAnnotationKind.Rectangle, new(0, 0, 10, 10), 0, 0, 0, Text: "x")]));
        _ = Assert.Throws<NativeFrameAnalysisException>(() => preview.EncodeJpeg(82,
            [new(NativeAnnotationKind.Rectangle, new(0, 0, 10, 10), 0, 0, 0, Thickness: 0)]));

        NativeFrame disposed = source.CreatePreview(GameGeometry.LogicalWidth / 2, GameGeometry.LogicalHeight / 2);
        disposed.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => disposed.EncodeJpeg(82, []));
    }

    private static (int Width, int Height) SofSize(byte[] jpeg)
    {
        for (int offset = 2; offset + 9 < jpeg.Length;)
        {
            byte marker = jpeg[offset + 1];
            if (marker is 0xC0 or 0xC2)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 7)), BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 5)));
            }

            offset += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 2));
        }

        throw new InvalidDataException("JPEG без SOF.");
    }
}
