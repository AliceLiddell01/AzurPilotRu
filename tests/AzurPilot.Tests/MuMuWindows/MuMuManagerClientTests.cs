using System.Globalization;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства поведения клиента control surface: точные аргументы запуска, bounded вывод, отказ на
/// усечённом выводе, отмена и проброс отказов границы процесса без подмены их общим отказом.
/// </summary>
/// <remarks>
/// Проверки идут через подменяемую границу процесса: ни установленная MuMu, ни реальный control utility
/// не участвуют.
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuManagerClientTests
{
    private static readonly MuMuInstanceId FirstInstance = MuMuInstanceId.FromIndex("1");
    private static readonly CancellationToken NoCancellation = CancellationToken.None;

    [Fact(DisplayName = "Версия запрашивается ровно у сконфигурированного control utility")]
    public async Task VersionRequestUsesExactExecutableAndArguments()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.VersionResponse);

        MuMuControlSurface surface = CreateSurface();
        MuMuManagerClient client = new(runner, surface);

        ApplicationResult<string> result = await client.GetVersionAsync(NoCancellation);

        Assert.True(result.IsSuccess);
        Assert.Equal("6.8.0.0", result.Value);

        RecordedProcessRequest recorded = Assert.Single(runner.Requests);

        Assert.Equal(surface.ExecutablePath, recorded.Request.ExecutablePath);
        AssertArguments(["version"], recorded.Request.Arguments);
        Assert.Equal(MuMuManagerClient.DefaultCommandTimeout, recorded.Request.Timeout);

        // Рабочий каталог не задаётся: проверенная форма control surface работает без него.
        Assert.Null(recorded.Request.WorkingDirectory);
    }

    [Fact(DisplayName = "Наблюдение состояния запрашивается по конкретному номеру экземпляра")]
    public async Task InstanceQueryUsesExactIndex()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.StoppedInstanceResponse);

        MuMuManagerClient client = new(runner, CreateSurface());

        ApplicationResult<MuMuInstanceQueryResult> result =
            await client.QueryInstanceAsync(FirstInstance, NoCancellation);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Stopped, result.Value!.Instance!.State);

        AssertArguments(["info", "--vmindex", "1"], Assert.Single(runner.Requests).Request.Arguments);
    }

    [Fact(DisplayName = "Операция изменения состояния выполняется ровно один раз и без повторных попыток")]
    public async Task ControlIsExecutedOnce()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.AcceptedControlResponse);

        MuMuManagerClient client = new(runner, CreateSurface());

        ApplicationResult<MuMuControlOutcome> result = await client.ExecuteControlAsync(
            FirstInstance,
            MuMuControlCommand.Launch,
            NoCancellation);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsAccepted);

        AssertArguments(["control", "--vmindex", "1", "launch"], Assert.Single(runner.Requests).Request.Arguments);
    }

    [Fact(DisplayName = "Усечённый вывод не разбирается: операция завершается отказом")]
    public async Task TruncatedOutputFailsClosed()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueOutcome(0, MuMuObservedPayloads.VersionResponse, standardOutputTruncated: true);

        MuMuManagerClient client = new(runner, CreateSurface());

        ApplicationResult<string> result = await client.GetVersionAsync(NoCancellation);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.Equal(MuMuFailureReasons.OutputTruncated, failure.Details![MuMuFailureDetailKeys.Reason]);

        // Полный вывод в details не попадает: сообщается только длина захваченного.
        Assert.Equal(
            MuMuObservedPayloads.VersionResponse.Length.ToString(CultureInfo.InvariantCulture),
            failure.Details![MuMuFailureDetailKeys.StandardOutputCharacters]);
    }

    [Fact(DisplayName = "Дедлайн процесса control utility пробрасывается как отказ lifecycle-таймаута")]
    public async Task ProcessDeadlineFailureIsPropagated()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueFailure(MuMuPlatformFailureMapper.ForProcessTimeout(
            CreateSurface().ExecutablePath,
            TimeSpan.FromSeconds(1),
            0,
            0));

        MuMuManagerClient client = new(runner, CreateSurface());

        ApplicationResult<MuMuInstanceEnumeration> result = await client.EnumerateInstancesAsync(NoCancellation);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.True(failure.IsRetryable);
        Assert.Equal(MuMuFailureReasons.ProcessTimeout, failure.Details![MuMuFailureDetailKeys.Reason]);
    }

    [Fact(DisplayName = "Отмена операции не превращается в отказ control surface")]
    public async Task CancellationIsPropagatedUnchanged()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueFailure(MuMuPlatformFailureMapper.ForCancellation());

        MuMuManagerClient client = new(runner, CreateSurface());

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        ApplicationResult<MuMuControlOutcome> result = await client.ExecuteControlAsync(
            FirstInstance,
            MuMuControlCommand.Shutdown,
            cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);

        // Токен отмены вызывающей стороны доходит до границы процесса без подмены.
        Assert.Equal(cancellation.Token, Assert.Single(runner.Requests).CancellationToken);
    }

    [Fact(DisplayName = "Отказ провайдера на несуществующий номер не считается успехом операции")]
    public async Task ProviderRejectionIsNotSuccess()
    {
        FakeMuMuProcessRunner runner = new();
        runner.EnqueueOutcome(
            MuMuObservedPayloads.IndexNotFoundExitCode,
            MuMuObservedPayloads.IndexNotFoundResponse);

        MuMuManagerClient client = new(runner, CreateSurface());

        ApplicationResult<MuMuControlOutcome> result = await client.ExecuteControlAsync(
            MuMuInstanceId.FromIndex("5"),
            MuMuControlCommand.Shutdown,
            NoCancellation);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAccepted);
        Assert.True(result.Value!.IsIndexNotFound);
        Assert.Equal(MuMuProviderCodes.PlayerIndexNotFound, result.Value!.ProviderError!.Code);
    }

    [Theory(DisplayName = "Неположительный дедлайн команды — ошибка программирования")]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeoutIsProgrammingError(int seconds)
    {
        FakeMuMuProcessRunner runner = new();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MuMuManagerClient(runner, CreateSurface(), TimeSpan.FromSeconds(seconds)));
    }

    [Fact(DisplayName = "Пустой путь к control surface — ошибка программирования")]
    public void EmptyExecutablePathIsProgrammingError()
    {
        FakeMuMuProcessRunner runner = new();

        _ = Assert.Throws<ArgumentException>(
            () => new MuMuManagerClient(runner, new MuMuControlSurface { ExecutablePath = " " }));
    }

    private static MuMuControlSurface CreateSurface()
        => new() { ExecutablePath = MuMuWindowsTestPaths.Create("nx_main", "MuMuManager.exe") };

    private static void AssertArguments(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }
}
