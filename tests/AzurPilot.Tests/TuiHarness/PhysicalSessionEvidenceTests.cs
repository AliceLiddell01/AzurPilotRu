using System.Text.Json;
using AzurPilot.TuiHarness;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class PhysicalSessionEvidenceTests
{
    private const string ExeSha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string AppDllSha256 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void ConPtyIdentity_PassesOnlyWhenExeAndDllMatchPhysicalRun()
    {
        object data = new
        {
            identity = new
            {
                exeSha256 = ExeSha256.ToLowerInvariant(),
                assemblySha256 = AppDllSha256,
            },
        };

        PhysicalSession.ConPtyBinaryIdentityEvidence evidence = PhysicalSession.AssessConPtyBinaryIdentity(
            data, ExeSha256, AppDllSha256);

        Assert.Equal("PASS", evidence.Status);
        Assert.Equal(ExeSha256.ToLowerInvariant(), evidence.ExeSha256);
        Assert.Equal(AppDllSha256, evidence.AppDllSha256);
    }

    [Fact]
    public void ConPtyIdentity_BlocksWhenExeOrDllDiffersOrIdentityIsMissing()
    {
        object differentExe = new
        {
            identity = new
            {
                exeSha256 = AppDllSha256,
                assemblySha256 = AppDllSha256,
            },
        };
        object differentDll = new
        {
            identity = new
            {
                exeSha256 = ExeSha256,
                assemblySha256 = ExeSha256,
            },
        };

        PhysicalSession.ConPtyBinaryIdentityEvidence exeMismatch = PhysicalSession.AssessConPtyBinaryIdentity(
            differentExe, ExeSha256, AppDllSha256);
        PhysicalSession.ConPtyBinaryIdentityEvidence mismatch = PhysicalSession.AssessConPtyBinaryIdentity(
            differentDll, ExeSha256, AppDllSha256);
        PhysicalSession.ConPtyBinaryIdentityEvidence missing = PhysicalSession.AssessConPtyBinaryIdentity(
            new { directory = "session" }, ExeSha256, AppDllSha256);

        Assert.Equal("BLOCKED", exeMismatch.Status);
        Assert.Equal("BLOCKED", mismatch.Status);
        Assert.Equal(ExeSha256, mismatch.AppDllSha256);
        Assert.Equal("BLOCKED", missing.Status);
    }

    [Fact]
    public async Task ConPtySessionCleanup_RecordsCloseFailureWithoutThrowing()
    {
        PhysicalSession.ConPtySessionCleanupEvidence evidence =
            await PhysicalSession.CaptureConPtySessionCleanupEvidenceAsync(
                () => Task.FromException<HarnessResponse>(new HarnessBlockedException("Владелец не ответил.")));

        Assert.Equal("BLOCKED", evidence.Status);
        Assert.Equal("Владелец не ответил.", evidence.Reason);
    }

    [Fact]
    public void CleanupMarkerAbsence_RecordsEnumerationFailureAsNotTested()
    {
        PhysicalSession.MarkerAbsenceEvidence evidence = PhysicalSession.CheckMarkerAbsence(
            () => throw new HarnessBlockedException("EnumWindows завершился ошибкой."));

        Assert.False(evidence.MarkerAbsent);
        Assert.Equal("NOT_TESTED", evidence.Status);
        Assert.Equal("EnumWindows завершился ошибкой.", evidence.Reason);
    }

    [Fact]
    public void ForegroundSample_RequiresTheMeasuredBlankCellBackground()
    {
        using JsonDocument blank = JsonDocument.Parse("""{"mode":"indexed","value":0}""");
        using JsonDocument same = JsonDocument.Parse("""{"mode":"indexed","value":0}""");
        using JsonDocument different = JsonDocument.Parse("""{"mode":"indexed","value":4}""");
        using JsonDocument equivalentResolvedColor = JsonDocument.Parse("""{"mode":"default","value":0}""");

        Assert.True(PhysicalSession.MatchesMeasuredBackground(same.RootElement, blank.RootElement, 0, 0));
        Assert.False(PhysicalSession.MatchesMeasuredBackground(different.RootElement, blank.RootElement, 4, 0));
        Assert.True(PhysicalSession.MatchesMeasuredBackground(equivalentResolvedColor.RootElement,
            blank.RootElement, 0, 0));
    }

    [Fact]
    public void NumericJsonProperties_ReturnFallbackForWrongValueKindsAndNonObjects()
    {
        using JsonDocument wrongKinds = JsonDocument.Parse(
            """{"integer":null,"long":"7","optionalInteger":false,"optionalDouble":"1.5"}""");
        using JsonDocument nonObject = JsonDocument.Parse("null");

        Assert.Equal(-1, PhysicalSession.IntProperty(wrongKinds.RootElement, "integer"));
        Assert.Equal(-1, PhysicalSession.Int64Property(wrongKinds.RootElement, "long"));
        Assert.Null(PhysicalSession.OptionalInt(wrongKinds.RootElement, "optionalInteger"));
        Assert.Null(PhysicalSession.OptionalDouble(wrongKinds.RootElement, "optionalDouble"));
        Assert.Equal(-1, PhysicalSession.IntProperty(nonObject.RootElement, "integer"));
        Assert.Equal(-1, PhysicalSession.Int64Property(nonObject.RootElement, "long"));
        Assert.Null(PhysicalSession.OptionalInt(nonObject.RootElement, "optionalInteger"));
        Assert.Null(PhysicalSession.OptionalDouble(nonObject.RootElement, "optionalDouble"));
    }
}
