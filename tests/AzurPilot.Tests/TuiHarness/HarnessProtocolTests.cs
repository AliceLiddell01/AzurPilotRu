using System.Text.Json;
using AzurPilot.TuiHarness;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class HarnessProtocolTests
{
    [Theory]
    [InlineData("--columns", "19")]
    [InlineData("--columns", "241")]
    [InlineData("--rows", "9")]
    [InlineData("--rows", "101")]
    [InlineData("--session", "../outside")]
    [InlineData("--name", "../outside")]
    [InlineData("--timeout-ms", "0")]
    [InlineData("--timeout-ms", "30001")]
    [InlineData("--lifetime-seconds", "901")]
    [InlineData("--unknown", "value")]
    public void RejectsUnsafeProtocolBounds(string option, string value)
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["capture", option, value]));
    }

    [Fact]
    public void RejectsDuplicateAndIncompleteOptions()
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["resize", "--rows", "30", "--rows", "25"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["resize", "--rows"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["unknown"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["text", "--value", new string('x', 4097)]));
    }

    [Fact]
    public void LiveGrantRequiresExplicitExactTargetAndFixedScenario()
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["live-environment"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["live-environment", "--instance", "auto", "--authorize", "prepare-and-restore"]));
        _ = Assert.Throws<ArgumentException>(() => HarnessRequest.Parse(["start", "--instance", "mumu:7", "--authorize", "prepare-and-restore"]));
        HarnessRequest parsed = HarnessRequest.Parse(["live-environment", "--instance", "mumu:7", "--authorize", "prepare-and-restore"]);
        Assert.Equal("mumu:7", parsed.LiveInstance);
    }

    [Fact]
    public async Task IpcPreservesUnicodeAndRejectsOversizedFrames()
    {
        using MemoryStream stream = new();
        HarnessRequest request = new() { Command = "text", Session = new string('a', 32), Value = "Кириллица е\u0301 界" };
        await SessionOwner.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        HarnessRequest roundtrip = await SessionOwner.ReadAsync<HarnessRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(request, roundtrip);
        stream.SetLength(0);
        await stream.WriteAsync(BitConverter.GetBytes(65537), TestContext.Current.CancellationToken);
        stream.Position = 0;
        _ = await Assert.ThrowsAsync<ArgumentException>(() => SessionOwner.ReadAsync<HarnessRequest>(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosedSessionIsBlockedAndDoesNotBecomeSuccess()
    {
        HarnessResponse response;
        try
        {
            response = await AzurPilot.TuiHarness.Program.SendAsync(new HarnessRequest { Command = "status", Session = Guid.NewGuid().ToString("N"), TimeoutMs = 100 });
        }
        catch (HarnessBlockedException exception)
        {
            response = SessionOwner.Failure(exception, null);
        }

        Assert.Equal("BLOCKED", response.Status);
        Assert.Equal(2, response.ExitCode);
    }

    [Theory]
    [InlineData(16, 0x000000)]
    [InlineData(21, 0x0000ff)]
    [InlineData(196, 0xff0000)]
    [InlineData(231, 0xffffff)]
    [InlineData(232, 0x080808)]
    [InlineData(244, 0x808080)]
    [InlineData(255, 0xeeeeee)]
    public void ResolvesExtendedPaletteToExpectedRgb(int index, int rgb)
    {
        JsonElement color = JsonSerializer.SerializeToElement(new { mode = "palette", value = index });
        Assert.Equal(rgb, ColorSamples.Resolve(color, default, "background"));
    }
}
