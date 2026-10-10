using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;

namespace AzurPilot.App.Diagnostics;

/// <summary>Происхождение кадра, переданное в явную операцию диагностического анализа.</summary>
public enum ScreenshotAnalysisSourceKind
{
    /// <summary>Кадр получен из successful capture точного Android target.</summary>
    AndroidCapture,

    /// <summary>Кадр декодирован из локального sample; его путь не сохраняется.</summary>
    LocalSample,
}

/// <summary>Ограниченные сведения об источнике анализируемого кадра.</summary>
/// <remarks>
/// Для Android capture сохраняются выбранная identity, точный endpoint, время capture, число PNG-байтов
/// и проверенные metadata frame. Для локального sample сохраняется только число encoded PNG-байтов;
/// имя и путь файла сюда не передаются.
/// </remarks>
public sealed record ScreenshotAnalysisSource
{
    private ScreenshotAnalysisSource(
        ScreenshotAnalysisSourceKind kind,
        MuMuInstanceId? instanceId,
        AndroidEndpoint? endpoint,
        DateTimeOffset? capturedAtUtc,
        uint encodedPngByteCount,
        NativeFrameInfo? capturedFrameInfo)
    {
        Kind = kind;
        InstanceId = instanceId;
        Endpoint = endpoint;
        CapturedAtUtc = capturedAtUtc;
        EncodedPngByteCount = encodedPngByteCount;
        CapturedFrameInfo = capturedFrameInfo;
    }

    /// <summary>Тип источника кадра.</summary>
    public ScreenshotAnalysisSourceKind Kind { get; }

    /// <summary>Identity выбранного MuMu instance для Android capture; <see langword="null"/> для sample.</summary>
    public MuMuInstanceId? InstanceId { get; }

    /// <summary>Точный Android endpoint capture; <see langword="null"/> для sample.</summary>
    public AndroidEndpoint? Endpoint { get; }

    /// <summary>UTC-момент capture; <see langword="null"/> для sample.</summary>
    public DateTimeOffset? CapturedAtUtc { get; }

    /// <summary>Число encoded PNG-байтов, полученных источником.</summary>
    public uint EncodedPngByteCount { get; }

    /// <summary>Frame metadata успешного Android capture; <see langword="null"/> для sample.</summary>
    public NativeFrameInfo? CapturedFrameInfo { get; }

    /// <summary>Создаёт provenance из успешного Android capture.</summary>
    /// <param name="captureInfo">Проверенные сведения exact-target capture.</param>
    /// <returns>Неизменяемый источник с identity, endpoint, временем и frame metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="captureInfo"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Capture не содержит положительного числа PNG-байтов.</exception>
    public static ScreenshotAnalysisSource ForCapture(AndroidScreenshotCaptureInfo captureInfo)
    {
        ArgumentNullException.ThrowIfNull(captureInfo);
        if (captureInfo.EncodedPngByteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureInfo),
                captureInfo.EncodedPngByteCount,
                "Успешный screenshot capture должен содержать PNG-байты.");
        }

        return new ScreenshotAnalysisSource(
            ScreenshotAnalysisSourceKind.AndroidCapture,
            captureInfo.InstanceId,
            captureInfo.Endpoint,
            captureInfo.CapturedAtUtc.ToUniversalTime(),
            checked((uint)captureInfo.EncodedPngByteCount),
            captureInfo.FrameInfo);
    }

    /// <summary>Создаёт provenance локального sample без сохранения имени или пути.</summary>
    /// <param name="encodedPngByteCount">Число encoded PNG-байтов перед декодированием.</param>
    /// <returns>Неизменяемый локальный источник без machine-specific file data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Размер равен нулю.</exception>
    public static ScreenshotAnalysisSource ForLocalSample(uint encodedPngByteCount)
    {
        if (encodedPngByteCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedPngByteCount),
                encodedPngByteCount,
                "Локальный PNG sample должен содержать хотя бы один байт.");
        }

        return new ScreenshotAnalysisSource(
            ScreenshotAnalysisSourceKind.LocalSample,
            instanceId: null,
            endpoint: null,
            capturedAtUtc: null,
            encodedPngByteCount: encodedPngByteCount,
            capturedFrameInfo: null);
    }
}

/// <summary>Структурированный результат статистики одной именованной области native RGB8-кадра.</summary>
/// <param name="Source">Bounded provenance источника.</param>
/// <param name="FrameWidth">Реальная ширина decoded frame в пикселях.</param>
/// <param name="FrameHeight">Реальная высота decoded frame в пикселях.</param>
/// <param name="PixelFormat">Имя пиксельного формата.</param>
/// <param name="Region">Идентификатор запрошенной именованной области.</param>
/// <param name="RegionName">Человекочитаемое имя области на русском языке.</param>
/// <param name="RequestedLogicalBounds">Запрошенные логические полуоткрытые границы.</param>
/// <param name="PhysicalPixelBounds">Фактические физические полуоткрытые границы ROI.</param>
/// <param name="PixelCount">Число пикселей, обработанных native ROI.</param>
/// <param name="MeanRed">Среднее значение красного канала.</param>
/// <param name="MeanGreen">Среднее значение зелёного канала.</param>
/// <param name="MeanBlue">Среднее значение синего канала.</param>
/// <param name="StandardDeviationRed">Population standard deviation красного канала.</param>
/// <param name="StandardDeviationGreen">Population standard deviation зелёного канала.</param>
/// <param name="StandardDeviationBlue">Population standard deviation синего канала.</param>
public sealed record ScreenshotRegionScanResult(
    ScreenshotAnalysisSource Source,
    uint FrameWidth,
    uint FrameHeight,
    string PixelFormat,
    ScreenshotDiagnosticRegion Region,
    string RegionName,
    ScreenshotLogicalBounds RequestedLogicalBounds,
    PixelRegion PhysicalPixelBounds,
    ulong PixelCount,
    double MeanRed,
    double MeanGreen,
    double MeanBlue,
    double StandardDeviationRed,
    double StandardDeviationGreen,
    double StandardDeviationBlue);

/// <summary>Явно сканирует одну diagnostic region конкретного native-owned кадра.</summary>
/// <remarks>
/// Сканер не получает screenshot сам, не выводит состояние игры и не обнаруживает viewport/letterbox.
/// Физические размеры берутся только из <see cref="NativeFrame.GetInfo"/>; region отображается на весь
/// decoded raster при точном совпадении поддерживаемого aspect ratio.
/// </remarks>
public static class ScreenshotRegionScanner
{
    private const string Rgb8Name = "RGB8";

    /// <summary>Сопоставляет logical region с native ROI и возвращает RGB population statistics.</summary>
    /// <param name="frame">Кадр, который остаётся владельцем native RGB8 пикселей.</param>
    /// <param name="source">Bounded provenance этого кадра.</param>
    /// <param name="region">Запрошенная именованная область.</param>
    /// <returns>Детерминированный structured scan result либо bounded ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException">Кадр или source равен <see langword="null"/>.</exception>
    public static ApplicationResult<ScreenshotRegionScanResult> Scan(
        NativeFrame frame,
        ScreenshotAnalysisSource source,
        ScreenshotDiagnosticRegion region)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(source);

        if (!Enum.IsDefined(region))
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(
                DiagnosticFailure("region_unknown", region, source, frameWidth: 0, frameHeight: 0));
        }

        ScreenshotLogicalBounds logicalBounds = ScreenshotDiagnosticRegions.GetLogicalBounds(region);
        NativeFrameInfo frameInfo;
        try
        {
            frameInfo = frame.GetInfo();
        }
        catch (ObjectDisposedException)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(DisposedFrameFailure(region, source));
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }

        if (frameInfo.PixelFormat != AzurPilotNativeBridge.PixelFormatRgb8)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(
                DiagnosticFailure("pixel_format_unsupported", region, source, frameInfo.Width, frameInfo.Height));
        }

        if (source.CapturedFrameInfo is NativeFrameInfo capturedInfo && capturedInfo != frameInfo)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(
                DiagnosticFailure("capture_frame_metadata_mismatch", region, source, frameInfo.Width, frameInfo.Height));
        }

        ApplicationResult<PixelRegion> mapped = ScreenshotDiagnosticRegions.MapBounds(
            logicalBounds,
            frameInfo.Width,
            frameInfo.Height);
        if (mapped.IsFailure)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(mapped.FailureInfo!);
        }

        PixelRegion physicalBounds = mapped.Value!;
        RegionRgbStatistics statistics;
        try
        {
            statistics = frame.ScanRegion(physicalBounds);
        }
        catch (ObjectDisposedException)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(DisposedFrameFailure(region, source));
        }
        catch (AzurPilotNativeBoundaryException exception)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(NativeBoundaryFailureMapper.Map(exception));
        }

        ulong expectedPixelCount;
        try
        {
            expectedPixelCount = checked(
                (ulong)(physicalBounds.Right - physicalBounds.Left)
                * (ulong)(physicalBounds.Bottom - physicalBounds.Top));
        }
        catch (OverflowException)
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(
                AnalysisFailure("pixel_count_overflow", region, source));
        }

        if (statistics.PixelCount != expectedPixelCount || !IsValid(statistics))
        {
            return ApplicationResult<ScreenshotRegionScanResult>.Failure(
                AnalysisFailure("native_statistics_invalid", region, source));
        }

        return ApplicationResult<ScreenshotRegionScanResult>.Success(new ScreenshotRegionScanResult(
            source,
            frameInfo.Width,
            frameInfo.Height,
            Rgb8Name,
            region,
            GetRegionName(region),
            logicalBounds,
            physicalBounds,
            statistics.PixelCount,
            statistics.MeanRed,
            statistics.MeanGreen,
            statistics.MeanBlue,
            statistics.StandardDeviationRed,
            statistics.StandardDeviationGreen,
            statistics.StandardDeviationBlue));
    }

    private static bool IsValid(RegionRgbStatistics statistics)
        => double.IsFinite(statistics.MeanRed)
            && statistics.MeanRed is >= 0 and <= byte.MaxValue
            && double.IsFinite(statistics.MeanGreen)
            && statistics.MeanGreen is >= 0 and <= byte.MaxValue
            && double.IsFinite(statistics.MeanBlue)
            && statistics.MeanBlue is >= 0 and <= byte.MaxValue
            && double.IsFinite(statistics.StandardDeviationRed)
            && statistics.StandardDeviationRed is >= 0 and <= byte.MaxValue
            && double.IsFinite(statistics.StandardDeviationGreen)
            && statistics.StandardDeviationGreen is >= 0 and <= byte.MaxValue
            && double.IsFinite(statistics.StandardDeviationBlue)
            && statistics.StandardDeviationBlue is >= 0 and <= byte.MaxValue;

    private static string GetRegionName(ScreenshotDiagnosticRegion region) => region switch
    {
        ScreenshotDiagnosticRegion.FullFrame => "Весь кадр",
        ScreenshotDiagnosticRegion.Center => "Центральная область",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Неизвестная screenshot diagnostic region."),
    };

    private static ApplicationFailure DisposedFrameFailure(
        ScreenshotDiagnosticRegion region,
        ScreenshotAnalysisSource source)
        => new()
        {
            Code = ApplicationFailure.NativeFrameAnalysisFailed,
            Message = "Native RGB8-кадр уже освобождён; анализ региона не выполнен.",
            IsRetryable = false,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "frame_disposed",
                ["exception_type"] = nameof(ObjectDisposedException),
                ["source_kind"] = source.Kind.ToString(),
                ["region"] = region.ToString(),
            },
        };

    private static ApplicationFailure DiagnosticFailure(
        string reason,
        ScreenshotDiagnosticRegion region,
        ScreenshotAnalysisSource source,
        uint frameWidth,
        uint frameHeight)
        => new()
        {
            Code = ApplicationFailure.DiagnosticRegionUnsupported,
            Message = "Screenshot diagnostic region не поддерживается для полученного кадра.",
            IsRetryable = false,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["source_kind"] = source.Kind.ToString(),
                ["region"] = region.ToString(),
                ["frame_width"] = frameWidth.ToString(CultureInfo.InvariantCulture),
                ["frame_height"] = frameHeight.ToString(CultureInfo.InvariantCulture),
            },
        };

    private static ApplicationFailure AnalysisFailure(
        string reason,
        ScreenshotDiagnosticRegion region,
        ScreenshotAnalysisSource source)
        => new()
        {
            Code = ApplicationFailure.NativeFrameAnalysisFailed,
            Message = "Native анализ региона вернул результат, нарушающий контракт RGB-статистики.",
            IsRetryable = false,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["source_kind"] = source.Kind.ToString(),
                ["region"] = region.ToString(),
            },
        };
}
