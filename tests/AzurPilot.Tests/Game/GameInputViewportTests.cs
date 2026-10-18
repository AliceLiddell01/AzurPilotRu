using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;
using Xunit;

namespace AzurPilot.Tests.Game;

public sealed class GameInputViewportTests
{
    private static readonly string[] TapArguments = ["-s", "explicit-host:16419", "shell", "input", "-d", "0", "tap", "42", "91"];
    internal const string Input = "Viewport INTERNAL: displayId=0, uniqueId=local:test, port=0, orientation=1, logicalFrame=[0, 0, 3840, 2160], physicalFrame=[0, 0, 3840, 2160], deviceSize=[3840, 2160], isActive=[1]";
    internal const string Window = """
          Window #0 Window{abc u0 com.YoStarEN.AzurLane/com.manjuu.azurlane.MainActivity}:
            mViewVisibility=0x0 mHaveFrame=true mObscured=false
            mFullConfiguration={ mAppBounds=Rect(0, 0 - 3840, 2160) mDisplayRotation=ROTATION_90 mWindowingMode=fullscreen }
            Frames: parent=[0,0][3840,2160] display=[0,0][3840,2160] frame=[0,0][3840,2160]
            isOnScreen=true
            isVisible=true
        """;
    internal const string Focus = "mCurrentFocus=Window{abc u0 com.YoStarEN.AzurLane/com.manjuu.azurlane.MainActivity}";

    [Fact]
    public void MatchingRotatedLogicalViewportAndFullscreenWindowProveIdentityMapping()
    {
        Assert.True(GameInputViewportParser.Validate(GameUiRecognizerTests.ProvenanceForSize(3840, 2160),
            Outcome(Input + "\n" + Input), Outcome(Window), Outcome(Focus)).IsSuccess);
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("physical")]
    [InlineData("display")]
    [InlineData("inactive")]
    [InlineData("letterbox")]
    [InlineData("app-bounds")]
    [InlineData("obscured")]
    [InlineData("multiviewport")]
    [InlineData("other-foreground")]
    [InlineData("unknown-format")]
    [InlineData("truncated")]
    public void UnknownOrInconsistentViewportFailsClosed(string scenario)
    {
        string input = scenario switch
        {
            "rotation" => Input.Replace("orientation=1", "orientation=0", StringComparison.Ordinal),
            "physical" => Input.Replace("physicalFrame=[0, 0, 3840, 2160]", "physicalFrame=[0, 0, 2160, 3840]", StringComparison.Ordinal),
            "display" => Input.Replace("displayId=0", "displayId=1", StringComparison.Ordinal),
            "inactive" => Input.Replace("isActive=[1]", "isActive=[0]", StringComparison.Ordinal),
            "multiviewport" => Input + "\n" + Input.Replace("orientation=1", "orientation=0", StringComparison.Ordinal),
            "unknown-format" => "unknown",
            _ => Input,
        };
        string window = scenario switch
        {
            "letterbox" => Window.Replace(" frame=[0,0][3840,2160]", " frame=[40,0][3800,2160]", StringComparison.Ordinal),
            "app-bounds" => Window.Replace("mAppBounds=Rect(0, 0 - 3840, 2160)", "mAppBounds=Rect(40, 0 - 3800, 2160)", StringComparison.Ordinal),
            "obscured" => Window.Replace("mObscured=false", "mObscured=true", StringComparison.Ordinal),
            _ => Window,
        };
        Assert.True(GameInputViewportParser.Validate(GameUiRecognizerTests.ProvenanceForSize(3840, 2160),
            Outcome(input) with { StandardOutputTruncated = scenario == "truncated" }, Outcome(window),
            Outcome(scenario == "other-foreground" ? Focus.Replace("com.YoStarEN.AzurLane", "other.app", StringComparison.Ordinal) : Focus)).IsFailure);
    }

    [Fact]
    public void InputArgumentsAlwaysAddressExactEndpointAndDisplay()
    {
        Core.Android.AndroidEndpoint endpoint = new("explicit-host", 16419);
        Assert.Equal(TapArguments,
            AdbCommandBuilder.BuildTapArguments(endpoint, new(42, 91)));
    }

    [Theory]
    [InlineData("1.05")]
    [InlineData("0.75")]
    [InlineData("NaN")]
    [InlineData("1.0junk")]
    public void CompatScaleMustBeExactlyKnownUnitScale(string scale)
    {
        Assert.True(GameInputViewportParser.Validate(GameUiRecognizerTests.ProvenanceForSize(3840, 2160),
            Outcome(Input), Outcome(Window + "\n mGlobalScale=" + scale + "\n"), Outcome(Focus)).IsFailure);
        Assert.True(GameInputViewportParser.Validate(GameUiRecognizerTests.ProvenanceForSize(3840, 2160),
            Outcome(Input), Outcome(Window + "\n mGlobalScale=1.0\n"), Outcome(Focus)).IsSuccess);
    }

    private static WindowsProcessOutcome Outcome(string text)
    {
        return new()
        {
            ExitCode = 0,
            StandardOutput = text,
            StandardError = "",
            Duration = TimeSpan.Zero,
            StandardOutputTruncated = false,
            StandardErrorTruncated = false,
        };
    }
}
