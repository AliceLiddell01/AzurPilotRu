namespace AzurPilot.AndroidAcceptance;

internal sealed record ScreenshotAcceptanceOutput<TEvidence>(
    string Mode,
    bool IsProven,
    string? FailureCode,
    bool ReportArtifactRequested,
    bool ReportArtifactWritten,
    TEvidence Evidence);
