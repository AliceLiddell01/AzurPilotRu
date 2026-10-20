using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzurPilot.App.Agent;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>
/// Локальные отчёты измерений транспортов: <c>.codex/local/reports/&lt;topic&gt;/&lt;run-id&gt;/</c> ближайшего
/// checkout (каталог игнорируется Git). Кадры, UID и пользовательские данные в отчёт не попадают.
/// </summary>
internal sealed class CaptureReportWriter
{
    private CaptureReportWriter(string root, string directory, string relative, string runId)
    {
        CheckoutRoot = root;
        Directory = directory;
        RelativeDirectory = relative;
        RunId = runId;
    }

    internal string CheckoutRoot { get; }

    internal string Directory { get; }

    internal string RelativeDirectory { get; }

    internal string RunId { get; }

    /// <summary>Создаёт каталог прогона либо возвращает null с причиной; ссылки в пути отвергаются.</summary>
    internal static CaptureReportWriter? Create(string topic, string runId, out string? reason)
        => Open(topic, runId, create: true, out reason);

    /// <summary>Открывает каталог существующего прогона без создания; отсутствие — <c>session_not_found</c>.</summary>
    internal static CaptureReportWriter? OpenExisting(string topic, string runId, out string? reason)
        => Open(topic, runId, create: false, out reason);

    private static CaptureReportWriter? Open(string topic, string runId, bool create, out string? reason)
    {
        reason = null;
        string? root = AgentDebugRun.FindCheckoutRoot(Environment.CurrentDirectory)
            ?? AgentDebugRun.FindCheckoutRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            reason = "checkout_root_not_found";
            return null;
        }

        try
        {
            string[] parts = [".codex", "local", "reports", topic, runId];
            string current = root;
            foreach (string part in parts)
            {
                current = Path.Combine(current, part);
                if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "report_path_reparse_point";
                    return null;
                }

                if (!create && !System.IO.Directory.Exists(current))
                {
                    reason = "session_not_found";
                    return null;
                }

                _ = System.IO.Directory.CreateDirectory(current);
            }

            return new(root, current, string.Join('/', parts), runId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "report_directory_unwritable";
            return null;
        }
    }

    internal static string NewRunId(string operation)
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + operation + "-"
            + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>Провенанс сборки и checkout для отчёта.</summary>
    internal object Provenance()
    {
        (string? head, string? branch) = AgentDebugRun.GitHead(CheckoutRoot);
        return new
        {
            gitHead = head,
            gitBranch = branch,
            dirty = (bool?)null,
            dirtyNote = "Состояние рабочей копии фиксирует live-приёмка (git status), приложение его не читает.",
            build = AgentDebugRun.Build(),
            captureDependencies = DependencyDigest(),
        };
    }

    internal void WriteJson(string name, object value)
    {
        string path = Path.Combine(Directory, name);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, AgentJson.Options);
    }

    internal void WriteText(string name, string text)
        => File.WriteAllText(Path.Combine(Directory, name), text, new UTF8Encoding(false));

    private static string? DependencyDigest()
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CaptureDependencies.ManifestPath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
