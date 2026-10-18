using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Game;

/// <summary>Классификация распознавания, отдельно от результата ввода.</summary>
public enum GameMatchStatus
{
    /// <summary>Единственный подтверждённый кандидат.</summary>
    Unique,
    /// <summary>Достаточных визуальных доказательств нет.</summary>
    Absent,
    /// <summary>Конкурирующие кандидаты.</summary>
    Ambiguous,
    /// <summary>Геометрия не поддерживается.</summary>
    GeometryRejected,
    /// <summary>Технический отказ.</summary>
    TechnicalFailure,
}

/// <summary>Происхождение одного свежего наблюдения; временная метка принадлежит монотонным часам текущего процесса.</summary>
public sealed record GameFrameProvenance(Guid FrameId, MuMuInstanceId InstanceId, AndroidEndpoint Endpoint,
    DateTimeOffset CaptureStartedAtUtc, long CaptureStartedTimestamp, int Width, int Height);

/// <summary>Доказательства одного известного ассета без пикселей.</summary>
public sealed record GameUiMatch(string AssetId, string? VariantId, GameMatchStatus Status,
    GamePixelRect? Bounds, GamePixelRect? SafeTapBounds, double Score, double RunnerUpScore,
    double MeanAbsoluteError, string Reason);

/// <summary>Один кадр и результаты поиска в нём.</summary>
public sealed record GameUiObservation(GameFrameProvenance Frame, IReadOnlyList<GameUiMatch> Matches)
{
    /// <summary>Требует единственного результата запрошенного ассета.</summary>
    public GameUiMatch? Find(string id)
    {
        return Matches.SingleOrDefault(m => string.Equals(m.AssetId, id, StringComparison.Ordinal));
    }
}

/// <summary>Разрешённый переход между независимо наблюдаемыми признаками.</summary>
public sealed record GameUiAction(string Id, string SourceAssetId, string PostconditionAssetId,
    TimeSpan MaximumObservationAge, TimeSpan PostconditionTimeout, IReadOnlyList<string>? ExcludedAssetIds = null);

/// <summary>Однократное разрешение нажатия, создаваемое Core после проверки распознавания, области игры и свежести кадра.</summary>
public sealed class GameTap
{
    private int _consumed;
    internal GameTap(GameFrameProvenance frame, string assetId, GamePixelRect safeBounds, GameInputPoint point, TimeSpan maximumAge)
    {
        Frame = frame;
        AssetId = assetId;
        SafeBounds = safeBounds;
        Point = point;
        MaximumObservationAge = maximumAge;
    }

    /// <summary>Кадр, который разрешил действие.</summary>
    public GameFrameProvenance Frame { get; }
    /// <summary>Распознанный ассет.</summary>
    public string AssetId { get; }
    /// <summary>Проверенная безопасная область.</summary>
    public GamePixelRect SafeBounds { get; }
    /// <summary>Разрешённая точка ввода.</summary>
    public GameInputPoint Point { get; }
    /// <summary>Срок действия, заданный исходным переходом.</summary>
    public TimeSpan MaximumObservationAge { get; }
    /// <summary>Атомарно расходует разрешение; повторный вызов запрещает второй ввод.</summary>
    public bool TryConsume()
    {
        return Interlocked.Exchange(ref _consumed, 1) == 0;
    }
}

/// <summary>Граница наблюдения, проверки области игры Android и одного адресованного нажатия.</summary>
public interface IGameUiHost
{
    /// <summary>Новый захват кадра точно выбранного устройства; старый кадр не переиспользуется.</summary>
    Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        IReadOnlyList<string> assets, CancellationToken cancellationToken);

    /// <summary>Доказывает передний план, полноэкранную область игры и совпадение координат ввода и кадра.</summary>
    Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken);

    /// <summary>Ровно одно нажатие через тот же поставляемый ADB, без повторной попытки.</summary>
    Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken);
}

/// <summary>Сохраняет известный частичный эффект при исключениях и отмене.</summary>
public sealed class GameActionProgress
{
    /// <summary>Вызвана внешняя операция изменения состояния.</summary>
    public bool MutationAttempted { get; internal set; }
    /// <summary>Переход доказан отдельным кадром.</summary>
    public bool ChangeProven { get; internal set; }
}

/// <summary>Итог действия с ограничением времени; Unknown не означает NoEffect.</summary>
public sealed record GameActionOutcome(string ActionId, GameUiObservation? Before, GameUiObservation? After,
    GameInputPoint? Point, bool MutationAttempted, bool ChangeProven, ApplicationFailure? Failure);
