using System.Text;
using System.Text.RegularExpressions;

namespace AzurPilot.App.Diagnostics;

/// <summary>Единая санитизация пользовательских представлений журнала до сохранения и усечения.</summary>
internal static partial class AzurPilotLogSanitizer
{
    internal static string Sanitize(string? value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder clean = new(Math.Min(value.Length, maximumLength));
        foreach (Rune rune in value.EnumerateRunes())
        {
            string text = Rune.IsControl(rune) ? " " : rune.ToString();
            if (clean.Length + text.Length > maximumLength)
            {
                if (clean.Length > maximumLength - 1)
                {
                    clean.Length -= clean.Length >= 2 && char.IsLowSurrogate(clean[^1])
                        && char.IsHighSurrogate(clean[^2]) ? 2 : 1;
                }

                _ = clean.Append('…');
                return clean.ToString();
            }

            _ = clean.Append(text);
        }

        return clean.ToString();
    }

    internal static bool IsSensitive(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("passwd", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Equals("key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("accesskey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    internal static string Property(string key, string value, int maximumLength)
        => IsSensitive(key) ? "[скрыто]" : Text(value, maximumLength);

    internal static string Message(string value, IEnumerable<KeyValuePair<string, string>>? properties, int maximumLength)
    {
        if (properties is not null)
        {
            foreach (var pair in properties)
            {
                if (IsSensitive(pair.Key) && pair.Value.Length != 0)
                {
                    value = value.Replace(pair.Value, "[скрыто]", StringComparison.Ordinal);
                }
            }
        }

        return Text(value, maximumLength);
    }

    internal static string Text(string value, int maximumLength) => Sanitize(Redact(value), maximumLength);

    private static string Redact(string text)
        => SecretPattern().Replace(BearerPattern().Replace(text, "Bearer [скрыто]"), "$1=[скрыто]");

    [GeneratedRegex(@"\b(password|passwd|token|secret|api[_-]?key|authorization)\s*[:=]\s*.+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();
}
