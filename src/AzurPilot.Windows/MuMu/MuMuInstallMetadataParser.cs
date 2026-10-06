using System.Text.Json;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Результат разбора документа install metadata.
/// </summary>
/// <remarks>
/// Неразобранный документ — не исключение, а значение: discovery решает, повлияла ли потеря документа
/// на результат, и сообщает причину отдельно от отказа операции.
/// </remarks>
public sealed record MuMuInstallMetadataParseResult
{
    /// <summary>Разобранные metadata или <see langword="null"/>, если документ не разобран.</summary>
    public MuMuInstallMetadata? Metadata { get; init; }

    /// <summary>Причина, по которой документ не разобран, или <see langword="null"/>.</summary>
    public string? InvalidReason { get; init; }

    /// <summary>Признак успешного разбора документа.</summary>
    public bool IsParsed => Metadata is not null;
}

/// <summary>
/// Parser install metadata установки MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Обязательными являются только те поля, из которых выводится решение: каталог установки продукта.
/// Версия продукта, идентификатор продукта и движки Android — evidence установки; их отсутствие не
/// делает документ недействительным, а неверный тип обязательного поля делает.
/// </para>
/// <para>
/// Разбор fail-closed в той же мере, что и разбор ответов control surface: неверный тип поля не
/// приводится к строке, а документ объявляется неразобранным. Значения версий при этом не проверяются
/// по allowlist — версия сообщается как факт.
/// </para>
/// </remarks>
public static class MuMuInstallMetadataParser
{
    /// <summary>Причина: содержимое документа не является корректным JSON-объектом.</summary>
    public const string InvalidJsonReason = "invalid_json";

    /// <summary>Причина: в документе нет обязательного каталога установки.</summary>
    public const string MissingInstallDirectoryReason = "missing_install_directory";

    /// <summary>Разбирает документ install metadata.</summary>
    /// <param name="filePath">Путь файла, из которого прочитан документ.</param>
    /// <param name="content">Содержимое документа.</param>
    /// <returns>Разобранные metadata либо причина отказа разбора.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> пуст.</exception>
    public static MuMuInstallMetadataParseResult Parse(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            return new MuMuInstallMetadataParseResult { InvalidReason = InvalidJsonReason };
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("product", out JsonElement product)
                || product.ValueKind != JsonValueKind.Object
                || !TryReadString(product, "install_dir", out string installDirectory)
                || string.IsNullOrWhiteSpace(installDirectory))
            {
                return new MuMuInstallMetadataParseResult { InvalidReason = MissingInstallDirectoryReason };
            }

            return new MuMuInstallMetadataParseResult
            {
                Metadata = new MuMuInstallMetadata
                {
                    FilePath = filePath,
                    ConfigVersion = TryReadString(root, "config_version", out string configVersion)
                        ? configVersion
                        : null,
                    InstallDirectory = installDirectory,
                    ProductName = TryReadString(product, "name", out string productName) ? productName : null,
                    ProductId = TryReadString(product, "product", out string productId) ? productId : null,
                    Version = TryReadString(product, "version", out string productVersion) ? productVersion : null,
                    AndroidEngines = ReadEngines(root),
                },
            };
        }
    }

    private static List<MuMuAndroidEngine> ReadEngines(JsonElement root)
    {
        if (!root.TryGetProperty("engines", out JsonElement engines) || engines.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<MuMuAndroidEngine> result = [];

        foreach (JsonProperty engine in engines.EnumerateObject())
        {
            if (engine.Value.ValueKind != JsonValueKind.Object
                || !engine.Value.TryGetProperty("player", out JsonElement player)
                || player.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new MuMuAndroidEngine
            {
                EngineKey = engine.Name,
                AndroidVersion = TryReadString(player, "android_version", out string androidVersion)
                    ? androidVersion
                    : null,
            });
        }

        return result;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}
