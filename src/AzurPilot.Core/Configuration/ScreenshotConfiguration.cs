using System.Text.Json.Serialization;
using AzurPilot.Core.Capture;

namespace AzurPilot.Core.Configuration;

/// <summary>Настройки транспорта снимков экрана и интервалов наблюдения.</summary>
/// <remarks>
/// Секция необязательна: документ без неё получает значения <see cref="AzurPilotConfigurationDefaults"/>.
/// Значение метода — <c>auto</c> либо точное имя из <see cref="ScreenshotMethodNames"/>.
/// </remarks>
public sealed record ScreenshotConfiguration
{
    /// <summary>Нижняя граница интервалов, мс.</summary>
    public const int MinimumIntervalMilliseconds = 1;

    /// <summary>Верхняя граница обычного интервала, мс.</summary>
    public const int MaximumNormalIntervalMilliseconds = 300;

    /// <summary>Верхняя граница боевого интервала, мс.</summary>
    public const int MaximumCombatIntervalMilliseconds = 1000;

    /// <summary>Метод снимка: <c>auto</c> или имя транспорта.</summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>Период start→start наблюдения постусловия игрового действия вне боя, мс.</summary>
    [JsonPropertyName("normalIntervalMs")]
    public required int NormalIntervalMilliseconds { get; init; }

    /// <summary>Период start→start наблюдения в бою, мс.</summary>
    [JsonPropertyName("combatIntervalMs")]
    public required int CombatIntervalMilliseconds { get; init; }

    /// <summary>Разобранное значение метода.</summary>
    [JsonIgnore]
    public ScreenshotMethodSetting MethodSetting => ScreenshotMethodNames.TryParseSetting(Method, out ScreenshotMethodSetting? setting)
        ? setting
        : throw new InvalidOperationException("Значение screenshot.method не прошло проверку схемы.");
}
