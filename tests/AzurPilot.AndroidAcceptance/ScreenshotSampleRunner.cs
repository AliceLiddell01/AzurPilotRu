using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.AndroidAcceptance;

/// <summary>Обезличенная сводка read-only проверки локальных PNG sample.</summary>
/// <param name="EnumeratedFiles">Число файлов верхнего уровня, включённых в ограниченную проверку.</param>
/// <param name="PngFiles">Число файлов с расширением PNG без учёта регистра.</param>
/// <param name="OtherFiles">Число файлов с другими расширениями.</param>
/// <param name="EnumerationSucceeded">Удалось ли завершить ограниченное перечисление.</param>
/// <param name="EnumerationCapped">Найден ли дополнительный файл за лимитом перечисления.</param>
/// <param name="HeaderDimensions">Число корректных PNG IHDR по физическим размерам.</param>
/// <param name="SelectedDimensions">Число выбранных для production decode файлов по заявленным размерам.</param>
/// <param name="DecodedDimensions">Число успешно декодированных native frame по фактическим размерам.</param>
/// <param name="CandidateCount">Число файлов с потенциально поддерживаемым IHDR и допустимым размером.</param>
/// <param name="SelectedCount">Число выбранных файлов для production decode.</param>
/// <param name="DecodedCount">Число файлов, успешно декодированных production native decoder.</param>
/// <param name="FullFrameScansPassed">Число успешных production scans области FullFrame.</param>
/// <param name="FullFrameScansFailed">Число отказов production scans области FullFrame.</param>
/// <param name="CenterScansPassed">Число успешных production scans области Center.</param>
/// <param name="CenterScansFailed">Число отказов production scans области Center.</param>
/// <param name="IsProven">Успешна ли ограниченная проверка при отсутствии отказов.</param>
/// <param name="FailureCounts">Счётчики фиксированных причин отказа без имён, путей или текста исключений.</param>
public sealed record ScreenshotSampleRunReport(
    int EnumeratedFiles,
    int PngFiles,
    int OtherFiles,
    bool EnumerationSucceeded,
    bool EnumerationCapped,
    IReadOnlyDictionary<string, int> HeaderDimensions,
    IReadOnlyDictionary<string, int> SelectedDimensions,
    IReadOnlyDictionary<string, int> DecodedDimensions,
    int CandidateCount,
    int SelectedCount,
    int DecodedCount,
    int FullFrameScansPassed,
    int FullFrameScansFailed,
    int CenterScansPassed,
    int CenterScansFailed,
    bool IsProven,
    IReadOnlyDictionary<string, int> FailureCounts);

/// <summary>Декодирует и проверяет небольшую детерминированную выборку локальных PNG без записи файлов.</summary>
public static class ScreenshotSampleRunner
{
    private const int MaximumTopLevelFiles = 500;
    private const int PngHeaderLength = 29;
    private const int SamplesPerBucket = 3;
    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>
    /// Ограниченно перечисляет переданный каталог и выполняет native decode/scan выбранных PNG.
    /// Выход содержит только агрегаты; содержимое изображений, имена и пути не сохраняются и не выводятся.
    /// </summary>
    /// <param name="directoryPath">Каталог с локальными изображениями; обход выполняется только на верхнем уровне.</param>
    /// <returns>Обезличенная сводка результатов и фиксированных причин отказа.</returns>
    public static ScreenshotSampleRunReport Run(string directoryPath)
    {
        Dictionary<string, int> failures = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            AddFailure(failures, "input_directory_unavailable");
            return CreateReport(
                files: [],
                pngFiles: 0,
                otherFiles: 0,
                enumerationSucceeded: false,
                enumerationCapped: false,
                headerDimensions: EmptyCounts,
                selectedDimensions: EmptyCounts,
                decodedDimensions: EmptyCounts,
                candidateCount: 0,
                selectedCount: 0,
                decodedCount: 0,
                fullFrameScansPassed: 0,
                fullFrameScansFailed: 0,
                centerScansPassed: 0,
                centerScansFailed: 0,
                failures: failures);
        }

        List<FileInfo> files = [];
        bool enumerationSucceeded = false;
        bool enumerationCapped = false;
        try
        {
            using IEnumerator<string> enumerator = Directory
                .EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                .GetEnumerator();

            while (files.Count < MaximumTopLevelFiles && enumerator.MoveNext())
            {
                files.Add(new FileInfo(enumerator.Current));
            }

            enumerationCapped = files.Count == MaximumTopLevelFiles && enumerator.MoveNext();
            enumerationSucceeded = true;
        }
        catch (Exception exception) when (IsExpectedInputException(exception))
        {
            AddFailure(failures, "directory_enumeration_failed");
        }

        if (enumerationCapped)
        {
            // Неполный inventory не задаёт детерминированный набор для decode: порядок обхода
            // каталога не является контрактом. До полного inventory файлы не читаются.
            AddFailure(failures, "directory_enumeration_capped");
        }

        if (!enumerationSucceeded || enumerationCapped)
        {
            return CreateReport(
                files: files,
                pngFiles: 0,
                otherFiles: 0,
                enumerationSucceeded: enumerationSucceeded,
                enumerationCapped: enumerationCapped,
                headerDimensions: EmptyCounts,
                selectedDimensions: EmptyCounts,
                decodedDimensions: EmptyCounts,
                candidateCount: 0,
                selectedCount: 0,
                decodedCount: 0,
                fullFrameScansPassed: 0,
                fullFrameScansFailed: 0,
                centerScansPassed: 0,
                centerScansFailed: 0,
                failures: failures);
        }

        int pngFiles = 0;
        int otherFiles = 0;
        Dictionary<string, int> dimensions = new(StringComparer.Ordinal);
        List<SampleCandidate> headerCandidates = [];
        foreach (FileInfo file in files)
        {
            string extension;
            try
            {
                extension = file.Extension;
            }
            catch (Exception exception) when (IsExpectedInputException(exception))
            {
                otherFiles++;
                AddFailure(failures, "file_metadata_unavailable");
                continue;
            }

            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                otherFiles++;
                continue;
            }

            pngFiles++;
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    AddFailure(failures, "png_reparse_point_skipped");
                    continue;
                }

                long length = file.Length;
                PngHeaderReadStatus status = ReadPngHeader(file.FullName, length, out PngHeader header);
                if (status != PngHeaderReadStatus.Success)
                {
                    AddFailure(failures, HeaderFailureReason(status));
                    continue;
                }

                AddCount(dimensions, DimensionKey(header.Width, header.Height));
                if (!header.IsPotentiallySupported)
                {
                    AddFailure(failures, "png_format_unsupported");
                    continue;
                }

                headerCandidates.Add(new SampleCandidate(file, length, header));
            }
            catch (Exception exception) when (IsExpectedInputException(exception))
            {
                AddFailure(failures, "png_metadata_or_header_read_failed");
            }
        }

        uint maximumPngBytes;
        try
        {
            maximumPngBytes = AzurPilotNativeBridge.GetMaximumPngBytes();
        }
        catch (NativeBoundaryUnavailableException)
        {
            AddFailure(failures, "native_boundary_unavailable");
            return CreateReport(
                files,
                pngFiles,
                otherFiles,
                enumerationSucceeded,
                enumerationCapped,
                dimensions,
                EmptyCounts,
                EmptyCounts,
                candidateCount: 0,
                selectedCount: 0,
                decodedCount: 0,
                fullFrameScansPassed: 0,
                fullFrameScansFailed: 0,
                centerScansPassed: 0,
                centerScansFailed: 0,
                failures: failures);
        }
        catch (NativeAbiMismatchException)
        {
            AddFailure(failures, "native_abi_mismatch");
            return CreateReport(
                files,
                pngFiles,
                otherFiles,
                enumerationSucceeded,
                enumerationCapped,
                dimensions,
                EmptyCounts,
                EmptyCounts,
                candidateCount: 0,
                selectedCount: 0,
                decodedCount: 0,
                fullFrameScansPassed: 0,
                fullFrameScansFailed: 0,
                centerScansPassed: 0,
                centerScansFailed: 0,
                failures: failures);
        }
        catch (AzurPilotNativeBoundaryException)
        {
            AddFailure(failures, "native_limit_unavailable");
            return CreateReport(
                files,
                pngFiles,
                otherFiles,
                enumerationSucceeded,
                enumerationCapped,
                dimensions,
                EmptyCounts,
                EmptyCounts,
                candidateCount: 0,
                selectedCount: 0,
                decodedCount: 0,
                fullFrameScansPassed: 0,
                fullFrameScansFailed: 0,
                centerScansPassed: 0,
                centerScansFailed: 0,
                failures: failures);
        }

        List<SampleCandidate> candidates = [];
        foreach (SampleCandidate candidate in headerCandidates)
        {
            if (candidate.Length > maximumPngBytes)
            {
                AddFailure(failures, "png_exceeds_native_byte_limit");
                continue;
            }

            if (candidate.Length > int.MaxValue)
            {
                AddFailure(failures, "png_exceeds_managed_array_limit");
                continue;
            }

            candidates.Add(candidate);
        }

        List<SampleCandidate> selected = SelectSamples(candidates);
        Dictionary<string, int> selectedDimensions = new(StringComparer.Ordinal);
        Dictionary<string, int> decodedDimensions = new(StringComparer.Ordinal);
        foreach (SampleCandidate candidate in selected)
        {
            AddCount(selectedDimensions, DimensionKey(candidate.Header.Width, candidate.Header.Height));
        }

        int decodedCount = 0;
        int fullFrameScansPassed = 0;
        int fullFrameScansFailed = 0;
        int centerScansPassed = 0;
        int centerScansFailed = 0;
        foreach (SampleCandidate candidate in selected)
        {
            if (!TryReadEncodedPng(candidate, maximumPngBytes, out byte[] encodedPng, out string readFailure))
            {
                AddFailure(failures, readFailure);
                continue;
            }

            NativeFrame? decodedFrame = DecodeSample(encodedPng, failures);
            if (decodedFrame is null)
            {
                continue;
            }

            using NativeFrame frame = decodedFrame;

            NativeFrameInfo frameInfo;
            try
            {
                frameInfo = frame.GetInfo();
            }
            catch (NativeBoundaryUnavailableException)
            {
                AddFailure(failures, "native_boundary_unavailable");
                continue;
            }
            catch (NativeAbiMismatchException)
            {
                AddFailure(failures, "native_abi_mismatch");
                continue;
            }
            catch (AzurPilotNativeBoundaryException)
            {
                AddFailure(failures, "native_frame_metadata_failed");
                continue;
            }

            if (frameInfo.Width != candidate.Header.Width || frameInfo.Height != candidate.Header.Height)
            {
                AddFailure(failures, "decoded_dimensions_mismatch_header");
                continue;
            }

            decodedCount++;
            AddCount(decodedDimensions, DimensionKey(frameInfo.Width, frameInfo.Height));
            ScreenshotAnalysisSource source = ScreenshotAnalysisSource.ForLocalSample(
                checked((uint)encodedPng.Length));

            if (Scan(frame, source, ScreenshotDiagnosticRegion.FullFrame, failures))
            {
                fullFrameScansPassed++;
            }
            else
            {
                fullFrameScansFailed++;
            }

            if (Scan(frame, source, ScreenshotDiagnosticRegion.Center, failures))
            {
                centerScansPassed++;
            }
            else
            {
                centerScansFailed++;
            }
        }

        return CreateReport(
            files,
            pngFiles,
            otherFiles,
            enumerationSucceeded,
            enumerationCapped,
            dimensions,
            selectedDimensions,
            decodedDimensions,
            candidates.Count,
            selected.Count,
            decodedCount,
            fullFrameScansPassed,
            fullFrameScansFailed,
            centerScansPassed,
            centerScansFailed,
            failures,
            isProven: failures.Count == 0
                && selected.Count > 0
                && decodedCount == selected.Count
                && fullFrameScansPassed == selected.Count
                && centerScansPassed == selected.Count);
    }

    private static bool Scan(
        NativeFrame frame,
        ScreenshotAnalysisSource source,
        ScreenshotDiagnosticRegion region,
        Dictionary<string, int> failures)
    {
        try
        {
            ApplicationResult<ScreenshotRegionScanResult> result = ScreenshotRegionScanner.Scan(frame, source, region);
            if (result.IsSuccess && result.Value is not null)
            {
                return true;
            }

            string failureCode = result.FailureInfo?.Code ?? "unknown_application_failure";
            AddFailure(failures, $"scan_{RegionKey(region)}_{failureCode}");
            return false;
        }
        catch (AzurPilotNativeBoundaryException)
        {
            AddFailure(failures, $"scan_{RegionKey(region)}_native_boundary_failed");
            return false;
        }
        catch (ObjectDisposedException)
        {
            AddFailure(failures, $"scan_{RegionKey(region)}_frame_disposed");
            return false;
        }
    }

    private static NativeFrame? DecodeSample(byte[] encodedPng, Dictionary<string, int> failures)
    {
        try
        {
            return AzurPilotNativeBridge.DecodePng(encodedPng);
        }
        catch (NativeBoundaryUnavailableException)
        {
            AddFailure(failures, "native_boundary_unavailable");
        }
        catch (NativeAbiMismatchException)
        {
            AddFailure(failures, "native_abi_mismatch");
        }
        catch (AzurPilotNativeBoundaryException)
        {
            AddFailure(failures, "native_decode_rejected_or_failed");
        }

        return null;
    }

    private static List<SampleCandidate> SelectSamples(IReadOnlyCollection<SampleCandidate> candidates)
    {
        List<SampleCandidate> selected = [];
        foreach (IGrouping<PngBucket, SampleCandidate> bucket in candidates
            .GroupBy(candidate => PngBucket.From(candidate.Header))
            .OrderBy(group => group.Key.Width)
            .ThenBy(group => group.Key.Height)
            .ThenBy(group => group.Key.BitDepth)
            .ThenBy(group => group.Key.ColorType)
            .ThenBy(group => group.Key.InterlaceMethod))
        {
            List<SampleCandidate> ordered = [.. bucket
                .OrderBy(candidate => candidate.Length)
                .ThenBy(candidate => candidate.File.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.File.FullName, StringComparer.Ordinal)];

            if (ordered.Count <= SamplesPerBucket)
            {
                selected.AddRange(ordered);
                continue;
            }

            selected.Add(ordered[0]);
            selected.Add(ordered[ordered.Count / 2]);
            selected.Add(ordered[^1]);
        }

        return selected;
    }

    private static bool TryReadEncodedPng(
        SampleCandidate candidate,
        uint maximumPngBytes,
        out byte[] encodedPng,
        out string failureReason)
    {
        encodedPng = [];
        failureReason = "png_file_read_failed";
        if (candidate.Length <= 0 || candidate.Length > maximumPngBytes || candidate.Length > int.MaxValue)
        {
            failureReason = "png_size_changed_or_out_of_bounds";
            return false;
        }

        try
        {
            using FileStream stream = new(
                candidate.File.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length != candidate.Length)
            {
                failureReason = "png_file_changed_after_inventory";
                return false;
            }

            byte[] bytes = new byte[checked((int)candidate.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    failureReason = "png_file_truncated_during_read";
                    return false;
                }

                offset += read;
            }

            if (stream.ReadByte() != -1)
            {
                failureReason = "png_file_grew_during_read";
                return false;
            }

            if (!HasExpectedHeader(bytes, candidate.Header))
            {
                failureReason = "png_header_changed_after_inventory";
                return false;
            }

            encodedPng = bytes;
            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (IsExpectedInputException(exception))
        {
            return false;
        }
    }

    private static PngHeaderReadStatus ReadPngHeader(string path, long fileLength, out PngHeader header)
    {
        header = default;
        if (fileLength < PngHeaderLength)
        {
            return PngHeaderReadStatus.TooShort;
        }

        Span<byte> bytes = stackalloc byte[PngHeaderLength];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes[offset..]);
            if (read == 0)
            {
                return PngHeaderReadStatus.Truncated;
            }

            offset += read;
        }

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!bytes[..signature.Length].SequenceEqual(signature))
        {
            return PngHeaderReadStatus.InvalidSignature;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13
            || bytes[12] != 0x49
            || bytes[13] != 0x48
            || bytes[14] != 0x44
            || bytes[15] != 0x52)
        {
            return PngHeaderReadStatus.InvalidIhdr;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (width == 0 || height == 0)
        {
            return PngHeaderReadStatus.InvalidDimensions;
        }

        header = new PngHeader(
            width,
            height,
            bytes[24],
            bytes[25],
            bytes[26],
            bytes[27],
            bytes[28]);
        return PngHeaderReadStatus.Success;
    }

    private static bool HasExpectedHeader(ReadOnlySpan<byte> bytes, PngHeader expected)
    {
        if (bytes.Length < PngHeaderLength)
        {
            return false;
        }

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        return bytes[..signature.Length].SequenceEqual(signature)
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) == 13
            && bytes[12] == 0x49
            && bytes[13] == 0x48
            && bytes[14] == 0x44
            && bytes[15] == 0x52
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) == expected.Width
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) == expected.Height
            && bytes[24] == expected.BitDepth
            && bytes[25] == expected.ColorType
            && bytes[26] == expected.CompressionMethod
            && bytes[27] == expected.FilterMethod
            && bytes[28] == expected.InterlaceMethod;
    }

    private static ScreenshotSampleRunReport CreateReport(
        List<FileInfo> files,
        int pngFiles,
        int otherFiles,
        bool enumerationSucceeded,
        bool enumerationCapped,
        IReadOnlyDictionary<string, int> headerDimensions,
        IReadOnlyDictionary<string, int> selectedDimensions,
        IReadOnlyDictionary<string, int> decodedDimensions,
        int candidateCount,
        int selectedCount,
        int decodedCount,
        int fullFrameScansPassed,
        int fullFrameScansFailed,
        int centerScansPassed,
        int centerScansFailed,
        Dictionary<string, int> failures,
        bool isProven = false)
        => new(
            files.Count,
            pngFiles,
            otherFiles,
            enumerationSucceeded,
            enumerationCapped,
            ReadOnlySnapshot(headerDimensions),
            ReadOnlySnapshot(selectedDimensions),
            ReadOnlySnapshot(decodedDimensions),
            candidateCount,
            selectedCount,
            decodedCount,
            fullFrameScansPassed,
            fullFrameScansFailed,
            centerScansPassed,
            centerScansFailed,
            isProven,
            ReadOnlySnapshot(failures));

    private static ReadOnlyDictionary<string, int> ReadOnlySnapshot(IReadOnlyDictionary<string, int> source)
    {
        SortedDictionary<string, int> snapshot = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> entry in source)
        {
            snapshot[entry.Key] = entry.Value;
        }

        return new ReadOnlyDictionary<string, int>(snapshot);
    }

    private static string HeaderFailureReason(PngHeaderReadStatus status) => status switch
    {
        PngHeaderReadStatus.Success => "png_header_unavailable",
        PngHeaderReadStatus.TooShort or PngHeaderReadStatus.Truncated => "png_header_truncated",
        PngHeaderReadStatus.InvalidSignature => "png_signature_invalid",
        PngHeaderReadStatus.InvalidIhdr => "png_ihdr_invalid",
        PngHeaderReadStatus.InvalidDimensions => "png_dimensions_invalid",
        _ => "png_header_unavailable",
    };

    private static bool IsExpectedInputException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    private static string DimensionKey(uint width, uint height)
        => string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    private static string RegionKey(ScreenshotDiagnosticRegion region) => region switch
    {
        ScreenshotDiagnosticRegion.FullFrame => "full_frame",
        ScreenshotDiagnosticRegion.Center => "center",
        _ => "unknown_region",
    };

    private static void AddCount(Dictionary<string, int> counts, string key)
    {
        _ = counts.TryGetValue(key, out int current);
        counts[key] = current + 1;
    }

    private static void AddFailure(Dictionary<string, int> failures, string key) => AddCount(failures, key);

    private readonly record struct SampleCandidate(FileInfo File, long Length, PngHeader Header);

    private readonly record struct PngBucket(uint Width, uint Height, byte BitDepth, byte ColorType, byte InterlaceMethod)
    {
        public static PngBucket From(PngHeader header)
            => new(header.Width, header.Height, header.BitDepth, header.ColorType, header.InterlaceMethod);
    }

    private readonly record struct PngHeader(
        uint Width,
        uint Height,
        byte BitDepth,
        byte ColorType,
        byte CompressionMethod,
        byte FilterMethod,
        byte InterlaceMethod)
    {
        public bool IsPotentiallySupported
            => BitDepth == 8
                && ColorType is 2 or 6
                && CompressionMethod == 0
                && FilterMethod == 0
                && InterlaceMethod == 0;
    }

    private enum PngHeaderReadStatus
    {
        Success,
        TooShort,
        Truncated,
        InvalidSignature,
        InvalidIhdr,
        InvalidDimensions,
    }
}
