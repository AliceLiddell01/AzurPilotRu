using System.Globalization;
using System.Text.Json.Serialization;
using AzurPilot.Core.Configuration;

namespace AzurPilot.App.Agent;

internal sealed record AgentArgumentType(
    string Type, string Description,
    [property: JsonIgnore] Func<string, bool> IsValid,
    int? Minimum = null, int? Maximum = null, string? Pattern = null,
    string? Platform = null, bool? FullyQualified = null,
    string? AutomaticValue = null, string? IdentityPrefix = null, string? Index = null)
{
    public int MaximumLength { get; init; } = AgentRequest.MaximumArgumentLength;
}

/// <summary>Общий реестр описаний и проверок реально поддерживаемых типов CLI.</summary>
internal static class AgentArgumentTypes
{
    internal static readonly IReadOnlyDictionary<string, AgentArgumentType> All =
        new Dictionary<string, AgentArgumentType>(StringComparer.Ordinal)
        {
            ["MuMuInstanceValue"] = new("string", "auto либо каноническая identity MuMu; регистр значим.",
                MuMuInstanceValue.IsValid, Pattern: "^(" + MuMuInstanceValue.AutoValue + "|"
                    + MuMuInstanceValue.ProviderPrefix + "(0|[1-9][0-9]*))$(?![\\s\\S])",
                AutomaticValue: MuMuInstanceValue.AutoValue, IdentityPrefix: MuMuInstanceValue.ProviderPrefix,
                Index: "Десятичные ASCII-цифры: ноль либо положительное целое без ведущих нулей."),
            ["integer"] = new("integer", "Десятичные ASCII-цифры без знака и пробелов; ведущие нули допустимы.",
                value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= AgentRequest.MinimumTimeoutMilliseconds and <= AgentRequest.MaximumTimeoutMilliseconds,
                AgentRequest.MinimumTimeoutMilliseconds, AgentRequest.MaximumTimeoutMilliseconds, "^[0-9]+$"),
            ["debug-session"] = new("string", "Безопасный идентификатор сессии debug-отчёта: 1..64 строчных ASCII-букв, цифр и дефисов без дефиса по краям и имён устройств Windows.",
                AgentDebugRun.IsValidSession, Pattern: AgentDebugRun.SessionPattern)
            {
                MaximumLength = AgentDebugRun.MaximumSessionLength,
            },
            ["screenshot-methods"] = new("string", "all либо список точных имён транспортов через запятую без пробелов: "
                + string.Join(", ", Core.Capture.ScreenshotMethodNames.All.Select(Core.Capture.ScreenshotMethodNames.ToName)) + ".",
                AgentCaptureArguments.IsValidMethods, Pattern: "^(all|[A-Za-z0-9_]+(,[A-Za-z0-9_]+)*)$"),
            ["screenshot-method"] = new("string", "Ровно одно точное имя транспорта.",
                value => Core.Capture.ScreenshotMethodNames.TryParse(value, out _), Pattern: "^[A-Za-z0-9_]+$"),
            ["benchmark-profile"] = new("string", "short (20–30 снимков), full (100–300), stress (до 1000).",
                value => value is "short" or "full" or "stress", Pattern: "^(short|full|stress)$"),
            ["capture-scene"] = new("string", "normal — доказанный главный экран; combat — бой, открытый пользователем вручную.",
                value => value is "normal" or "combat", Pattern: "^(normal|combat)$"),
            ["sample-count"] = new("integer", "Число снимков серии: 1..1000.",
                value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 1 and <= 1000, 1, 1000, "^[0-9]+$"),
            ["boolean"] = new("string", "true или false.", value => value is "true" or "false", Pattern: "^(true|false)$"),
            ["confirmation-code"] = new("string", "Одноразовый код подтверждения боевой фазы: 8 заглавных букв и цифр.",
                value => value.Length == 8 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')),
                Pattern: "^[A-Z0-9]{8}$"),
            ["interval-grid"] = new("string", "Список интервалов в миллисекундах через запятую (1..1000, до 16 значений).",
                AgentCaptureArguments.IsValidGrid, Pattern: "^[0-9]+(\\.[0-9]+)?(,[0-9]+(\\.[0-9]+)?)*$"),
            ["absolute-path"] = new("string", "Полностью квалифицированный путь Windows: корень диска либо UNC; существование файла не требуется. Относительные пути и пути от текущего диска отклоняются.",
                Path.IsPathFullyQualified, Platform: "windows", FullyQualified: true),
        };
}
