using System.Runtime.InteropServices;

namespace AzurPilot.Windows;

/// <summary>Проверенные native способы сопоставления конкретного варианта ассета.</summary>
public enum NativeTemplateMode
{
    /// <summary>CCOEFF_NORMED по трём RGB каналам.</summary>
    Rgb = 0,
    /// <summary>CCOEFF_NORMED по яркости с преобразованием RGB2GRAY.</summary>
    Luma = 1,
}

/// <summary>Native оценки; пороги и разрешение действия принадлежат данным ассета.</summary>
public sealed record NativeTemplateMatch(PixelRegion Bounds, double Score, double RunnerUpScore,
    double MeanAbsoluteError, double TemplateStandardDeviation);

public static partial class AzurPilotNativeBridge
{
    internal static unsafe NativeTemplateMatch MatchTemplate(NativeFrameSafeHandle frame, PixelRegion search,
        ReadOnlySpan<byte> png, int width, int height, NativeTemplateMode mode)
    {
        EnsureFrameAbiVersion();
        NativeRegion region = new() { Left = search.Left, Top = search.Top, Right = search.Right, Bottom = search.Bottom };
        int status;
        NativeMatchResult result;
        fixed (byte* pointer = png)
        {
            status = MatchNative(frame, in region, pointer, checked((uint)png.Length), width, height, (uint)mode, out result);
        }

        return status != StatusOk
            ? throw new NativeFrameAnalysisException(status)
            : !double.IsFinite(result.Score) || result.Score is < -1 or > 1
            || !double.IsFinite(result.SecondScore) || result.SecondScore is < -1 or > 1
            || !double.IsFinite(result.Error) || result.Error is < 0 or > 255
            || !double.IsFinite(result.StandardDeviation) || result.StandardDeviation <= 0
            || result.Region.Left < search.Left || result.Region.Top < search.Top
            || result.Region.Right > search.Right || result.Region.Bottom > search.Bottom
            || (long)result.Region.Right - result.Region.Left != width || (long)result.Region.Bottom - result.Region.Top != height
            ? throw new NativeFrameAnalysisException(StatusInternal)
            : new(new(result.Region.Left, result.Region.Top, result.Region.Right, result.Region.Bottom),
            result.Score, result.SecondScore, result.Error, result.StandardDeviation);
    }

    private static unsafe void EnsureFrameMatchApi()
    {
        NativeMatchResult layout = default;
        if (sizeof(NativeMatchResult) != 48 || (byte*)&layout.Score - (byte*)&layout != 16
            || (byte*)&layout.Error - (byte*)&layout != 32)
        {
            throw new NativeAbiMismatchException("Раскладка match ABI не совпадает с нормативным заголовком.");
        }

        try
        {
            int status = ProbeMatchNative(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, out _);
            if (status != StatusInvalidArgument)
            {
                throw new NativeAbiMismatchException("Нарушен NULL-контракт match ABI.");
            }
        }
        catch (EntryPointNotFoundException) { throw MissingFrameApi("azurpilot_native_frame_match"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMatchResult
    {
        internal NativeRegion Region;
        internal double Score;
        internal double SecondScore;
        internal double Error;
        internal double StandardDeviation;
    }

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_match")]
    private static unsafe partial int MatchNative(NativeFrameSafeHandle frame, in NativeRegion region,
        byte* png, uint length, int width, int height, uint mode, out NativeMatchResult result);

    [LibraryImport(LibraryName, EntryPoint = "azurpilot_native_frame_match")]
    private static partial int ProbeMatchNative(IntPtr frame, IntPtr region, IntPtr png, uint length,
        int width, int height, uint mode, out NativeMatchResult result);
}
