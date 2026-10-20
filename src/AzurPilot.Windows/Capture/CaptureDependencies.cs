using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurPilot.Windows.Capture;

/// <summary>Закреплённая device-side зависимость с проверенным локальным файлом.</summary>
/// <param name="Id">Идентификатор из manifest.</param>
/// <param name="Version">Закреплённая версия.</param>
/// <param name="License">SPDX-идентификатор лицензии.</param>
/// <param name="Sha256">Ожидаемый SHA-256 (верхний регистр).</param>
/// <param name="LocalPath">Путь к проверенному файлу рядом с приложением.</param>
internal sealed record CaptureDependency(string Id, string Version, string License, string Sha256, string LocalPath);

/// <summary>Публичное описание закреплённой зависимости для матрицы совместимости и отчётов.</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Version">Версия или коммит.</param>
/// <param name="License">SPDX-лицензия.</param>
/// <param name="Source">Исходный репозиторий.</param>
/// <param name="Sha256">Ожидаемый SHA-256.</param>
/// <param name="VerificationFailure">Причина непригодности локального файла либо null, если SHA-256 совпал.</param>
public sealed record CaptureDependencyInfo(string Id, string Version, string License, string Source, string Sha256,
    string? VerificationFailure);

/// <summary>
/// Читает manifest зависимостей, staging-уемый сборкой рядом с приложением, и перед выдачей файла
/// сверяет его SHA-256. Файл с другим hash не используется: загрузок во время работы нет.
/// </summary>
public static class CaptureDependencies
{
    /// <summary>Каталог зависимостей рядом с приложением.</summary>
    public const string DirectoryName = "capture";

    /// <summary>Имя manifest зависимостей.</summary>
    public const string ManifestFileName = "capture-dependencies.json";

    /// <summary>Полный путь manifest рядом с приложением.</summary>
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, DirectoryName, ManifestFileName);

    /// <summary>Описание всех зависимостей manifest и результат проверки SHA-256 локального файла.</summary>
    public static IReadOnlyList<CaptureDependencyInfo> Describe()
    {
        Manifest? manifest;
        try
        {
            using FileStream stream = File.OpenRead(ManifestPath);
            manifest = JsonSerializer.Deserialize<Manifest>(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        return [.. (manifest?.Dependencies ?? []).Where(entry => entry.Id is not null).Select(entry =>
        {
            string? failure = null;
            try
            {
                _ = Get(entry.Id!);
            }
            catch (CaptureBackendException exception)
            {
                failure = exception.Reason;
            }

            return new CaptureDependencyInfo(entry.Id!, entry.Version ?? string.Empty, entry.License ?? string.Empty,
                entry.Source ?? string.Empty, entry.Sha256 ?? string.Empty, failure);
        })];
    }

    private static readonly ConcurrentDictionary<string, CaptureDependency> Verified = new(StringComparer.Ordinal);

    /// <summary>Возвращает проверенную зависимость.</summary>
    /// <exception cref="CaptureBackendException">Manifest, файл или hash недоступны либо не совпали.</exception>
    internal static CaptureDependency Get(string id)
    {
        if (Verified.TryGetValue(id, out CaptureDependency? cached))
        {
            return cached;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, DirectoryName);
        ManifestEntry? entry;
        try
        {
            using FileStream stream = File.OpenRead(ManifestPath);
            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(stream);
            entry = manifest?.Dependencies?.SingleOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CaptureBackendException("dependency_manifest_unavailable", detail: id, inner: exception);
        }

        if (entry is null || string.IsNullOrEmpty(entry.File) || entry.File.Contains('/', StringComparison.Ordinal)
            || entry.File.Contains('\\', StringComparison.Ordinal) || entry.Sha256 is not { Length: 64 })
        {
            throw new CaptureBackendException("dependency_not_declared", detail: id);
        }

        string path = Path.Combine(directory, entry.File);
        string actual;
        try
        {
            using FileStream file = File.OpenRead(path);
            actual = Convert.ToHexString(SHA256.HashData(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CaptureBackendException("dependency_missing", detail: id, inner: exception);
        }

        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureBackendException("dependency_digest_mismatch", detail: id);
        }

        CaptureDependency dependency = new(id, entry.Version ?? string.Empty, entry.License ?? string.Empty,
            entry.Sha256.ToUpperInvariant(), path);
        _ = Verified.TryAdd(id, dependency);
        return dependency;
    }

    private sealed record Manifest(
        [property: JsonPropertyName("dependencies")] IReadOnlyList<ManifestEntry>? Dependencies);

    private sealed record ManifestEntry(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("file")] string? File,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("license")] string? License,
        [property: JsonPropertyName("source")] string? Source);
}
