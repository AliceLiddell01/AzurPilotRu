using System.Globalization;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.App.Diagnostics;

/// <summary>Небольшой набор геометрических областей для явной диагностики кадра.</summary>
public enum ScreenshotDiagnosticRegion
{
    /// <summary>Вся область декодированного растра.</summary>
    FullFrame,

    /// <summary>Центральная часть декодированного растра.</summary>
    Center,
}

/// <summary>Полуоткрытые логические границы прямоугольника: левая и верхняя включены.</summary>
/// <param name="Left">Включённая левая граница.</param>
/// <param name="Top">Включённая верхняя граница.</param>
/// <param name="Right">Невключённая правая граница.</param>
/// <param name="Bottom">Невключённая нижняя граница.</param>
public readonly record struct ScreenshotLogicalBounds(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Геометрия областей конкретного диагностического потребителя и их отображение на физический кадр.
/// </summary>
/// <remarks>
/// <see cref="ReferenceWidth"/> и <see cref="ReferenceHeight"/> задают только логические координаты
/// этих диагностических областей. Реальные размеры всегда поступают из метаданных декодированного кадра.
/// Отображение разрешено только для landscape-кадра с тем же точным aspect ratio; автоматическое
/// обнаружение или обрезка letterbox не выполняются.
/// </remarks>
public static class ScreenshotDiagnosticRegions
{
    /// <summary>Ширина логического пространства областей этого потребителя.</summary>
    public const int ReferenceWidth = 1280;

    /// <summary>Высота логического пространства областей этого потребителя.</summary>
    public const int ReferenceHeight = 720;

    private static readonly ScreenshotLogicalBounds FullFrameBounds =
        new(0, 0, ReferenceWidth, ReferenceHeight);

    // Центральная область занимает примерно треть логической ширины и высоты и не описывает игровой UI.
    // Нечётные края также закрепляют наблюдаемое округление при масштабе 1.5.
    private static readonly ScreenshotLogicalBounds CenterBounds = new(427, 241, 853, 479);

    /// <summary>Возвращает логические полуоткрытые границы выбранной именованной области.</summary>
    /// <param name="region">Область диагностического потребителя.</param>
    /// <returns>Неизменяемые границы внутри заданного пространства логических координат.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Неизвестная область.</exception>
    public static ScreenshotLogicalBounds GetLogicalBounds(ScreenshotDiagnosticRegion region) => region switch
    {
        ScreenshotDiagnosticRegion.FullFrame => FullFrameBounds,
        ScreenshotDiagnosticRegion.Center => CenterBounds,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Неизвестная диагностическая область кадра."),
    };

    /// <summary>Преобразует логические границы в физические пиксели конкретного кадра.</summary>
    /// <remarks>
    /// Левые и верхние границы округляются вниз, правые и нижние — вверх, поэтому целевой полуоткрытый
    /// интервал покрывает все физические пиксели, пересекающие логический регион. Области вне пространства
    /// логических координат и неподдерживаемые размеры кадра отклоняются; clipping не выполняется.
    /// </remarks>
    /// <param name="requested">Запрошенные логические полуоткрытые границы.</param>
    /// <param name="frameWidth">Ширина кадра из native метаданных.</param>
    /// <param name="frameHeight">Высота кадра из native метаданных.</param>
    /// <returns>Физические полуоткрытые границы пикселей либо fail-closed отказ.</returns>
    public static ApplicationResult<PixelRegion> MapBounds(
        ScreenshotLogicalBounds requested,
        uint frameWidth,
        uint frameHeight)
    {
        if (requested.Left < 0
            || requested.Top < 0
            || requested.Right > ReferenceWidth
            || requested.Bottom > ReferenceHeight)
        {
            return Unsupported("logical_bounds_out_of_range", requested, frameWidth, frameHeight);
        }

        if (requested.Right <= requested.Left || requested.Bottom <= requested.Top)
        {
            return Unsupported("logical_bounds_empty_or_reversed", requested, frameWidth, frameHeight);
        }

        if (frameWidth == 0 || frameHeight == 0)
        {
            return Unsupported("frame_dimensions_empty", requested, frameWidth, frameHeight);
        }

        if (frameWidth > int.MaxValue || frameHeight > int.MaxValue)
        {
            return Unsupported("frame_dimensions_overflow", requested, frameWidth, frameHeight);
        }

        if (frameWidth <= frameHeight
            || checked((ulong)frameWidth * ReferenceHeight)
                != checked((ulong)frameHeight * ReferenceWidth))
        {
            return Unsupported("frame_aspect_ratio_unsupported", requested, frameWidth, frameHeight);
        }

        try
        {
            PixelRegion mapped = new(
                MapFloor(requested.Left, frameWidth, ReferenceWidth),
                MapFloor(requested.Top, frameHeight, ReferenceHeight),
                MapCeiling(requested.Right, frameWidth, ReferenceWidth),
                MapCeiling(requested.Bottom, frameHeight, ReferenceHeight));

            if (mapped.Right <= mapped.Left
                || mapped.Bottom <= mapped.Top
                || mapped.Left < 0
                || mapped.Top < 0
                || (uint)mapped.Right > frameWidth
                || (uint)mapped.Bottom > frameHeight)
            {
                return Unsupported("mapped_bounds_empty_or_out_of_range", requested, frameWidth, frameHeight);
            }

            return ApplicationResult<PixelRegion>.Success(mapped);
        }
        catch (OverflowException)
        {
            return Unsupported("coordinate_overflow", requested, frameWidth, frameHeight);
        }
    }

    private static int MapFloor(int coordinate, uint frameDimension, int referenceDimension)
    {
        long numerator = checked(coordinate * frameDimension);
        return checked((int)(numerator / referenceDimension));
    }

    private static int MapCeiling(int coordinate, uint frameDimension, int referenceDimension)
    {
        long numerator = checked(coordinate * frameDimension);
        long quotient = numerator / referenceDimension;
        if (numerator % referenceDimension != 0)
        {
            quotient = checked(quotient + 1);
        }

        return checked((int)quotient);
    }

    private static ApplicationResult<PixelRegion> Unsupported(
        string reason,
        ScreenshotLogicalBounds requested,
        uint frameWidth,
        uint frameHeight)
    {
        return ApplicationResult<PixelRegion>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.DiagnosticRegionUnsupported,
            Message = "Логическая область не отображается на этот физический кадр.",
            IsRetryable = false,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["frame_width"] = frameWidth.ToString(CultureInfo.InvariantCulture),
                ["frame_height"] = frameHeight.ToString(CultureInfo.InvariantCulture),
                ["logical_bounds"] = string.Join(
                    ",",
                    requested.Left.ToString(CultureInfo.InvariantCulture),
                    requested.Top.ToString(CultureInfo.InvariantCulture),
                    requested.Right.ToString(CultureInfo.InvariantCulture),
                    requested.Bottom.ToString(CultureInfo.InvariantCulture)),
            },
        });
    }
}
