using System.Text;

namespace AzurPilot.AndroidAcceptance;

internal static class ScreenshotAcceptanceArtifactWriter
{
    internal static bool TryWriteReport(string? artifactName, string reportJson, out string reason)
    {
        ArgumentNullException.ThrowIfNull(reportJson);
        if (artifactName is null)
        {
            reason = string.Empty;
            return true;
        }

        if (!string.Equals(Path.GetFileName(artifactName), artifactName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(artifactName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            reason = "artifact_name_invalid";
            return false;
        }

        string? repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            reason = "repository_root_not_found";
            return false;
        }

        string artifactDirectory = Path.Combine(repositoryRoot, "artifacts", "screenshot-diagnostics");
        string artifactPath = Path.Combine(artifactDirectory, artifactName);
        try
        {
            _ = Directory.CreateDirectory(artifactDirectory);
            using FileStream stream = new(
                artifactPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(reportJson);
            reason = string.Empty;
            return true;
        }
        catch (IOException)
        {
            reason = "artifact_write_failed_or_already_exists";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "artifact_write_denied";
            return false;
        }
    }

    private static string? FindRepositoryRoot(string startPath)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            bool hasGitMetadata = Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git"));
            if (hasGitMetadata
                && File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "AzurPilot.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
