using AzurPilot.AndroidAcceptance;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;
using Xunit;

namespace AzurPilot.Tests.AndroidAcceptance;

/// <summary>Контракты parser и command audit тестового acceptance-инструмента.</summary>
public sealed class AndroidAcceptanceModeTests
{
    private static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);
    private static readonly AndroidEndpoint OtherEndpoint = new("127.0.0.1", 16417);
    private static readonly MuMuInstanceId Instance = MuMuInstanceId.FromIndex("1");
    private static readonly AndroidPackageId Package = AzurLaneGameStateService.Package;

    [Fact(DisplayName = "Подготовка transport требует отдельный exact instance")]
    public void PreparationParserAcceptsOnlyExplicitExactIdentity()
    {
        AndroidTransportPreparationArguments accepted = AndroidTransportPreparationArgumentParser.Parse(
            ["--prepare-transport", "--instance", "mumu:1"]);

        Assert.NotNull(accepted.Options);
        Assert.Equal("mumu:1", accepted.Options!.InstanceValue);
        Assert.Null(AndroidTransportPreparationArgumentParser.Parse(["--prepare-transport"]).Options);
        Assert.Null(AndroidTransportPreparationArgumentParser.Parse(
            ["--prepare-transport", "--instance", "mumu:auto"]).Options);
        Assert.Null(AndroidTransportPreparationArgumentParser.Parse(
            ["--prepare-transport", "--instance", "mumu:01"]).Options);
        Assert.Null(AndroidTransportPreparationArgumentParser.Parse(
            ["--prepare-transport", "--instance", "mumu:1", "--captures", "3"]).Options);
    }

    [Fact(DisplayName = "Screenshot CLI не принимает transport preparation как live capture option")]
    public void ScreenshotParserRejectsPreparationOption()
    {
        ScreenshotAcceptanceArguments result = ScreenshotAcceptanceArgumentParser.Parse(
            ["--screenshot-live", "--instance", "mumu:1", "--prepare-transport"]);

        Assert.Null(result.Options);
    }

    [Fact(DisplayName = "Preparation audit принимает только один connect и exact readiness команды")]
    public void PreparationAuditAllowsOneExactConnectAndReadinessCommands()
    {
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.TransportPreparation);
        string adb = TestExecutablePath("bundled-adb.exe");
        string control = TestExecutablePath("control.exe");
        Record(audit, adb, AdbCommandBuilder.BuildVersionArguments());
        Record(audit, adb, AdbCommandBuilder.BuildConnectArguments(Endpoint));
        Record(audit, adb, AdbCommandBuilder.BuildGetStateArguments(Endpoint));
        Record(audit, adb, AdbCommandBuilder.BuildBootCompletedArguments(Endpoint));
        Record(audit, adb, AdbCommandBuilder.BuildAndroidReleaseArguments(Endpoint));
        Record(audit, adb, AdbCommandBuilder.BuildSdkLevelArguments(Endpoint));

        Assert.Null(audit.FindViolation(control, adb, Instance, Endpoint, Package));
        Assert.Equal(1, audit.CountConnectAttempts(adb, Endpoint));
    }

    [Fact(DisplayName = "Preparation audit отвергает второй connect и другой endpoint")]
    public void PreparationAuditRejectsRepeatedOrForeignConnect()
    {
        string adb = TestExecutablePath("bundled-adb.exe");
        string control = TestExecutablePath("control.exe");

        AndroidCommandAudit repeated = new(AndroidCommandAuditMode.TransportPreparation);
        Record(repeated, adb, AdbCommandBuilder.BuildConnectArguments(Endpoint));
        Record(repeated, adb, AdbCommandBuilder.BuildConnectArguments(Endpoint));
        Assert.NotNull(repeated.FindViolation(control, adb, Instance, Endpoint, Package));

        AndroidCommandAudit foreign = new(AndroidCommandAuditMode.TransportPreparation);
        Record(foreign, adb, AdbCommandBuilder.BuildConnectArguments(OtherEndpoint));
        Assert.NotNull(foreign.FindViolation(control, adb, Instance, Endpoint, Package));
    }

    [Fact(DisplayName = "Screenshot audit допускает exact screencap, но запрещает connect")]
    public void ScreenshotAuditAllowsOnlyExactCaptureAndRejectsConnect()
    {
        string adb = TestExecutablePath("bundled-adb.exe");
        string control = TestExecutablePath("control.exe");

        AndroidCommandAudit capture = new(AndroidCommandAuditMode.ScreenshotReadOnly);
        Record(capture, adb, AdbCommandBuilder.BuildScreenshotArguments(Endpoint));
        Assert.Null(capture.FindViolation(control, adb, Instance, Endpoint, Package));

        AndroidCommandAudit connect = new(AndroidCommandAuditMode.ScreenshotReadOnly);
        Record(connect, adb, AdbCommandBuilder.BuildConnectArguments(Endpoint));
        Assert.NotNull(connect.FindViolation(control, adb, Instance, Endpoint, Package));
    }

    [Theory(DisplayName = "Preparation audit отвергает capture, game mutations и посторонний executable")]
    [InlineData("screenshot")]
    [InlineData("game")]
    [InlineData("executable")]
    public void PreparationAuditRejectsCommandsOutsideItsSurface(string kind)
    {
        AndroidCommandAudit audit = new(AndroidCommandAuditMode.TransportPreparation);
        string adb = TestExecutablePath("bundled-adb.exe");
        string control = TestExecutablePath("control.exe");
        switch (kind)
        {
            case "screenshot":
                Record(audit, adb, AdbCommandBuilder.BuildScreenshotArguments(Endpoint));
                break;
            case "game":
                Record(audit, adb, AdbCommandBuilder.BuildForceStopGameArguments(Endpoint, Package));
                break;
            case "executable":
                Record(audit, TestExecutablePath("other-adb.exe"), AdbCommandBuilder.BuildGetStateArguments(Endpoint));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Assert.NotNull(audit.FindViolation(control, adb, Instance, Endpoint, Package));
    }

    private static void Record(AndroidCommandAudit audit, string executable, IReadOnlyList<string> arguments)
    {
        AuditEntry entry = audit.Begin(new WindowsProcessRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(1),
        });
        audit.Complete(entry, null);
    }

    private static string TestExecutablePath(string fileName)
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "azurpilot-acceptance-tests", fileName));
}
