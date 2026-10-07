using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства разрешения точного ADB endpoint выбранного Android-экземпляра MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Проверки идут через настоящий <see cref="AndroidEndpointResolver"/> поверх переиспользуемой подмены
/// общей границы запуска процесса <see cref="FakeWindowsProcessRunner"/>: второй fake runner не
/// заводится, а форма команды и разбор ответа остаются у своих владельцев
/// <see cref="MuMuManagerCommandBuilder"/> и <c>MuMuManagerResponseParser</c>.
/// </para>
/// <para>
/// Endpoint не подставляется по умолчанию и не вычисляется по номеру экземпляра: если сведения не
/// содержат точного адреса, разрешение сообщает отказ с machine-stable причиной.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidEndpointResolutionTests
{
    private static readonly MuMuInstanceId FirstInstance = MuMuInstanceId.FromIndex("1");

    private static readonly MuMuInstanceId SecondInstance = MuMuInstanceId.FromIndex("2");

    [Fact(DisplayName = "Endpoint берётся из сведений ровно запрошенной identity экземпляра")]
    public async Task EndpointComesFromRequestedInstanceInfo()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, InstanceResponse("1", "127.0.0.1", 16416));
        AndroidEndpointResolver resolver = new(runner);

        ApplicationResult<AndroidEndpoint> result =
            await resolver.ResolveAsync(AndroidTestContext.Installation, FirstInstance, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new AndroidEndpoint("127.0.0.1", 16416), result.Value!);
        Assert.Equal("127.0.0.1:16416", result.Value!.ToString());

        RecordedProcessRequest request = Assert.Single(runner.Requests);

        Assert.Equal(AndroidTestContext.Installation.ControlExecutablePath, request.Request.ExecutablePath);
        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(FirstInstance).ToArray(),
            request.Request.Arguments.ToArray());
    }

    [Fact(DisplayName = "Каждый экземпляр разрешает свой endpoint, а не endpoint соседа")]
    public async Task EachInstanceResolvesItsOwnEndpoint()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, InstanceResponse("1", "127.0.0.1", 16416));
        runner.EnqueueOutcome(0, InstanceResponse("2", "127.0.0.1", 20312));
        AndroidEndpointResolver resolver = new(runner);

        ApplicationResult<AndroidEndpoint> first =
            await resolver.ResolveAsync(AndroidTestContext.Installation, FirstInstance, CancellationToken.None);
        ApplicationResult<AndroidEndpoint> second =
            await resolver.ResolveAsync(AndroidTestContext.Installation, SecondInstance, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(16416, first.Value!.Port);
        Assert.Equal(20312, second.Value!.Port);
        Assert.NotEqual(first.Value, second.Value);

        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(SecondInstance).ToArray(),
            runner.Requests[1].Request.Arguments.ToArray());
    }

    [Fact(DisplayName = "Несообщённый host не разрешается значением по умолчанию")]
    public async Task MissingHostIsNotSubstituted()
    {
        ApplicationResult<AndroidEndpoint> missing = await ResolveAsync(InstanceResponse("1", null, 16416));
        ApplicationResult<AndroidEndpoint> blank = await ResolveAsync(InstanceResponse("1", "   ", 16416));

        AssertUnavailable(missing, AndroidDetailValues.HostMissing);
        AssertUnavailable(blank, AndroidDetailValues.HostMissing);
    }

    [Fact(DisplayName = "Host в непригодной форме не разрешается")]
    public async Task InvalidHostIsNotResolved()
    {
        ApplicationResult<AndroidEndpoint> withPort = await ResolveAsync(InstanceResponse("1", "127.0.0.1:5555", 16416));
        ApplicationResult<AndroidEndpoint> withSpace = await ResolveAsync(InstanceResponse("1", "bad host", 16416));

        AssertUnavailable(withPort, AndroidDetailValues.HostInvalid);
        AssertUnavailable(withSpace, AndroidDetailValues.HostInvalid);
    }

    [Fact(DisplayName = "Несообщённый порт не разрешается значением по умолчанию")]
    public async Task MissingPortIsNotSubstituted()
    {
        ApplicationResult<AndroidEndpoint> result = await ResolveAsync(InstanceResponse("1", "127.0.0.1", null));

        AssertUnavailable(result, AndroidDetailValues.PortMissing);
    }

    [Fact(DisplayName = "Порт вне диапазона endpoint-а не разрешается")]
    public async Task OutOfRangePortIsNotResolved()
    {
        ApplicationResult<AndroidEndpoint> tooSmall =
            await ResolveAsync(InstanceResponse("1", "127.0.0.1", AndroidEndpoint.MinPort - 1));
        ApplicationResult<AndroidEndpoint> tooLarge =
            await ResolveAsync(InstanceResponse("1", "127.0.0.1", AndroidEndpoint.MaxPort + 1));

        AssertUnavailable(tooSmall, AndroidDetailValues.PortOutOfRange);
        AssertUnavailable(tooLarge, AndroidDetailValues.PortOutOfRange);
    }

    [Fact(DisplayName = "Нераспознанный ответ control surface не разрешает endpoint")]
    public async Task UnrecognizedResponseIsNotResolved()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(1, "not-json-at-all");
        AndroidEndpointResolver resolver = new(runner);

        ApplicationResult<AndroidEndpoint> result =
            await resolver.ResolveAsync(AndroidTestContext.Installation, FirstInstance, CancellationToken.None);

        AssertUnavailable(result, AndroidDetailValues.ResponseUnrecognized);

        // Повторов нет: разрешение выполняет ровно один запрос сведений.
        _ = Assert.Single(runner.Requests);
    }

    [Fact(DisplayName = "Отмена запроса сведений пробрасывается, а не выдаётся за состояние endpoint-а")]
    public async Task CancelledQueryIsPropagatedUnchanged()
    {
        ApplicationResult<AndroidEndpoint> result =
            await ResolveFailureAsync(MuMuPlatformFailureMapper.ForCancellation());

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
    }

    [Fact(DisplayName = "Причина отказа чтения сведений сохраняется, а не подменяется нераспознанным ответом")]
    public async Task ProjectedFailureReasonIsPreserved()
    {
        string executable = AndroidTestContext.Installation.ControlExecutablePath;

        ApplicationResult<AndroidEndpoint> timedOut = await ResolveFailureAsync(
            MuMuPlatformFailureMapper.ForProcessTimeout(executable, TimeSpan.FromSeconds(30), 0, 0));
        ApplicationResult<AndroidEndpoint> startFailed = await ResolveFailureAsync(
            MuMuPlatformFailureMapper.ForProcessStartFailure(executable, exception: null));

        AssertUnavailable(timedOut, MuMuFailureReasons.ProcessTimeout);
        AssertUnavailable(startFailed, MuMuFailureReasons.ProcessStartFailed);
    }

    [Fact(DisplayName = "Отказ без сообщённой причины описывается нераспознанным ответом")]
    public async Task FailureWithoutReportedReasonFallsBackToUnrecognizedResponse()
    {
        ApplicationResult<AndroidEndpoint> result = await ResolveFailureAsync(new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Точка входа control surface MuMu не поддерживается.",
        });

        AssertUnavailable(result, AndroidDetailValues.ResponseUnrecognized);
    }

    [Fact(DisplayName = "Разрешение выполняет ровно один запрос и не выполняет mutation")]
    public async Task ResolutionPerformsSingleQueryWithoutMutation()
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, InstanceResponse("1", "127.0.0.1", 16416));
        AndroidEndpointResolver resolver = new(runner);

        ApplicationResult<AndroidEndpoint> result =
            await resolver.ResolveAsync(AndroidTestContext.Installation, FirstInstance, CancellationToken.None);

        Assert.True(result.IsSuccess);

        RecordedProcessRequest request = Assert.Single(runner.Requests);

        Assert.DoesNotContain("control", request.Request.Arguments);
        Assert.DoesNotContain("launch", request.Request.Arguments);
        Assert.DoesNotContain("shutdown", request.Request.Arguments);
        Assert.DoesNotContain("kill-server", request.Request.Arguments);
    }

    private static async Task<ApplicationResult<AndroidEndpoint>> ResolveFailureAsync(ApplicationFailure failure)
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueFailure(failure);
        AndroidEndpointResolver resolver = new(runner);

        ApplicationResult<AndroidEndpoint> result = await resolver.ResolveAsync(
            AndroidTestContext.Installation,
            FirstInstance,
            CancellationToken.None);

        // Отказ чтения не приводит к повторному запросу сведений: разрешение выполняет ровно один запрос.
        _ = Assert.Single(runner.Requests);

        return result;
    }

    private static async Task<ApplicationResult<AndroidEndpoint>> ResolveAsync(string payload)
    {
        FakeWindowsProcessRunner runner = new();
        runner.EnqueueOutcome(0, payload);
        AndroidEndpointResolver resolver = new(runner);

        return await resolver.ResolveAsync(
            AndroidTestContext.Installation,
            FirstInstance,
            CancellationToken.None);
    }

    private static void AssertUnavailable(ApplicationResult<AndroidEndpoint> result, string reason)
    {
        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, failure.Code);
        Assert.Equal(reason, failure.Details![AndroidDetailKeys.Reason]);
        Assert.Equal(FirstInstance.ToString(), failure.Details[AndroidDetailKeys.InstanceId]);
    }

    private static string InstanceResponse(string index, string? host, int? port)
    {
        List<string> properties =
        [
            $"\"index\": \"{index}\"",
            "\"is_process_started\": true",
            "\"is_android_started\": true",
        ];

        if (host is not null)
        {
            properties.Add($"\"adb_host_ip\": \"{host}\"");
        }

        if (port is int portValue)
        {
            properties.Add($"\"adb_port\": {portValue}");
        }

        return "{\n  " + string.Join(",\n  ", properties) + "\n}";
    }
}
