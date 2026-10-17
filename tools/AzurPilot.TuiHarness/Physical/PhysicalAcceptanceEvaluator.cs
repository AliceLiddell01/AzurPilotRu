namespace AzurPilot.TuiHarness.Physical;

internal sealed record PhysicalAcceptanceEvidence(
    string WindowIdentity,
    string AppIdentity,
    string PngIntegrity,
    string ContentGeometry,
    string RgbMeasurement,
    string SeparateConPtyEvidence,
    string PhysicalInteraction,
    string Cleanup,
    string UserConfigurationUnchanged,
    string CaptureProvenance,
    string ColorComparison,
    string StateComparability,
    string VisualAssessment,
    string ConPtySessionCleanup = "NOT_TESTED");

internal sealed record PhysicalAcceptanceResult(
    string Status,
    IReadOnlyDictionary<string, string> Gates,
    IReadOnlyList<string> BlockedGates,
    IReadOnlyList<string> FailedGates,
    string Message);

/// <summary>Сводит обязательные технические результаты физической контрольной проверки.</summary>
internal static class PhysicalAcceptanceEvaluator
{
    internal static PhysicalAcceptanceResult Evaluate(PhysicalAcceptanceEvidence evidence, bool interactive)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        Dictionary<string, string> gates = new(StringComparer.Ordinal)
        {
            ["windowIdentity"] = Normalize(evidence.WindowIdentity),
            ["appIdentity"] = Normalize(evidence.AppIdentity),
            ["pngIntegrity"] = Normalize(evidence.PngIntegrity),
            ["contentGeometry"] = Normalize(evidence.ContentGeometry),
            ["rgbMeasurement"] = Normalize(evidence.RgbMeasurement),
            ["separateConPtyEvidence"] = Normalize(evidence.SeparateConPtyEvidence),
            ["physicalInteraction"] = interactive ? Normalize(evidence.PhysicalInteraction) : "NOT_TESTED",
            ["cleanup"] = Normalize(evidence.Cleanup),
            ["conptySessionCleanup"] = Normalize(evidence.ConPtySessionCleanup),
            ["userConfigurationUnchanged"] = Normalize(evidence.UserConfigurationUnchanged),
            ["captureProvenance"] = Normalize(evidence.CaptureProvenance),
            ["colorComparison"] = NormalizeComparison(evidence.ColorComparison),
            ["stateComparability"] = Normalize(evidence.StateComparability),
            ["visualAssessment"] = Normalize(evidence.VisualAssessment),
        };

        string[] required = ["windowIdentity", "appIdentity", "pngIntegrity", "contentGeometry", "rgbMeasurement",
            "separateConPtyEvidence", "cleanup", "conptySessionCleanup", "userConfigurationUnchanged"];
        if (interactive)
        {
            required = [.. required, "physicalInteraction"];
        }

        string[] blocked = [.. required.Where(name => gates[name] is "BLOCKED" or "NOT_TESTED")];
        string[] failed = [.. required.Where(name => gates[name] == "FAIL")];
        string status = failed.Length > 0 ? "FAIL" : blocked.Length > 0 ? "BLOCKED" : "PASS";
        string message = status switch
        {
            "PASS" => "Обязательные технические условия физической проверки выполнены.",
            "FAIL" => "Физическая проверка обнаружила нарушение обязательного технического условия: "
                + string.Join(", ", failed) + ".",
            _ => "Физическая проверка заблокирована без подтверждения обязательных условий: "
                + string.Join(", ", blocked) + ".",
        };

        return new(status, gates, blocked, failed, message);
    }

    private static string Normalize(string value) => value is "PASS" or "FAIL" or "BLOCKED" or "NOT_TESTED"
        ? value : "NOT_TESTED";

    private static string NormalizeComparison(string value) => value switch
    {
        "MATCH" or "DIFFERENT" or "PASS" or "FAIL" or "BLOCKED" or "NOT_TESTED" => value,
        _ => "NOT_TESTED",
    };
}
