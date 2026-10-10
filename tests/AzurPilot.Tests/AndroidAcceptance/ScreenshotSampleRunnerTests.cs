using System.Globalization;
using AzurPilot.AndroidAcceptance;
using AzurPilot.Tests.Diagnostics;
using Xunit;

namespace AzurPilot.Tests.AndroidAcceptance;

/// <summary>Проверки доказательности bounded sample acceptance.</summary>
[Trait("Category", "Integration")]
public sealed class ScreenshotSampleRunnerTests
{
    [Fact(DisplayName = "Невалидный PNG вне выбранной выборки оставляет прогон недоказанным")]
    public void InvalidPngOutsideSelectedSamplePreventsProof()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AzurPilot.Tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "valid.png"),
                ScreenshotPngFixture.CreateRgb(16, 9, static (_, _) => (byte.MaxValue, 0, 0)));
            File.WriteAllBytes(Path.Combine(directory, "invalid.png"), new byte[29]);

            ScreenshotSampleRunReport report = ScreenshotSampleRunner.Run(directory);

            Assert.Equal(1, report.SelectedCount);
            Assert.Equal(1, report.DecodedCount);
            Assert.Equal(1, report.FullFrameScansPassed);
            Assert.Equal(1, report.CenterScansPassed);
            Assert.Equal(1, report.FailureCounts["png_signature_invalid"]);
            Assert.False(report.IsProven);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
