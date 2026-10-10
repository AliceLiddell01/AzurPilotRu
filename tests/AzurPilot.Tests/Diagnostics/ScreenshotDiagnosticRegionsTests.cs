using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.Diagnostics;

public sealed class ScreenshotDiagnosticRegionsTests
{
    [Fact(DisplayName = "Логическое пространство задаёт две фиксированные полуоткрытые области")]
    public void NamedRegionsHaveStableLogicalBounds()
    {
        Assert.Equal(1280, ScreenshotDiagnosticRegions.ReferenceWidth);
        Assert.Equal(720, ScreenshotDiagnosticRegions.ReferenceHeight);
        Assert.Equal(
            new ScreenshotLogicalBounds(
                0,
                0,
                ScreenshotDiagnosticRegions.ReferenceWidth,
                ScreenshotDiagnosticRegions.ReferenceHeight),
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.FullFrame));
        Assert.Equal(
            new ScreenshotLogicalBounds(427, 241, 853, 479),
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.Center));
    }

    [Fact(DisplayName = "Кадр 1280 на 720 даёт identity mapping для именованных областей")]
    public void ReferenceSizeMapsToIdentity()
    {
        ApplicationResult<PixelRegion> fullFrame = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.FullFrame),
            ScreenshotDiagnosticRegions.ReferenceWidth,
            ScreenshotDiagnosticRegions.ReferenceHeight);
        ApplicationResult<PixelRegion> center = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.Center),
            ScreenshotDiagnosticRegions.ReferenceWidth,
            ScreenshotDiagnosticRegions.ReferenceHeight);

        Assert.True(fullFrame.IsSuccess);
        Assert.Equal(
            new PixelRegion(
                0,
                0,
                ScreenshotDiagnosticRegions.ReferenceWidth,
                ScreenshotDiagnosticRegions.ReferenceHeight),
            fullFrame.Value);
        Assert.True(center.IsSuccess);
        Assert.Equal(new PixelRegion(427, 241, 853, 479), center.Value);
    }

    [Fact(DisplayName = "Кадр 1920 на 1080 получает пропорциональное отображение с outward rounding")]
    public void FullHdFrameMapsNamedRegionsProportionally()
    {
        ApplicationResult<PixelRegion> fullFrame = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.FullFrame),
            1920,
            1080);
        ApplicationResult<PixelRegion> center = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.Center),
            1920,
            1080);

        Assert.True(fullFrame.IsSuccess);
        Assert.Equal(new PixelRegion(0, 0, 1920, 1080), fullFrame.Value);
        Assert.True(center.IsSuccess);
        Assert.Equal(new PixelRegion(640, 361, 1280, 719), center.Value);
    }

    [Fact(DisplayName = "Дробные границы округляются наружу отдельно для каждой стороны")]
    public void FractionalEdgesUseFloorForLeadingAndCeilingForTrailingBounds()
    {
        ApplicationResult<PixelRegion> mapping = ScreenshotDiagnosticRegions.MapBounds(
            new ScreenshotLogicalBounds(1, 1, 3, 3),
            1920,
            1080);

        Assert.True(mapping.IsSuccess);
        Assert.Equal(new PixelRegion(1, 1, 5, 5), mapping.Value);
    }

    [Theory(DisplayName = "Невалидные логические границы отклоняются без clipping")]
    [InlineData(-1, 0, 10, 10, "logical_bounds_out_of_range")]
    [InlineData(0, 0, 1281, 720, "logical_bounds_out_of_range")]
    [InlineData(0, 0, 1280, 721, "logical_bounds_out_of_range")]
    [InlineData(7, 4, 7, 8, "logical_bounds_empty_or_reversed")]
    [InlineData(8, 9, 7, 10, "logical_bounds_empty_or_reversed")]
    public void InvalidLogicalBoundsAreRejected(
        int left,
        int top,
        int right,
        int bottom,
        string expectedReason)
    {
        ApplicationResult<PixelRegion> mapping = ScreenshotDiagnosticRegions.MapBounds(
            new ScreenshotLogicalBounds(left, top, right, bottom),
            1920,
            1080);

        Assert.True(mapping.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, mapping.FailureInfo!.Code);
        Assert.Equal(expectedReason, mapping.FailureInfo.Details!["reason"]);
    }

    [Theory(DisplayName = "Нулевой, portrait и неподтверждённый aspect ratio кадра отклоняются")]
    [InlineData(0u, 0u, "frame_dimensions_empty")]
    [InlineData(1080u, 1920u, "frame_aspect_ratio_unsupported")]
    [InlineData(1920u, 1200u, "frame_aspect_ratio_unsupported")]
    public void UnsupportedFrameGeometryFailsClosed(uint width, uint height, string expectedReason)
    {
        ApplicationResult<PixelRegion> mapping = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.FullFrame),
            width,
            height);

        Assert.True(mapping.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, mapping.FailureInfo!.Code);
        Assert.Equal(expectedReason, mapping.FailureInfo.Details!["reason"]);
    }

    [Fact(DisplayName = "Размер кадра, не представимый в physical bounds, отклоняется как overflow")]
    public void UnrepresentableFrameDimensionsAreRejectedBeforeMapping()
    {
        ApplicationResult<PixelRegion> mapping = ScreenshotDiagnosticRegions.MapBounds(
            ScreenshotDiagnosticRegions.GetLogicalBounds(ScreenshotDiagnosticRegion.FullFrame),
            3_221_225_472u,
            1_811_939_328u);

        Assert.True(mapping.IsFailure);
        Assert.Equal(ApplicationFailure.DiagnosticRegionUnsupported, mapping.FailureInfo!.Code);
        Assert.Equal("frame_dimensions_overflow", mapping.FailureInfo.Details!["reason"]);
    }
}
