using System.Globalization;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства разбора ответов control surface: точная форма ответа реальной установки распознаётся,
/// а неизвестная форма приводит к отказу, а не к догадке.
/// </summary>
/// <remarks>
/// Предмет проверок — именно синтаксис ответа провайдера: payload-ы сняты с реальной установки
/// MuMuPlayer 6.8.0, а негативные варианты отличаются от них ровно одним признаком формы.
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuManagerResponseParserTests
{
    private static readonly MuMuInstanceId FirstInstance = MuMuInstanceId.FromIndex("1");

    [Fact(DisplayName = "Ответ подкоманды version реальной установки распознаётся")]
    public void VersionResponseIsRecognized()
    {
        ApplicationResult<string> result =
            MuMuManagerResponseParser.ParseVersion(MuMuObservedPayloads.VersionResponse, 0);

        Assert.True(result.IsSuccess);
        Assert.Equal("6.8.0.0", result.Value);
    }

    [Theory(DisplayName = "Неизвестная форма ответа version не разбирается")]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("not json", 0)]
    [InlineData("[]", 0)]
    [InlineData("\"6.8.0.0\"", 0)]
    [InlineData("{}", 0)]
    [InlineData("{ \"version\": \"\" }", 0)]
    [InlineData("{ \"version\": \"6\" }", 0)]
    [InlineData("{ \"version\": \"6.8.0.0.1\" }", 0)]
    [InlineData("{ \"version\": \"latest\" }", 0)]
    [InlineData("{ \"version\": 6800 }", 0)]
    [InlineData("{ \"version\": \"6.8.0.0\" }", -1)]
    [InlineData("{ \"errcode\": 0, \"errmsg\": \"\", \"version\": \"6.8.0.0\" }", 0)]
    [InlineData("{ \"version\": \"6.8.0.0\", }", 0)]
    [InlineData("// комментарий\n{ \"version\": \"6.8.0.0\" }", 0)]
    public void UnknownVersionShapeFailsClosed(string standardOutput, int exitCode)
    {
        ApplicationResult<string> result = MuMuManagerResponseParser.ParseVersion(standardOutput, exitCode);

        AssertFailure(result, exitCode, null);
    }

    [Fact(DisplayName = "Ответ info для запущенного экземпляра распознаётся как Running")]
    public void RunningInstanceIsRecognized()
    {
        ApplicationResult<MuMuInstanceQueryResult> result = MuMuManagerResponseParser.ParseInstanceQuery(
            FirstInstance,
            MuMuObservedPayloads.RunningInstanceResponse,
            0);

        Assert.True(result.IsSuccess);

        MuMuInstanceQueryResult query = result.Value!;

        Assert.True(query.IsFound);
        Assert.Null(query.ProviderError);

        MuMuInstanceInfo instance = query.Instance!;

        Assert.Equal("1", instance.Id.Index);
        Assert.Equal("Azur lane", instance.DisplayName);
        Assert.Equal("15.0", instance.AndroidVersion);
        Assert.Equal(MuMuLifecycleState.Running, instance.State);
        Assert.Equal(MuMuPlayerStateMap.FinishedPlayerState, instance.RawPlayerState);
        Assert.True(instance.IsProcessStarted);
        Assert.True(instance.IsAndroidStarted);
        Assert.Equal(31176, instance.ProcessId);
        Assert.Equal(16416, instance.AdbPort);
        Assert.Equal(1785242087433799, instance.CreatedTimestamp);
    }

    [Fact(DisplayName = "Ответ info для остановленного экземпляра распознаётся как Stopped без player_state")]
    public void StoppedInstanceIsRecognized()
    {
        ApplicationResult<MuMuInstanceQueryResult> result = MuMuManagerResponseParser.ParseInstanceQuery(
            FirstInstance,
            MuMuObservedPayloads.StoppedInstanceResponse,
            0);

        Assert.True(result.IsSuccess);

        MuMuInstanceInfo instance = result.Value!.Instance!;

        Assert.Equal(MuMuLifecycleState.Stopped, instance.State);
        Assert.Null(instance.RawPlayerState);
        Assert.False(instance.IsProcessStarted);
        Assert.False(instance.IsAndroidStarted);
        Assert.Null(instance.ProcessId);
        Assert.Null(instance.AdbPort);

        // Метка создания экземпляра сохраняется между запусками: именно она, а не PID, доказывает,
        // что наблюдается тот же самый экземпляр.
        Assert.Equal(1785242087433799, instance.CreatedTimestamp);
    }

    [Fact(DisplayName = "Несуществующий номер экземпляра возвращается значением, а не отказом разбора")]
    public void MissingInstanceIsReturnedAsValue()
    {
        ApplicationResult<MuMuInstanceQueryResult> result = MuMuManagerResponseParser.ParseInstanceQuery(
            MuMuInstanceId.FromIndex("5"),
            MuMuObservedPayloads.IndexNotFoundResponse,
            MuMuObservedPayloads.IndexNotFoundExitCode);

        Assert.True(result.IsSuccess);

        MuMuInstanceQueryResult query = result.Value!;

        Assert.False(query.IsFound);
        Assert.True(query.IsIndexNotFound);
        Assert.Null(query.Instance);
        Assert.Equal(MuMuProviderCodes.PlayerIndexNotFound, query.ProviderError!.Code);
        Assert.Equal("player index not found", query.ProviderError.Message);
    }

    [Theory(DisplayName = "Неизвестная форма ответа info не разбирается")]
    [InlineData("{ \"index\": \"1\", \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": \"true\", \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": \"01\", \"is_process_started\": true, \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": \"1.0\", \"is_process_started\": true, \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": 1, \"is_process_started\": true, \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": \"2\", \"is_process_started\": true, \"is_android_started\": true }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true, \"is_android_started\": true, \"player_state\": null }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true, \"is_android_started\": true, \"player_state\": 7 }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true, \"is_android_started\": true, \"pid\": \"31176\" }", 0)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true, \"is_android_started\": true, \"created_timestamp\": 1.5 }", 0)]
    [InlineData("{ \"errcode\": 0, \"errmsg\": \"\" }", 0)]
    [InlineData("{ \"errcode\": -200, \"errmsg\": \"player index not found\" }", 0)]
    [InlineData("{ \"errcode\": -201, \"errmsg\": \"player index not found\" }", -200)]
    [InlineData("{ \"errcode\": -200 }", -200)]
    [InlineData("{ \"index\": \"1\", \"is_process_started\": true, \"is_android_started\": true }", 3)]
    [InlineData("[1]", 0)]
    public void UnknownInstanceShapeFailsClosed(string standardOutput, int exitCode)
    {
        ApplicationResult<MuMuInstanceQueryResult> result =
            MuMuManagerResponseParser.ParseInstanceQuery(FirstInstance, standardOutput, exitCode);

        AssertFailure(result, exitCode, null);
    }

    [Fact(DisplayName = "Перечисление перечисляет и остановленный экземпляр")]
    public void EnumerationListsStoppedInstance()
    {
        ApplicationResult<MuMuInstanceEnumeration> result = MuMuManagerResponseParser.ParseInstanceEnumeration(
            MuMuObservedPayloads.SingleStoppedEnumerationResponse,
            0);

        Assert.True(result.IsSuccess);

        MuMuInstanceEnumeration enumeration = result.Value!;

        MuMuInstanceInfo instance = Assert.Single(enumeration.Instances);

        Assert.Equal("1", instance.Id.Index);
        Assert.Equal(MuMuLifecycleState.Stopped, instance.State);
        Assert.Empty(enumeration.UnavailableEntries);
    }

    [Fact(DisplayName = "Перечисление различает запущенный и остановленный экземпляры")]
    public void EnumerationDistinguishesStates()
    {
        ApplicationResult<MuMuInstanceEnumeration> result = MuMuManagerResponseParser.ParseInstanceEnumeration(
            MuMuObservedPayloads.TwoInstancesEnumerationResponse,
            0);

        Assert.True(result.IsSuccess);

        MuMuInstanceEnumeration enumeration = result.Value!;

        Assert.Equal(2, enumeration.Instances.Count);
        Assert.Equal(MuMuLifecycleState.Running, enumeration.Instances[0].State);
        Assert.Equal("1", enumeration.Instances[0].Id.Index);
        Assert.Equal(MuMuLifecycleState.Stopped, enumeration.Instances[1].State);
        Assert.Equal("2", enumeration.Instances[1].Id.Index);
        Assert.Equal("Второй экземпляр", enumeration.Instances[1].DisplayName);
    }

    [Fact(DisplayName = "Перечисление сохраняет записи-ошибки, не теряя номер экземпляра")]
    public void EnumerationKeepsUnavailableEntries()
    {
        ApplicationResult<MuMuInstanceEnumeration> result = MuMuManagerResponseParser.ParseInstanceEnumeration(
            MuMuObservedPayloads.PartialEnumerationResponse,
            0);

        Assert.True(result.IsSuccess);

        MuMuInstanceEnumeration enumeration = result.Value!;

        _ = Assert.Single(enumeration.Instances);
        MuMuUnavailableEntry unavailable = Assert.Single(enumeration.UnavailableEntries);

        Assert.Equal("2", unavailable.Id.Index);
        Assert.Equal(MuMuObservedPayloads.IndexNotFoundExitCode, unavailable.ProviderError.Code);
    }

    [Fact(DisplayName = "Пустое перечисление остаётся успешным результатом без экземпляров")]
    public void EmptyEnumerationIsSuccessful()
    {
        ApplicationResult<MuMuInstanceEnumeration> result =
            MuMuManagerResponseParser.ParseInstanceEnumeration("{}", 0);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Instances);
        Assert.Empty(result.Value!.UnavailableEntries);
    }

    [Theory(DisplayName = "Неизвестная форма перечисления не разбирается")]
    [InlineData("{ \"all\": {} }", 0)]
    [InlineData("{ \"01\": { \"index\": \"1\", \"is_process_started\": false, \"is_android_started\": false } }", 0)]
    [InlineData("{ \"1\": [] }", 0)]
    [InlineData("{ \"1\": { \"index\": \"1\" } }", 0)]
    [InlineData("{ \"1\": { \"errcode\": 0, \"errmsg\": \"\" } }", 0)]
    [InlineData("{ \"1\": { \"errcode\": -200 } }", 0)]
    [InlineData("{ \"errcode\": -200, \"errmsg\": \"player index not found\" }", -200)]
    [InlineData("{ \"1\": { \"index\": \"1\", \"is_process_started\": false, \"is_android_started\": false } }", 5)]
    [InlineData("null", 0)]
    public void UnknownEnumerationShapeFailsClosed(string standardOutput, int exitCode)
    {
        ApplicationResult<MuMuInstanceEnumeration> result =
            MuMuManagerResponseParser.ParseInstanceEnumeration(standardOutput, exitCode);

        AssertFailure(result, exitCode, null);
    }

    [Fact(DisplayName = "Ответ операции изменения состояния распознаётся как принятый")]
    public void AcceptedControlResponseIsRecognized()
    {
        ApplicationResult<MuMuControlOutcome> result = MuMuManagerResponseParser.ParseControlResult(
            FirstInstance,
            MuMuControlCommand.Launch,
            MuMuObservedPayloads.AcceptedControlResponse,
            0);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsAccepted);
        Assert.Null(result.Value!.ProviderError);
        Assert.Equal(MuMuControlCommand.Launch, result.Value!.Command);
        Assert.Equal("1", result.Value!.Id.Index);
    }

    [Fact(DisplayName = "Отказ провайдера на операцию изменения состояния возвращается значением")]
    public void RejectedControlResponseIsReturnedAsValue()
    {
        ApplicationResult<MuMuControlOutcome> result = MuMuManagerResponseParser.ParseControlResult(
            MuMuInstanceId.FromIndex("5"),
            MuMuControlCommand.Shutdown,
            MuMuObservedPayloads.IndexNotFoundResponse,
            MuMuObservedPayloads.IndexNotFoundExitCode);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAccepted);
        Assert.Equal(MuMuObservedPayloads.IndexNotFoundExitCode, result.Value!.ProviderError!.Code);
    }

    [Theory(DisplayName = "Неизвестная форма ответа операции изменения состояния не разбирается")]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("{ \"errcode\": 0 }", 0)]
    [InlineData("{ \"errmsg\": \"\" }", 0)]
    [InlineData("{ \"errcode\": 0, \"errmsg\": 5 }", 0)]
    [InlineData("{ \"errcode\": 0, \"errmsg\": \"\" }", -200)]
    [InlineData("{ \"errcode\": -200, \"errmsg\": \"player index not found\" }", 0)]
    [InlineData("{ \"errcode\": -201, \"errmsg\": \"player index not found\" }", -200)]
    public void UnknownControlShapeFailsClosed(string standardOutput, int exitCode)
    {
        ApplicationResult<MuMuControlOutcome> result = MuMuManagerResponseParser.ParseControlResult(
            FirstInstance,
            MuMuControlCommand.Shutdown,
            standardOutput,
            exitCode);

        AssertFailure(result, exitCode, null);
    }

    [Fact(DisplayName = "Нераспознанная форма даёт отказ control surface с bounded details")]
    public void UnrecognizedShapeProducesBoundedFailure()
    {
        // Код в теле и код выхода расходятся: ответ не целостен, поэтому он не разбирается, а код
        // провайдера остаётся диагностическим фактом.
        ApplicationResult<MuMuInstanceQueryResult> result =
            MuMuManagerResponseParser.ParseInstanceQuery(FirstInstance, "{ \"errcode\": -7, \"errmsg\": \"x\" }", 0);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.Equal(MuMuFailureReasons.ResponseUnrecognized, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("0", failure.Details![MuMuFailureDetailKeys.ExitCode]);
        Assert.Equal("-7", failure.Details![MuMuFailureDetailKeys.ProviderErrorCode]);
    }

    [Fact(DisplayName = "Неизвестный код провайдера не трактуется как отсутствие экземпляра")]
    public void UnknownProviderCodeIsNotIndexNotFound()
    {
        ApplicationResult<MuMuInstanceQueryResult> result = MuMuManagerResponseParser.ParseInstanceQuery(
            FirstInstance,
            "{ \"errcode\": -7, \"errmsg\": \"неизвестный отказ\" }",
            -7);

        Assert.True(result.IsSuccess);

        MuMuInstanceQueryResult query = result.Value!;

        Assert.False(query.IsFound);
        Assert.False(query.IsIndexNotFound);
        Assert.Equal(-7, query.ProviderError!.Code);
        Assert.Equal("неизвестный отказ", query.ProviderError.Message);
    }

    private static void AssertFailure<T>(ApplicationResult<T> result, int exitCode, int? providerErrorCode)
    {
        Assert.True(result.IsFailure, "Ожидался отказ разбора, но результат успешен.");

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.Equal(MuMuFailureReasons.ResponseUnrecognized, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal(
            exitCode.ToString(CultureInfo.InvariantCulture),
            failure.Details![MuMuFailureDetailKeys.ExitCode]);

        if (providerErrorCode is int code)
        {
            Assert.Equal(
                code.ToString(CultureInfo.InvariantCulture),
                failure.Details![MuMuFailureDetailKeys.ProviderErrorCode]);
        }
    }
}
