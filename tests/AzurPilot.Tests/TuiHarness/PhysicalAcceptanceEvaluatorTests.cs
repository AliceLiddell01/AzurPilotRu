using AzurPilot.TuiHarness.Physical;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class PhysicalAcceptanceEvaluatorTests
{
    [Fact]
    public void Evaluate_TechnicalEvidencePassesWithUnknownCaptureModeAndUnreviewedImages()
    {
        PhysicalAcceptanceResult result = PhysicalAcceptanceEvaluator.Evaluate(
            CompleteEvidence with
            {
                CaptureProvenance = "NOT_TESTED",
                ColorComparison = "NOT_TESTED",
                StateComparability = "NOT_TESTED",
                VisualAssessment = "NOT_TESTED",
            }, interactive: false);

        Assert.Equal("PASS", result.Status);
        Assert.Equal("NOT_TESTED", result.Gates["physicalInteraction"]);
        Assert.Equal("NOT_TESTED", result.Gates["captureProvenance"]);
        Assert.Equal("NOT_TESTED", result.Gates["colorComparison"]);
        Assert.Equal("NOT_TESTED", result.Gates["stateComparability"]);
        Assert.Equal("NOT_TESTED", result.Gates["visualAssessment"]);
    }

    [Theory]
    [InlineData("PngIntegrity", "FAIL", "FAIL")]
    [InlineData("WindowIdentity", "BLOCKED", "BLOCKED")]
    [InlineData("AppIdentity", "NOT_TESTED", "BLOCKED")]
    [InlineData("ContentGeometry", "BLOCKED", "BLOCKED")]
    [InlineData("RgbMeasurement", "NOT_TESTED", "BLOCKED")]
    [InlineData("SeparateConPtyEvidence", "BLOCKED", "BLOCKED")]
    [InlineData("Cleanup", "BLOCKED", "BLOCKED")]
    [InlineData("UserConfigurationUnchanged", "FAIL", "FAIL")]
    public void Evaluate_RequiredTechnicalFailureOrMissingEvidenceCannotPass(string field, string value, string expected)
    {
        PhysicalAcceptanceEvidence evidence = field switch
        {
            nameof(PhysicalAcceptanceEvidence.WindowIdentity) => CompleteEvidence with { WindowIdentity = value },
            nameof(PhysicalAcceptanceEvidence.AppIdentity) => CompleteEvidence with { AppIdentity = value },
            nameof(PhysicalAcceptanceEvidence.PngIntegrity) => CompleteEvidence with { PngIntegrity = value },
            nameof(PhysicalAcceptanceEvidence.ContentGeometry) => CompleteEvidence with { ContentGeometry = value },
            nameof(PhysicalAcceptanceEvidence.RgbMeasurement) => CompleteEvidence with { RgbMeasurement = value },
            nameof(PhysicalAcceptanceEvidence.SeparateConPtyEvidence) => CompleteEvidence with { SeparateConPtyEvidence = value },
            nameof(PhysicalAcceptanceEvidence.Cleanup) => CompleteEvidence with { Cleanup = value },
            nameof(PhysicalAcceptanceEvidence.UserConfigurationUnchanged) => CompleteEvidence with { UserConfigurationUnchanged = value },
            _ => throw new InvalidOperationException("Неизвестное обязательное техническое свидетельство."),
        };

        PhysicalAcceptanceResult result = PhysicalAcceptanceEvaluator.Evaluate(evidence, interactive: false);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "FAIL" ? 1 : 0, result.FailedGates.Count);
    }

    [Fact]
    public void Evaluate_ReportsPaletteDifferenceWithoutReclassifyingTechnicalCaptureAsFailed()
    {
        PhysicalAcceptanceResult result = PhysicalAcceptanceEvaluator.Evaluate(
            CompleteEvidence with { ColorComparison = "DIFFERENT" }, interactive: false);

        Assert.Equal("PASS", result.Status);
        Assert.Equal("DIFFERENT", result.Gates["colorComparison"]);
        Assert.Empty(result.FailedGates);
    }

    [Fact]
    public void Evaluate_RequestedF2EscapeNeedsConfirmedTransition()
    {
        PhysicalAcceptanceResult result = PhysicalAcceptanceEvaluator.Evaluate(
            CompleteEvidence with { PhysicalInteraction = "BLOCKED" }, interactive: true);

        Assert.Equal("BLOCKED", result.Status);
        Assert.Contains("physicalInteraction", result.BlockedGates);
    }

    private static readonly PhysicalAcceptanceEvidence CompleteEvidence = new(
        WindowIdentity: "PASS",
        AppIdentity: "PASS",
        PngIntegrity: "PASS",
        ContentGeometry: "PASS",
        RgbMeasurement: "PASS",
        SeparateConPtyEvidence: "PASS",
        PhysicalInteraction: "PASS",
        Cleanup: "PASS",
        UserConfigurationUnchanged: "PASS",
        CaptureProvenance: "PASS",
        ColorComparison: "MATCH",
        StateComparability: "PASS",
        VisualAssessment: "PASS",
        ConPtySessionCleanup: "PASS");

    [Theory]
    [InlineData("PASS", "PASS")]
    [InlineData("BLOCKED", "BLOCKED")]
    [InlineData("NOT_TESTED", "BLOCKED")]
    [InlineData("FAIL", "FAIL")]
    public void Evaluate_SeparateConPtyCleanupIsRequired(string cleanup, string expected)
    {
        PhysicalAcceptanceResult result = PhysicalAcceptanceEvaluator.Evaluate(
            CompleteEvidence with { ConPtySessionCleanup = cleanup }, interactive: false);

        Assert.Equal(expected, result.Status);
        Assert.Equal(cleanup, result.Gates["conptySessionCleanup"]);
        Assert.Equal("PASS", result.Gates["cleanup"]);
        Assert.Equal("PASS", result.Gates["separateConPtyEvidence"]);
    }
}
