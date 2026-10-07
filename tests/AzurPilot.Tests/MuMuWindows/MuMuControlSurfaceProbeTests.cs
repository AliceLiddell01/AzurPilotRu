using AzurPilot.Core.Failures;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства capability discovery: поддержка control surface подтверждается фактической формой
/// ответа, а не номером версии, и проверка выполняется только чтением.
/// </summary>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuControlSurfaceProbeTests
{
    [Fact(DisplayName = "Распознанные version и перечисление подтверждают поддержку control surface")]
    public async Task RecognizedSurfacesConfirmSupport()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.VersionResponse);
        runner.EnqueueOutcome(0, MuMuObservedPayloads.SingleStoppedEnumerationResponse);

        MuMuControlSurfaceProbe probe = new(new MuMuManagerClient(runner, CreateSurface()));

        ApplicationResult<MuMuCapabilityReport> result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        MuMuCapabilityReport report = result.Value!;

        Assert.True(report.IsControlSurfaceSupported);
        Assert.True(report.IsVersionSurfaceRecognized);
        Assert.True(report.IsInstanceEnumerationRecognized);
        Assert.Equal("6.8.0.0", report.ReportedVersion);
        Assert.Equal(1, report.EnumeratedInstanceCount);
        Assert.Null(report.UnsupportedReason);
    }

    [Fact(DisplayName = "Проверка поддержки не выполняет mutation ни над одним экземпляром")]
    public async Task ProbePerformsReadOnlyRequestsOnly()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.VersionResponse);
        runner.EnqueueOutcome(0, MuMuObservedPayloads.SingleStoppedEnumerationResponse);

        MuMuControlSurfaceProbe probe = new(new MuMuManagerClient(runner, CreateSurface()));

        _ = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("version", runner.Requests[0].Request.Arguments[0]);
        Assert.Equal("info", runner.Requests[1].Request.Arguments[0]);

        Assert.DoesNotContain(
            runner.Requests,
            recorded => recorded.Request.Arguments.Contains(MuMuManagerCommandBuilder.ControlSubcommand));
    }

    [Fact(DisplayName = "Нераспознанный ответ на version делает форму неподдерживаемой")]
    public async Task UnrecognizedVersionMakesSurfaceUnsupported()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, "{ \"player_version\": \"6.8.0.0\" }");

        MuMuControlSurfaceProbe probe = new(new MuMuManagerClient(runner, CreateSurface()));

        ApplicationResult<MuMuCapabilityReport> result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        MuMuCapabilityReport report = result.Value!;

        Assert.False(report.IsControlSurfaceSupported);
        Assert.False(report.IsVersionSurfaceRecognized);
        Assert.Null(report.ReportedVersion);
        Assert.Equal(
            ApplicationFailure.MuMuControlSurfaceUnsupported,
            report.UnsupportedReason!.Code);

        // Перечисление не запрашивается: поддержка уже не подтверждена.
        _ = Assert.Single(runner.Requests);
    }

    [Fact(DisplayName = "Нераспознанный ответ на перечисление делает форму неподдерживаемой")]
    public async Task UnrecognizedEnumerationMakesSurfaceUnsupported()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.VersionResponse);
        runner.EnqueueOutcome(0, "{ \"instances\": [] }");

        MuMuControlSurfaceProbe probe = new(new MuMuManagerClient(runner, CreateSurface()));

        ApplicationResult<MuMuCapabilityReport> result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        MuMuCapabilityReport report = result.Value!;

        Assert.False(report.IsControlSurfaceSupported);
        Assert.True(report.IsVersionSurfaceRecognized);
        Assert.False(report.IsInstanceEnumerationRecognized);
        Assert.Equal("6.8.0.0", report.ReportedVersion);
        Assert.Null(report.EnumeratedInstanceCount);
        Assert.Equal(
            ApplicationFailure.MuMuControlSurfaceUnsupported,
            report.UnsupportedReason!.Code);
    }

    [Fact(DisplayName = "Отмена проверки не превращается в «не поддерживается»")]
    public async Task CancelledProbeIsFailureNotUnsupported()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueFailure(MuMuPlatformFailureMapper.ForCancellation());

        MuMuControlSurfaceProbe probe = new(new MuMuManagerClient(runner, CreateSurface()));

        ApplicationResult<MuMuCapabilityReport> result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
    }

    private static MuMuControlSurface CreateSurface()
        => new() { ExecutablePath = MuMuWindowsTestPaths.Create("nx_main", "MuMuManager.exe") };
}
