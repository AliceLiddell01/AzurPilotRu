using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.Agent;

[Trait("Category", "Agent")]
public sealed class AgentContractTests
{
    [Fact]
    public void MutationLeaseRejectsNonCanonicalIdentityBeforeFilesystemAccess()
    {
        Assert.Equal(ApplicationFailure.InvalidArgument,
            WindowsInstanceMutationLease.TryAcquire(new MuMuInstanceId("../outside")).FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.InvalidArgument,
            WindowsInstanceMutationLease.TryAcquire(default).FailureInfo!.Code);
    }

    [Fact]
    public async Task DiscoveryNeverBuildsHostAndDescribesActualHandlers()
    {
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "discover"], (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement data = json.RootElement.GetProperty("data");
        JsonElement commands = data.GetProperty("commands");
        JsonElement argumentTypes = data.GetProperty("argumentTypes");
        Assert.True(argumentTypes.TryGetProperty("MuMuInstanceValue", out JsonElement muMuInstanceValue));
        Assert.Equal("string", muMuInstanceValue.GetProperty("type").GetString());
        Assert.Equal(AgentCommands.All.Count, commands.GetArrayLength());
        Assert.Equal(AgentCommands.All.Count, commands.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Distinct().Count());
        foreach (AgentCommand command in AgentCommands.All)
        {
            Assert.NotNull(command.Handler);
            AgentRequest request = AgentRequest.Parse(["agent", command.Name]).Value!;
            Assert.Same(command, request.Command);
            JsonElement description = commands.EnumerateArray().Single(c => c.GetProperty("name").GetString() == command.Name);
            Assert.Equal(command.Mutation, description.GetProperty("mutation").GetBoolean());
            Assert.Equal(command.Arguments.Count, description.GetProperty("arguments").GetArrayLength());
            foreach (JsonElement argument in description.GetProperty("arguments").EnumerateArray())
            {
                string type = argument.GetProperty("type").GetString()!;
                Assert.True(type is "integer" or "absolute-path" || argumentTypes.TryGetProperty(type, out _),
                    $"Discovery не описывает тип аргумента {type}.");
            }
        }

        Assert.DoesNotContain(AgentCommands.All, c => c.Name is "Exit" or "Select" or "build" or "ocr");
    }

    private static readonly string[][] InvalidCases =
    [
        ["agent"], ["agent", "missing"], ["--mode", "agent"],
        ["agent", "status", "--target", "mumu:1"], ["agent", "resolve", "--target", "mumu:01"],
        ["agent", "resolve", "--target", "mumu:-1"], ["agent", "resolve", "--target"],
        ["agent", "resolve", "--target", "mumu:1", "--target", "mumu:2"],
        ["agent", "status", "--timeout-ms", "0"], ["agent", "status", "--timeout-ms", "300001"],
        ["agent", "status", "--timeout-ms", "1.5"], ["agent", "status", "--timeout-ms", "-1"],
        ["agent", "status", "--mode", "normal"], ["agent", "status", "--log-viewer"],
        ["agent", "status", "--config", "relative.yaml"], ["agent", "discover", "--config", Path.GetTempPath()],
        ["agent", "resolve", "--endpoint", "127.0.0.1:1234"],
        ["agent", "status", "--timeout-ms", new string('1', 257)],
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task InvalidArgumentsAlwaysProduceMachineFailure(int caseIndex)
    {
        AgentEnvelope result = await AgentRunner.RunAsync(InvalidCases[caseIndex], (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken);
        Assert.Equal(AzurPilotExitCode.InvalidArgument, result.ExitCode);
        Assert.Equal("failure", result.Status);
        Assert.Equal("none", result.Effect);
        using JsonDocument document = JsonDocument.Parse(AgentJson.Serialize(ref result));
        Assert.Equal(ApplicationFailure.InvalidArgument, document.RootElement.GetProperty("failure").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300000)]
    public void DeadlineAcceptsBothInclusiveBounds(int milliseconds)
    {
        var result = AgentRequest.Parse(["agent", "status", "--timeout-ms", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Assert.True(result.IsSuccess);
        Assert.Equal(milliseconds, result.Value!.TimeoutMilliseconds);
    }

    [Fact]
    public async Task EarlyExceptionDoesNotLeakMessage()
    {
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "status"], (_, _) => throw new InvalidOperationException("secret-marker"), TestContext.Current.CancellationToken);
        byte[] json = AgentJson.Serialize(ref result);
        Assert.Equal(AzurPilotExitCode.InternalError, result.ExitCode);
        Assert.DoesNotContain("secret-marker", System.Text.Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientCancellationAndDeadlineHaveDistinctOutcomes()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        AgentEnvelope client = await AgentRunner.RunAsync(["agent", "status"], cancellationToken: cancelled.Token);
        Assert.Equal("cancelled", client.Status);
        Assert.Equal(AzurPilotExitCode.OperationCancelled, client.ExitCode);
        AgentEnvelope deadline = await AgentRunner.RunAsync(["agent", "status", "--timeout-ms", "1"], async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }, TestContext.Current.CancellationToken);
        Assert.Equal("failure", deadline.Status);
        Assert.Equal(AzurPilotExitCode.AgentDeadlineExceeded, deadline.ExitCode);
        Assert.Equal("none", deadline.Effect);
    }

    [Fact]
    public void OversizedResponseIsReplacedBeforeWritingAndPreservesEffect()
    {
        AgentEnvelope result = AgentJson.Envelope("correlation", "operation", "mumu:1",
            new(new string('x', AgentJson.MaximumOutputBytes), Effect: "changed"));
        byte[] json = AgentJson.Serialize(ref result);
        Assert.True(json.Length < AgentJson.MaximumOutputBytes);
        Assert.Equal(AzurPilotExitCode.AgentOutputLimitExceeded, result.ExitCode);
        Assert.Equal("changed", result.Effect);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public void NestedFailureUsesBoundedSafeProjection()
    {
        ApplicationFailure failure = new()
        {
            Code = ApplicationFailure.InternalError,
            Message = "secret-marker",
            Details = new Dictionary<string, string> { ["evidence"] = new string('x', 1000), ["path"] = Path.GetTempPath() },
        };
        AgentEnvelope result = AgentJson.Envelope("correlation", "operation", null, new(new { nested = failure }));
        string output = System.Text.Encoding.UTF8.GetString(AgentJson.Serialize(ref result));
        Assert.DoesNotContain("secret-marker", output, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement details = json.RootElement.GetProperty("data").GetProperty("nested").GetProperty("details");
        Assert.Equal(256, details.GetProperty("evidence").GetString()!.Length);
        Assert.Equal("[путь скрыт]", details.GetProperty("path").GetString());
    }
}
