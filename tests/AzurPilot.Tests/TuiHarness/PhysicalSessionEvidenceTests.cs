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
