using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.Core.Game;

namespace AzurPilot.Windows.Game;

/// <summary>Проверенный вариант шаблона с воспроизводимым происхождением.</summary>
public sealed record GameAssetVariant(string Id, string File, string SourceSha256, int SourceWidth, int SourceHeight,
    GameRect Crop, string Sha256, NativeTemplateMode Mode, double MinimumScore, double MinimumMargin,
    double MaximumMeanAbsoluteError);

/// <summary>Единственный владелец ROI, безопасной области и способов распознавания ассета.</summary>
public sealed record GameAsset(string Id, string Purpose, GameRect Search, GameRect? SafeTapOffset,
    IReadOnlyList<GameAssetVariant> Variants);

/// <summary>Небольшой неизменяемый каталог встроенных игровых ресурсов Global/EN.</summary>
public sealed class GameAssetCatalog
{
    private const string ResourcePrefix = "AzurPilot.GameAssets.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly Lazy<GameAssetCatalog> DefaultCatalog = new(Load, LazyThreadSafetyMode.PublicationOnly);
    private readonly FrozenDictionary<string, GameAsset> _assets;
    private readonly FrozenDictionary<string, byte[]> _templates;
    private readonly FrozenDictionary<string, GameUiAction> _actions;
    /// <summary>Общий неизменяемый каталог с ограниченным кешем закодированных шаблонов.</summary>
    public static GameAssetCatalog Default => DefaultCatalog.Value;
    /// <summary>Проверенные игровые ассеты.</summary>
    public IReadOnlyCollection<GameAsset> Assets => _assets.Values;
    /// <summary>Разрешённые обратимые переходы.</summary>
    public IReadOnlyCollection<GameUiAction> Actions => _actions.Values;

    private GameAssetCatalog(Manifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Client != "Global/EN" || manifest.Assets.Count is < 1 or > 16)
        {
            throw new InvalidDataException("Неподдерживаемый каталог игровых ассетов.");
        }

        Dictionary<string, byte[]> templates = new(StringComparer.Ordinal);
        foreach (GameAsset asset in manifest.Assets)
        {
            GameGeometry.Validate(asset.Search);
            if (asset.Variants.Count is < 1 or > 4)
            {
                throw new InvalidDataException("Недопустимое число вариантов.");
            }

            foreach (GameAssetVariant variant in asset.Variants)
            {
                GameGeometry.Validate(variant.Crop);
                if (variant.SourceWidth < GameGeometry.LogicalWidth || variant.SourceHeight < GameGeometry.LogicalHeight
                    || (long)variant.SourceWidth * GameGeometry.LogicalHeight != (long)variant.SourceHeight * GameGeometry.LogicalWidth
                    || variant.MinimumScore is < 0.7 or > 1 || variant.MinimumMargin is < 0.01 or > 1
                    || variant.MaximumMeanAbsoluteError is < 0 or > 50 || !Enum.IsDefined(variant.Mode)
                    || variant.SourceSha256.Length != 64)
                {
                    throw new InvalidDataException("Непригодные свойства игрового шаблона.");
                }

                if (asset.SafeTapOffset is GameRect safe && (safe.Left < 0 || safe.Top < 0
                    || safe.Right > variant.Crop.Right - variant.Crop.Left || safe.Bottom > variant.Crop.Bottom - variant.Crop.Top
                    || safe.Right <= safe.Left || safe.Bottom <= safe.Top))
                {
                    throw new InvalidDataException("Область tap должна находиться внутри распознанного элемента.");
                }

                byte[] bytes = ReadResource(variant.File);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), variant.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Контрольная сумма шаблона не совпадает.");
                }

                using NativeFrame template = AzurPilotNativeBridge.DecodePng(bytes);
                NativeFrameInfo info = template.GetInfo();
                GamePixelRect expected = new GameGeometry(variant.SourceWidth, variant.SourceHeight).Map(variant.Crop);
                if (info.Width != expected.Right - expected.Left || info.Height != expected.Bottom - expected.Top)
                {
                    throw new InvalidDataException("Размер шаблона не соответствует provenance crop.");
                }

                templates.Add(variant.File, bytes);
            }
        }

        _assets = manifest.Assets.ToFrozenDictionary(a => a.Id,
            a => a with { Variants = Array.AsReadOnly(a.Variants.ToArray()) }, StringComparer.Ordinal);
        _templates = templates.ToFrozenDictionary(StringComparer.Ordinal);
        _actions = manifest.Actions.ToFrozenDictionary(a => a.Id,
            a => a with { ExcludedAssetIds = Array.AsReadOnly((a.ExcludedAssetIds ?? []).ToArray()) }, StringComparer.Ordinal);
        foreach (GameUiAction action in _actions.Values)
        {
            if (action.ExcludedAssetIds!.Count > 4 || action.ExcludedAssetIds.Any(id => !_assets.ContainsKey(id)
                || id == action.SourceAssetId || id == action.PostconditionAssetId))
            {
                throw new InvalidDataException("Недопустимый исключающий признак игрового перехода.");
            }

            if (!_assets.ContainsKey(action.PostconditionAssetId) || !_assets.TryGetValue(action.SourceAssetId, out GameAsset? source)
                || source.SafeTapOffset is null || action.SourceAssetId == action.PostconditionAssetId
                || action.MaximumObservationAge <= TimeSpan.Zero || action.MaximumObservationAge > TimeSpan.FromSeconds(15)
                || action.PostconditionTimeout <= TimeSpan.Zero || action.PostconditionTimeout > TimeSpan.FromSeconds(30))
            {
                throw new InvalidDataException("Непригодный игровой переход.");
            }
        }
    }

    /// <summary>Возвращает известный ассет; подстановки по имени нет.</summary>
    public GameAsset GetAsset(string id)
    {
        return _assets[id];
    }

    /// <summary>Возвращает разрешённый переход.</summary>
    public GameUiAction GetAction(string id)
    {
        return _actions[id];
    }

    internal ReadOnlySpan<byte> GetTemplate(GameAssetVariant variant)
    {
        return _templates[variant.File];
    }

    private static GameAssetCatalog Load()
    {
        return new(JsonSerializer.Deserialize<Manifest>(ReadResource("catalog.json"), JsonOptions)
            ?? throw new InvalidDataException("Каталог игровых ассетов пуст."));
    }

    private static byte[] ReadResource(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Путь ресурса не является именем встроенного ассета.");
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException("Встроенный игровой ресурс не найден.");
        if (stream.Length is < 1 or > 262144)
        {
            throw new InvalidDataException("Игровой ресурс превышает бюджет.");
        }

        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed record Manifest(int SchemaVersion, string Client, IReadOnlyList<GameAsset> Assets, IReadOnlyList<GameUiAction> Actions);
}
