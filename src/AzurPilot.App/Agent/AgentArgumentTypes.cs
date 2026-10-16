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
    public int MaximumLength { get; } = AgentRequest.MaximumArgumentLength;
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
            ["absolute-path"] = new("string", "Полностью квалифицированный путь Windows: корень диска либо UNC; существование файла не требуется. Относительные пути и пути от текущего диска отклоняются.",
                Path.IsPathFullyQualified, Platform: "windows", FullyQualified: true),
        };
}
