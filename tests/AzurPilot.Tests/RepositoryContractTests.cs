using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>Проверяет полезные ограничения checkout по файлам, которые действительно видит Git.</summary>
[Trait("Category", "Repository")]
public sealed class RepositoryContractTests
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".h", ".hpp", ".cpp", ".csproj", ".props", ".targets", ".slnx",
        ".json", ".yml", ".yaml", ".cmake", ".ps1", ".config", ".toml",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".lib", ".exe", ".pdb", ".obj", ".so", ".dylib", ".zip", ".7z", ".nupkg",
    };

    private static readonly Regex MachinePathPattern = new(
        @"(?<![A-Za-z0-9])[A-Za-z]" + ":" + @"[\\/]|\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_$.-]+\\",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ResolutionPattern = new(
        "1280" + @"\s*[xXхХ×,]\s*" + "720",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Fact(DisplayName = "Код и конфигурация не содержат machine-specific абсолютных путей")]
    public void SourceAndConfigurationDoNotContainMachineSpecificPaths()
    {
        AssertNoSourceMatches(MachinePathPattern, "machine-specific абсолютный путь");
    }

    [Fact(DisplayName = "Фундамент не закрепляет разрешение экрана")]
    public void FoundationDoesNotHardcodeScreenResolution()
    {
        AssertNoSourceMatches(ResolutionPattern, "hardcode фундаментального разрешения");
    }

    [Fact(DisplayName = "Git не отслеживает бинарники и не предлагает добавить build outputs")]
    public void GitVisibleFilesExcludeBinariesAndBuildOutputs()
    {
        string[] violations = [.. GitVisibleFiles()
            .Where(path => BinaryExtensions.Contains(Path.GetExtension(path))
                || path.Split('/').Any(segment => segment is "artifacts" or "bin" or "obj"))];

        Assert.True(violations.Length == 0, "Бинарники или build outputs видны Git: " + string.Join(", ", violations));
    }

    private static void AssertNoSourceMatches(Regex pattern, string description)
    {
        List<string> violations = [];
        foreach (string relativePath in GitVisibleFiles().Where(IsProjectSource))
        {
            string fullPath = Path.Combine(PinnedVersions.RepositoryRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                // Удалённый из working tree файл ещё может находиться в индексе до commit.
                continue;
            }

            int lineNumber = 0;
            foreach (string line in File.ReadLines(fullPath))
            {
                lineNumber++;
                if (pattern.IsMatch(line))
                {
                    violations.Add($"{relativePath}:{lineNumber}");
                }
            }
        }

        Assert.True(violations.Count == 0, $"Найден {description}: " + string.Join(", ", violations));
    }

    private static bool IsProjectSource(string path)
    {
        if (path.StartsWith("docs/", StringComparison.Ordinal)
            || path.StartsWith(".codex/context/", StringComparison.Ordinal))
        {
            return false;
        }

        return SourceExtensions.Contains(Path.GetExtension(path))
            || string.Equals(Path.GetFileName(path), "CMakeLists.txt", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(path), ".editorconfig", StringComparison.Ordinal);
    }

    private static string[] GitVisibleFiles()
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = PinnedVersions.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить Git для проверки checkout.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Git ls-files не завершился за 30 секунд.");
        }

        string files = output.GetAwaiter().GetResult();
        string diagnostics = error.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"Git ls-files завершился с кодом {process.ExitCode}: {diagnostics}");
        string[] paths = files.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(paths);
        return [.. paths.Distinct(StringComparer.Ordinal)];
    }
}
