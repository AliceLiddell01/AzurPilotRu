using System.Text.RegularExpressions;

namespace AzurPilot.TuiHarness;

// Только диагностика SGR; разбор состояния терминала целиком принадлежит xterm.js.
internal static partial class VtDiagnostics
{
    internal static string[] Summarize(string vt)
    {
        return [.. Sgr().Matches(vt).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(128)];
    }

    [GeneratedRegex("\u001b\\[([0-9;:]*)m", RegexOptions.CultureInvariant)]
    private static partial Regex Sgr();
}
