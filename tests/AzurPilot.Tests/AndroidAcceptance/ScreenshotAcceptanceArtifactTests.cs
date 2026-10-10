using System.Globalization;
using System.Text;
using System.Text.Json;
using AzurPilot.AndroidAcceptance;
using Xunit;

namespace AzurPilot.Tests.AndroidAcceptance;

/// <summary>Раздельные исходы scan/capture и сохранения обезличенного отчёта.</summary>
public sealed class ScreenshotAcceptanceArtifactTests
{
    [Theory(DisplayName = "JSON и exit code различают proof и сохранение report artifact")]
    [InlineData("local_samples", true, false, false, null, 0)]
    [InlineData("local_samples", true, true, true, null, 0)]
    [InlineData("local_samples", true, true, false, "screenshot_report_artifact_failed", 3)]
    [InlineData("local_samples", false, false, false, null, 1)]
    [InlineData("local_samples", false, true, true, null, 1)]
    [InlineData("local_samples", false, true, false, "screenshot_report_artifact_failed", 1)]
    [InlineData("live_capture", true, false, false, null, 0)]
    [InlineData("live_capture", true, true, true, null, 0)]
    [InlineData("live_capture", true, true, false, "screenshot_report_artifact_failed", 3)]
    [InlineData("live_capture", false, false, false, null, 1)]
    [InlineData("live_capture", false, true, true, null, 1)]
    [InlineData("live_capture", false, true, false, "screenshot_report_artifact_failed", 1)]
    public void OutputKeepsProofAndArtifactOutcomesSeparate(
        string mode,
        bool proof,
        bool requested,
        bool written,
        string? artifactFailureCode,
        int expectedExitCode)
    {
        string? scanFailureCode = proof ? null : "scan_not_proven";
        ScreenshotAcceptanceOutput<object> output = new(
            Mode: mode,
            IsProven: proof,
            FailureCode: scanFailureCode,
            ReportArtifactRequested: requested,
            ReportArtifactWritten: written,
            ArtifactFailureCode: artifactFailureCode,
            Evidence: new { IsProven = proof });

        Assert.Equal(expectedExitCode, output.ExitCode);
        using JsonDocument document = JsonDocument.Parse(ScreenshotAcceptanceJson.Serialize(output));
        JsonElement json = document.RootElement;
        Assert.Equal(mode, json.GetProperty("mode").GetString());
        Assert.Equal(proof, json.GetProperty("isProven").GetBoolean());
        Assert.Equal(proof, json.GetProperty("evidence").GetProperty("isProven").GetBoolean());
        Assert.Equal(scanFailureCode, json.GetProperty("failureCode").GetString());
        Assert.Equal(requested, json.GetProperty("reportArtifactRequested").GetBoolean());
        Assert.Equal(written, json.GetProperty("reportArtifactWritten").GetBoolean());
        Assert.Equal(artifactFailureCode, json.GetProperty("artifactFailureCode").GetString());
        Assert.False(json.TryGetProperty("exitCode", out _));
    }

    [Fact(DisplayName = "Без report artifact недоступный repository root не мешает результату scan")]
    public void NoArtifactRequestedDoesNotRequireRepositoryRoot()
    {
        using ArtifactFixture fixture = new();

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            null, "{}", fixture.Root, fixture.Root, out string reason);

        Assert.True(written);
        Assert.Empty(reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact(DisplayName = "Запрошенный report artifact без repository root возвращает bounded отказ")]
    public void RequestedArtifactWithoutRepositoryRootFails()
    {
        using ArtifactFixture fixture = new();

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{}", fixture.Root, fixture.Root, out string reason);

        Assert.False(written);
        Assert.Equal("repository_root_not_found", reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact(DisplayName = "Публикуемый output вне repository root не превращает artifact в успех")]
    public void PublishedOutputOutsideRepositoryCannotPersistArtifact()
    {
        using ArtifactFixture fixture = new();
        string unrelatedWorkingDirectory = Directory.CreateDirectory(
            Path.Combine(fixture.Root, "working-directory")).FullName;
        string publishedOutput = Directory.CreateDirectory(
            Path.Combine(fixture.Root, "published-output")).FullName;

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{}", unrelatedWorkingDirectory, publishedOutput, out string reason);

        Assert.False(written);
        Assert.Equal("repository_root_not_found", reason);
        Assert.False(Directory.Exists(Path.Combine(unrelatedWorkingDirectory, "artifacts")));
        Assert.False(Directory.Exists(Path.Combine(publishedOutput, "artifacts")));
    }

    [Fact(DisplayName = "Другой cwd допускает уникальный report artifact через application repository root")]
    public void UnrelatedWorkingDirectoryUsesApplicationRepositoryRoot()
    {
        using ArtifactFixture fixture = new();
        string repository = fixture.CreateRepository("application-repository");
        string applicationDirectory = Directory.CreateDirectory(Path.Combine(repository, "output")).FullName;
        string unrelatedWorkingDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "cwd")).FullName;
        string artifactName = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json";
        const string reportJson = "{\"isProven\":true,\"description\":\"обезличенный отчёт\"}";

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            artifactName, reportJson, unrelatedWorkingDirectory, applicationDirectory, out string reason);

        Assert.True(written);
        Assert.Empty(reason);
        string artifactPath = Path.Combine(repository, "artifacts", "screenshot-diagnostics", artifactName);
        Assert.Equal(reportJson + Environment.NewLine, File.ReadAllText(artifactPath, Encoding.UTF8));
        Assert.Equal(Encoding.UTF8.GetBytes(reportJson + Environment.NewLine), File.ReadAllBytes(artifactPath));
        Assert.False(Directory.Exists(Path.Combine(unrelatedWorkingDirectory, "artifacts")));
    }

    [Fact(DisplayName = "Repository root текущего cwd имеет приоритет перед application output root")]
    public void WorkingRepositoryRootTakesPriority()
    {
        using ArtifactFixture fixture = new();
        string workingRepository = fixture.CreateRepository("working-repository");
        string applicationRepository = fixture.CreateRepository("application-repository");
        string nestedWorkingDirectory = Directory.CreateDirectory(
            Path.Combine(workingRepository, "nested", "cwd")).FullName;

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{}", nestedWorkingDirectory, applicationRepository, out string reason);

        Assert.True(written);
        Assert.Empty(reason);
        Assert.True(File.Exists(Path.Combine(workingRepository, "artifacts", "screenshot-diagnostics", "report.json")));
        Assert.False(Directory.Exists(Path.Combine(applicationRepository, "artifacts")));
    }

    [Fact(DisplayName = "Report artifact не перезаписывает существующий файл")]
    public void ExistingArtifactRemainsUnchanged()
    {
        using ArtifactFixture fixture = new();
        string repository = fixture.CreateRepository("repository");
        Assert.True(ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{\"first\":true}", repository, fixture.Root, out _));
        string artifactPath = Path.Combine(repository, "artifacts", "screenshot-diagnostics", "report.json");
        byte[] originalBytes = File.ReadAllBytes(artifactPath);

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{\"second\":true}", repository, fixture.Root, out string reason);

        Assert.False(written);
        Assert.Equal("artifact_write_failed_or_already_exists", reason);
        Assert.Equal(originalBytes, File.ReadAllBytes(artifactPath));
    }

    [Fact(DisplayName = "Невозможный каталог report artifact возвращает bounded отказ записи")]
    public void ArtifactDirectoryConflictReturnsWriteFailure()
    {
        using ArtifactFixture fixture = new();
        string repository = fixture.CreateRepository("repository");
        string artifactsDirectory = Directory.CreateDirectory(Path.Combine(repository, "artifacts")).FullName;
        string conflictingFile = Path.Combine(artifactsDirectory, "screenshot-diagnostics");
        File.WriteAllText(conflictingFile, "existing-file");

        bool written = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            "report.json", "{}", repository, fixture.Root, out string reason);

        Assert.False(written);
        Assert.Equal("artifact_write_failed_or_already_exists", reason);
        Assert.Equal("existing-file", File.ReadAllText(conflictingFile));
    }

    private sealed class ArtifactFixture : IDisposable
    {
        internal ArtifactFixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "AzurPilot.Tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)))
                .FullName;
        }

        internal string Root { get; }

        internal string CreateRepository(string name)
        {
            string repository = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(repository, ".git"), "git metadata marker");
            File.WriteAllText(Path.Combine(repository, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(repository, "AzurPilot.slnx"), "<Solution />");
            return repository;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
