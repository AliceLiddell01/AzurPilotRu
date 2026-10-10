using System.Globalization;
using AzurPilot.AndroidAcceptance;
using AzurPilot.Tests.Diagnostics;
using Xunit;

namespace AzurPilot.Tests.AndroidAcceptance;

/// <summary>Проверки доказательности bounded sample acceptance.</summary>
[Trait("Category", "Integration")]
[Collection(InteropCollection.Name)]
public sealed class ScreenshotSampleRunnerTests
{
    [Theory(DisplayName = "Неполное перечисление sample не выбирает произвольный поднабор PNG")]
    [InlineData(501)]
    [InlineData(507)]
    public void EnumerationCapPreventsSelectionAndDecode(int fileCount)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AzurPilot.Tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(directory);

        try
        {
            // Любой порядок перечисления содержит поддерживаемые PNG. Проверка не зависит от того,
            // какой из файлов оказался первым или за лимитом.
            byte[] png = ScreenshotPngFixture.CreateRgb(16, 9, static (_, _) => (byte.MaxValue, 0, 0));
            for (int index = 0; index < fileCount; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, index.ToString("D4", CultureInfo.InvariantCulture) + ".png"),
                    png);
            }

            ScreenshotSampleRunReport report = ScreenshotSampleRunner.Run(directory);

            Assert.True(report.EnumerationSucceeded);
            Assert.True(report.EnumerationCapped);
            Assert.False(report.IsProven);
            Assert.Equal(500, report.EnumeratedFiles);
            Assert.Equal(0, report.SelectedCount);
            Assert.Equal(0, report.DecodedCount);
            Assert.Equal(0, report.FullFrameScansPassed);
            Assert.Equal(0, report.CenterScansPassed);
            Assert.Empty(report.SelectedDimensions);
            Assert.Empty(report.DecodedDimensions);
            Assert.Single(report.FailureCounts);
            Assert.Equal(1, report.FailureCounts["directory_enumeration_capped"]);
            string json = ScreenshotAcceptanceJson.Serialize(report);
            Assert.DoesNotContain(directory, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".png", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "Ровно 500 sample файлов завершает перечисление и допускает scan")]
    public void ExactEnumerationLimitCanStillProveSamples()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AzurPilot.Tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(directory);

        try
        {
            byte[] png = ScreenshotPngFixture.CreateRgb(16, 9, static (_, _) => (byte.MaxValue, 0, 0));
            for (int index = 0; index < 500; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, index.ToString("D4", CultureInfo.InvariantCulture) + ".png"),
                    png);
            }

            ScreenshotSampleRunReport report = ScreenshotSampleRunner.Run(directory);

            Assert.True(report.EnumerationSucceeded);
            Assert.False(report.EnumerationCapped);
            Assert.Equal(500, report.EnumeratedFiles);
            Assert.True(report.IsProven);
            Assert.Equal(3, report.SelectedCount);
            Assert.Equal(report.SelectedCount, report.DecodedCount);
            Assert.Equal(report.SelectedCount, report.FullFrameScansPassed);
            Assert.Equal(report.SelectedCount, report.CenterScansPassed);
            Assert.Empty(report.FailureCounts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
