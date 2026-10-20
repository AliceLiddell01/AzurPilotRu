namespace AzurPilot.Windows.Capture;

/// <summary>Итог сравнения кадра транспорта с эталонным кадром ADB того же экрана.</summary>
/// <param name="SameGeometry">Совпали ширина и высота.</param>
/// <param name="Cells">Число ячеек сетки.</param>
/// <param name="MatchedCells">Ячейки, средние RGB которых совпали в пределах допуска.</param>
/// <param name="MeanAbsoluteError">Средняя абсолютная ошибка каналов по ячейкам.</param>
/// <param name="SwappedMeanAbsoluteError">Та же ошибка при переставленных R и B кандидата.</param>
/// <param name="ChromaticCells">Ячейки, где R и B эталона различаются настолько, что перестановка различима.</param>
public sealed record ScreenshotFrameComparison(bool SameGeometry, int Cells, int MatchedCells, double MeanAbsoluteError,
    double SwappedMeanAbsoluteError, int ChromaticCells)
{
    /// <summary>Размер сетки по горизонтали.</summary>
    public const int Columns = 16;

    /// <summary>Размер сетки по вертикали.</summary>
    public const int Rows = 9;

    /// <summary>Допуск среднего канала ячейки: покрывает JPEG, RGB565 и YUV-квантование.</summary>
    public const double CellTolerance = 16;

    /// <summary>Доля совпавших ячеек; живой фон главного экрана допускает расхождение части ячеек.</summary>
    public const double RequiredMatchedRatio = 0.75;

    /// <summary>Порядок каналов доказан: перестановка R/B даёт заметно большую ошибку.</summary>
    public bool ChannelOrderProven => ChromaticCells >= 4 && SwappedMeanAbsoluteError > (MeanAbsoluteError * 1.5) + 2;

    /// <summary>Кадр совпал с эталоном по геометрии, содержимому и порядку каналов.</summary>
    public bool Matches => SameGeometry && Cells > 0 && (double)MatchedCells / Cells >= RequiredMatchedRatio
        && ChannelOrderProven;

    /// <summary>Машинная причина несовпадения либо null.</summary>
    public string? FailureReason => !SameGeometry ? "geometry_mismatch"
        : !ChannelOrderProven ? (ChromaticCells < 4 ? "channel_order_unprovable" : "channel_order_mismatch")
        : Matches ? null : "content_mismatch";

    /// <summary>Сравнивает два кадра по средним RGB ячеек сетки внутри DLL без копирования пикселей.</summary>
    public static ScreenshotFrameComparison Compare(NativeFrame reference, NativeFrame candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        NativeFrameInfo left = reference.GetInfo();
        NativeFrameInfo right = candidate.GetInfo();
        if (left.Width != right.Width || left.Height != right.Height)
        {
            return new(false, 0, 0, double.NaN, double.NaN, 0);
        }

        int width = (int)left.Width;
        int height = (int)left.Height;
        int matched = 0, chromatic = 0, cells = 0;
        double error = 0, swapped = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                PixelRegion cell = new(column * width / Columns, row * height / Rows, (column + 1) * width / Columns,
                    (row + 1) * height / Rows);
                if (cell.Right <= cell.Left || cell.Bottom <= cell.Top)
                {
                    continue;
                }

                RegionRgbStatistics a = reference.ScanRegion(cell);
                RegionRgbStatistics b = candidate.ScanRegion(cell);
                double red = Math.Abs(a.MeanRed - b.MeanRed);
                double green = Math.Abs(a.MeanGreen - b.MeanGreen);
                double blue = Math.Abs(a.MeanBlue - b.MeanBlue);
                cells++;
                error += (red + green + blue) / 3;
                swapped += (Math.Abs(a.MeanRed - b.MeanBlue) + green + Math.Abs(a.MeanBlue - b.MeanRed)) / 3;
                if (Math.Max(red, Math.Max(green, blue)) <= CellTolerance)
                {
                    matched++;
                }

                if (Math.Abs(a.MeanRed - a.MeanBlue) >= 24)
                {
                    chromatic++;
                }
            }
        }

        return new(true, cells, matched, cells == 0 ? 0 : error / cells, cells == 0 ? 0 : swapped / cells, chromatic);
    }
}
