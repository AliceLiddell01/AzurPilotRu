using AzurPilot.Core.Game;

namespace AzurPilot.Windows.Game;

/// <summary>Распознаёт известные ассеты на исходном нативном кадре, масштабируя только шаблоны.</summary>
public sealed class GameUiRecognizer(GameAssetCatalog catalog)
{
    /// <summary>Возвращает происхождение кадра и типизированные результаты распознавания; никогда не выдаёт разрешение ввода.</summary>
    public GameUiObservation Observe(NativeFrame frame, GameFrameProvenance provenance, IReadOnlyList<string> assetIds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(assetIds);
        NativeFrameInfo info = frame.GetInfo();
        GameGeometry geometry;
        try
        {
            geometry = new(checked((int)info.Width), checked((int)info.Height));
            if (info.Width != provenance.Width || info.Height != provenance.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(provenance));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(provenance, [.. assetIds.Select(id => Empty(id, GameMatchStatus.GeometryRejected, "unsupported_frame_geometry"))]);
        }

        return new(provenance, [.. assetIds.Select(id => Match(frame, geometry, catalog.GetAsset(id)))]);
    }

    private GameUiMatch Match(NativeFrame frame, GameGeometry geometry, GameAsset asset)
    {
        List<GameUiMatch> candidates = [];
        try
        {
            GamePixelRect search = geometry.Map(asset.Search);
            foreach (GameAssetVariant variant in asset.Variants)
            {
                GamePixelRect template = geometry.Map(variant.Crop);
                NativeTemplateMatch match = frame.MatchTemplate(new(search.Left, search.Top, search.Right, search.Bottom),
                    catalog.GetTemplate(variant), template.Right - template.Left, template.Bottom - template.Top, variant.Mode);
                GameMatchStatus status = match.Score < variant.MinimumScore || match.MeanAbsoluteError > variant.MaximumMeanAbsoluteError
                    ? GameMatchStatus.Absent
                    : match.RunnerUpScore >= variant.MinimumScore || match.Score - match.RunnerUpScore < variant.MinimumMargin
                        ? GameMatchStatus.Ambiguous : GameMatchStatus.Unique;
                GamePixelRect bounds = new(match.Bounds.Left, match.Bounds.Top, match.Bounds.Right, match.Bounds.Bottom);
                GamePixelRect? safe = null;
                if (asset.SafeTapOffset is GameRect offset)
                {
                    // Область действия округляется внутрь, затем переносится вслед за найденным элементом.
                    int localWidth = variant.Crop.Right - variant.Crop.Left;
                    int localHeight = variant.Crop.Bottom - variant.Crop.Top;
                    int width = bounds.Right - bounds.Left;
                    int height = bounds.Bottom - bounds.Top;
                    safe = new(bounds.Left + (((offset.Left * width) + localWidth - 1) / localWidth),
                        bounds.Top + (((offset.Top * height) + localHeight - 1) / localHeight),
                        bounds.Left + (offset.Right * width / localWidth), bounds.Top + (offset.Bottom * height / localHeight));
                }

                candidates.Add(new(asset.Id, variant.Id, status, bounds, status == GameMatchStatus.Unique ? safe : null,
                    match.Score, match.RunnerUpScore, match.MeanAbsoluteError, status.ToString()));
            }
        }
        catch (AzurPilotNativeBoundaryException)
        {
            return Empty(asset.Id, GameMatchStatus.TechnicalFailure, "native_match_failed");
        }

        GameUiMatch best = candidates.OrderByDescending(c => c.Status == GameMatchStatus.Unique).ThenByDescending(c => c.Score).First();
        return candidates.Any(c => c.Status == GameMatchStatus.Ambiguous)
            || candidates.Any(c => c.Status == GameMatchStatus.Unique && c.Bounds != best.Bounds)
            ? (best with { Status = GameMatchStatus.Ambiguous, SafeTapBounds = null, Reason = "competing_candidates" })
            : best;
    }

    private static GameUiMatch Empty(string id, GameMatchStatus status, string reason)
    {
        return new(id, null, status, null, null, 0, 0, 0, reason);
    }
}
