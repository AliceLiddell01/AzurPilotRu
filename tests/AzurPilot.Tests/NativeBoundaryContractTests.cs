using AzurPilot.Core;
using Xunit;

namespace AzurPilot.Tests;

public sealed class NativeBoundaryContractTests
{
    [Fact(DisplayName = "Канонический контракт ABI 4 требует подтверждённый imgproc")]
    public void CanonicalAbi4ContractRejectsMissingImgproc()
    {
        NativeBoundaryInfo info = new(
            NativeBoundaryContract.ExpectedAbiVersion,
            new Version(1, 0),
            ["core", "imgcodecs"],
            BuildFlags: 0,
            BuildInfo: string.Empty);

        NativeBoundaryCompatibility compatibility = NativeBoundaryContract.Canonical.Check(info);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains("imgproc", compatibility.Reason, StringComparison.Ordinal);
    }
}
