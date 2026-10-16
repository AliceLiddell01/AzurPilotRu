using System.Security.Cryptography;

namespace AzurPilot.Core.Configuration;

/// <summary>Узкая граница переноса и удаления файлов; проверки и запись остаются в production-коде.</summary>
internal sealed class ConfigurationFileOperations
{
    internal static ConfigurationFileOperations System { get; } = new();
    internal static Lock WriteGate { get; } = new();
    internal Action<string, string> Move { get; init; } = static (source, target) => File.Move(source, target);
    internal Action<string> Delete { get; init; } = File.Delete;
    internal Action<string, string, string> Replace { get; init; } = static (source, target, backup) => File.Replace(source, target, backup);
    internal static string VersionOfFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string Version(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
