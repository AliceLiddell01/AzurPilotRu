using System.Globalization;
using System.Text.RegularExpressions;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

public sealed partial class AndroidWindowsHost
{
    /// <inheritdoc />
    public async Task<ApplicationResult<bool>> ValidateGameInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AdbClient client = GetAdbClient();
        ApplicationResult<WindowsProcessOutcome> input = await client.RunAsync(
            AdbCommandBuilder.BuildInputViewportArguments(frame.Endpoint), cancellationToken).ConfigureAwait(false);
        if (input.IsFailure)
        {
            return ApplicationResult<bool>.Failure(input.FailureInfo!);
        }

        ApplicationResult<WindowsProcessOutcome> windows = await client.RunAsync(
            AdbCommandBuilder.BuildGameWindowArguments(frame.Endpoint), cancellationToken).ConfigureAwait(false);
        if (windows.IsFailure)
        {
            return ApplicationResult<bool>.Failure(windows.FailureInfo!);
        }

        ApplicationResult<WindowsProcessOutcome> focus = await client.RunAsync(
            AdbCommandBuilder.BuildWindowDumpArguments(frame.Endpoint), cancellationToken).ConfigureAwait(false);
        return focus.IsFailure
            ? ApplicationResult<bool>.Failure(focus.FailureInfo!)
            : GameInputViewportParser.Validate(frame, input.Value!, windows.Value!, focus.Value!);
    }

    /// <inheritdoc />
    public async Task<ApplicationResult<AndroidCommandOutcome>> TapGameAsync(GameTap tap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tap);
        AndroidEndpoint endpoint = tap.Frame.Endpoint;
        GameInputPoint point = tap.Point;
        TimeSpan age = _timeProvider.GetElapsedTime(tap.Frame.CaptureStartedTimestamp);
        if (!tap.TryConsume() || string.IsNullOrWhiteSpace(endpoint.Host) || !tap.SafeBounds.Contains(point)
            || age < TimeSpan.Zero || age > tap.MaximumObservationAge
            || point.X < 0 || point.X >= tap.Frame.Width || point.Y < 0 || point.Y >= tap.Frame.Height)
        {
            return ApplicationResult<AndroidCommandOutcome>.Failure(GameUiActionRunner.Failure("input_arguments_invalid"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ApplicationResult<WindowsProcessOutcome> result = await GetAdbClient().RunAsync(
            AdbCommandBuilder.BuildTapArguments(endpoint, point), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApplicationResult<AndroidCommandOutcome>.Failure(result.FailureInfo!);
        }

        WindowsProcessOutcome outcome = result.Value!;
        return outcome.StandardOutputTruncated || outcome.StandardErrorTruncated
            || !string.IsNullOrWhiteSpace(outcome.StandardError) || !string.IsNullOrWhiteSpace(outcome.StandardOutput)
            ? ApplicationResult<AndroidCommandOutcome>.Failure(GameUiActionRunner.Failure("adb_input_response_invalid", true))
            : ApplicationResult<AndroidCommandOutcome>.Success(AdbResponseParser.ToCommandOutcome(outcome));
    }
}

/// <summary>Строгое доказательство viewport; неизвестный формат, несогласованная ориентация и letterbox отклоняются.</summary>
internal static partial class GameInputViewportParser
{
    internal static ApplicationResult<bool> Validate(GameFrameProvenance frame, WindowsProcessOutcome input,
        WindowsProcessOutcome windows, WindowsProcessOutcome focus)
    {
        if (new[] { input, windows, focus }.Any(o => o.ExitCode != 0 || o.StandardOutputTruncated || o.StandardErrorTruncated))
        {
            return Reject("viewport_query_failed");
        }

        if (AdbResponseParser.ParseForeground(focus, AzurLaneProduct.Package).Status != AndroidForegroundStatus.Foreground)
        {
            return Reject("game_not_foreground");
        }

        string[] viewports = [.. Viewports().Matches(input.StandardOutput).Select(m => m.Value).Distinct(StringComparer.Ordinal)];
        if (viewports.Length != 1)
        {
            return Reject("input_viewport_ambiguous");
        }

        string viewport = viewports[0];
        string width = frame.Width.ToString(CultureInfo.InvariantCulture);
        string height = frame.Height.ToString(CultureInfo.InvariantCulture);
        Match orientation = Orientation().Match(viewport);
        if (!orientation.Success || !viewport.Contains("isActive=[1]", StringComparison.Ordinal)
            || !viewport.Contains("displayId=0,", StringComparison.Ordinal)
            || !viewport.Contains($"logicalFrame=[0, 0, {width}, {height}]", StringComparison.Ordinal)
            || !viewport.Contains($"physicalFrame=[0, 0, {width}, {height}]", StringComparison.Ordinal)
            || !viewport.Contains($"deviceSize=[{width}, {height}]", StringComparison.Ordinal))
        {
            return Reject("input_viewport_mismatch");
        }

        string[] gameWindows = [.. GameWindows().Matches(windows.StandardOutput).Select(m => m.Value)
            .Where(w => w[..w.IndexOf('\n')].Contains(" " + AzurLaneProduct.Package + "/", StringComparison.Ordinal)
                && w.Contains("isOnScreen=true", StringComparison.Ordinal) && w.Contains("isVisible=true", StringComparison.Ordinal))];
        if (gameWindows.Length != 1)
        {
            return Reject("game_window_ambiguous");
        }

        string window = gameWindows[0];
        MatchCollection scales = GlobalScale().Matches(window);
        if (window.Contains("mGlobalScale=", StringComparison.Ordinal)
            && (scales.Count != 1 || !double.TryParse(scales[0].Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double scale) || scale != 1.0))
        {
            return Reject("game_compat_scale_unproven");
        }

        string rotation = orientation.Groups[1].Value switch { "0" => "ROTATION_0", "1" => "ROTATION_90", "2" => "ROTATION_180", "3" => "ROTATION_270", _ => "unproven" };
        return !window.Contains($" frame=[0,0][{width},{height}]", StringComparison.Ordinal)
            || !window.Contains($"mAppBounds=Rect(0, 0 - {width}, {height})", StringComparison.Ordinal)
            || !window.Contains("mWindowingMode=fullscreen", StringComparison.Ordinal)
            || !window.Contains($"mDisplayRotation={rotation} ", StringComparison.Ordinal)
            || !window.Contains("mObscured=false", StringComparison.Ordinal)
            ? Reject("game_viewport_unproven")
            : ApplicationResult<bool>.Success(true);
    }

    private static ApplicationResult<bool> Reject(string reason)
    {
        return ApplicationResult<bool>.Failure(GameUiActionRunner.Failure(reason));
    }

    [GeneratedRegex(@"Viewport INTERNAL: [^\r\n]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Viewports();
    [GeneratedRegex(@"\borientation=([0-3]),", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Orientation();
    [GeneratedRegex(@"\bmGlobalScale=([0-9]+(?:\.[0-9]+)?)(?=\s|$)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GlobalScale();
    [GeneratedRegex(@"(?ms)^[\t ]*Window #\d+ Window\{[^\r\n}]+\}:[^\r\n]*\r?\n(?:(?!^[\t ]*Window #\d+).)*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GameWindows();
}
