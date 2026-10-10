namespace AzurPilot.AndroidAcceptance;

internal sealed record ScreenshotAcceptanceOutput<TEvidence>(
    string Mode,
    bool IsProven,
    string? FailureCode,
    bool ReportArtifactRequested,
    bool ReportArtifactWritten,
    string? ArtifactFailureCode,
    TEvidence Evidence)
{
    // Результат capture/scan и сохранение явно запрошенного отчёта имеют разные коды выхода.
    internal int ExitCode => !IsProven
        ? AndroidAcceptanceExitCode.NotProven
        : ReportArtifactRequested && !ReportArtifactWritten
            ? AndroidAcceptanceExitCode.ReportArtifactNotWritten
            : AndroidAcceptanceExitCode.Success;
}
