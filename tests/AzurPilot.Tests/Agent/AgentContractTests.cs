using System.Text.Json;
using System.Text.RegularExpressions;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.Agent;

[Trait("Category", "Agent")]
public sealed class AgentContractTests
{
    [Theory]
    [InlineData("android-prepare", true)]
    [InlineData("game-dock-roundtrip", true)]
    [InlineData("game-ui-ready-ensure", true)]
    [InlineData("game-ui-ready-observe", false)]
    [InlineData("android-observe", false)]
    [InlineData("game-observe", false)]
    [InlineData("game-ui-observe", false)]
    [InlineData("game-input-observe", false)]
    [InlineData("screenshot-scan", false)]
    [InlineData("diagnostics", false)]
    [InlineData("status", false)]
    public async Task DiscoveryMarksTransportMutationAndPreservesReadOnlyCommands(string name, bool mutation)
    {
        AgentEnvelope result = await AgentRunner.RunAsync(["agent", "discover"], cancellationToken: TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref result));
        JsonElement command = json.RootElement.GetProperty("data").GetProperty("commands").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name);
        Assert.Equal(mutation, command.GetProperty("mutation").GetBoolean());
        if (name == "android-prepare")
        {
            Assert.True(command.GetProperty("requiresResolvedTarget").GetBoolean());
        }
    }

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
                Assert.True(argumentTypes.TryGetProperty(type, out JsonElement definition),
                    $"Discovery не описывает тип аргумента {type}.");
                Assert.True(definition.GetProperty("type").GetString() is "integer" or "string");
                Assert.Equal(AgentArgumentTypes.All[type].MaximumLength, definition.GetProperty("maximumLength").GetInt32());
            }
        }

        Assert.DoesNotContain(AgentCommands.All, c => c.Name is "Exit" or "Select" or "build" or "ocr");
    }

    [Fact]
    public void LeaseUsesConfigurationApplicationFolder()
    {
        MuMuInstanceId identity = MuMuInstanceId.FromIndex(Random.Shared.Next(100000, int.MaxValue)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        var acquired = WindowsInstanceMutationLease.TryAcquire(identity);
        Assert.True(acquired.IsSuccess);
        using FileStream lease = acquired.Value!;
        Assert.Equal(Path.Combine(Path.GetDirectoryName(AzurPilotConfigurationPath.GetDefaultRuntimePath())!,
            "mutation-locks", identity.Index + ".lock"), lease.Name);
    }

    [Theory]
    [InlineData("--timeout-ms", "0", false)]
    [InlineData("--timeout-ms", "1", true)]
    [InlineData("--timeout-ms", "300000", true)]
    [InlineData("--timeout-ms", "300001", false)]
    [InlineData("--timeout-ms", "0001", true)]
    [InlineData("--timeout-ms", "+1", false)]
    [InlineData("--timeout-ms", " 1", false)]
    [InlineData("--timeout-ms", "1.5", false)]
    [InlineData("--timeout-ms", "١", false)]
    [InlineData("--target", "auto", true)]
    [InlineData("--target", "Auto", false)]
    [InlineData("--target", "mumu:0", true)]
    [InlineData("--target", "mumu:12", true)]
    [InlineData("--target", "mumu:01", false)]
    [InlineData("--target", "mumu:-1", false)]
    [InlineData("--target", "mumu:١", false)]
    [InlineData("--target", "mumu:1\n", false)]
    [InlineData("--config", "relative.yaml", false)]
    [InlineData("--config", "C:relative.yaml", false)]
    [InlineData("--config", "\\rooted.yaml", false)]
    public async Task ParserMatchesPublishedArgumentTypeConstraints(string option, string value, bool accepted)
    {
        AgentEnvelope envelope = await AgentRunner.RunAsync(["agent", "discover"], cancellationToken: TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(AgentJson.Serialize(ref envelope));
        JsonElement data = json.RootElement.GetProperty("data");
        JsonElement argument = data.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "resolve")
            .GetProperty("arguments").EnumerateArray().Single(a => a.GetProperty("name").GetString() == option);
        JsonElement definition = data.GetProperty("argumentTypes").GetProperty(argument.GetProperty("type").GetString()!);
        bool described = value.Length <= definition.GetProperty("maximumLength").GetInt32();
        if (definition.GetProperty("pattern").ValueKind == JsonValueKind.String)
        {
            Match match = Regex.Match(value, definition.GetProperty("pattern").GetString()!);
            described &= match.Success && match.Length == value.Length;
        }

        if (definition.GetProperty("type").GetString() == "integer")
        {
            described &= long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long number)
                && number >= definition.GetProperty("minimum").GetInt32() && number <= definition.GetProperty("maximum").GetInt32();
        }

        if (definition.GetProperty("fullyQualified").ValueKind == JsonValueKind.True)
        {
            Assert.Equal("windows", definition.GetProperty("platform").GetString());
            described &= Path.IsPathFullyQualified(value);
        }

        Assert.Equal(accepted, described);
        Assert.Equal(described, AgentRequest.Parse(["agent", "resolve", option, value]).IsSuccess);
    }

    [Fact]
    public void ConfigurationTypeAcceptsAbsolutePaths()
    {
        Assert.True(AgentRequest.Parse(["agent", "settings", "--config", Path.Combine(Path.GetTempPath(), "config.yaml")]).IsSuccess);
        Assert.True(AgentRequest.Parse(["agent", "settings", "--config", new string(Path.DirectorySeparatorChar, 2) + Path.Combine("server", "share", "config.yaml")]).IsSuccess);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ParserHonorsPublishedLengthBoundaryForOtherwiseValidValues(int excess, bool accepted)
    {
        foreach (AgentArgument argument in AgentCommands.All.Single(c => c.Name == "resolve").Arguments)
        {
            // Граница каждого аргумента — опубликованная в discovery длина его типа.
            int length = AgentArgumentTypes.All[argument.Type].MaximumLength + excess;
            string prefix = argument.Name switch
            {
                "--target" => MuMuInstanceValue.ProviderPrefix,
                "--config" => Path.GetTempPath(),
                _ => string.Empty,
            };
            string value = prefix + new string('0', length - prefix.Length - 1) + '1';
            if (argument.Name == "--target")
            {
                value = prefix + new string('1', length - prefix.Length);
            }

            Assert.Equal(accepted, AgentRequest.Parse(["agent", "resolve", argument.Name, value]).IsSuccess);
        }
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
        ["agent", "discover", "--debug-report", "pr-1"], ["agent", "status", "--debug-report"],
        ["agent", "status", "--debug-report", "pr-1", "--debug-report", "pr-2"],
        ["agent", "status", "--debug-report", "Pr-1"], ["agent", "status", "--debug-report", "-pr"],
        ["agent", "status", "--debug-report", "pr-"], ["agent", "status", "--debug-report", "con"],
        ["agent", "status", "--debug-report", "com1"], ["agent", "status", "--debug-report", "a/b"],
        ["agent", "status", "--debug-report", ".."], ["agent", "status", "--debug-report", "pr_1"],
        ["agent", "status", "--debug-report", new string('a', 65)],
    ];

    [Fact]
    public void DebugReportIsOptionalExplicitAndBoundedSession()
    {
        Assert.Null(AgentRequest.Parse(["agent", "status"]).Value!.DebugSession);
        Assert.Equal("pr-25-debug", AgentRequest.Parse(["agent", "status", "--debug-report", "pr-25-debug"]).Value!.DebugSession);
        Assert.Equal(new string('a', 64), AgentRequest.Parse(["agent", "status", "--debug-report", new string('a', 64)]).Value!.DebugSession);
        Assert.True(AgentRequest.Parse(["agent", "game-dock-roundtrip", "--target", "mumu:7", "--timeout-ms", "1000",
            "--config", Path.Combine(Path.GetTempPath(), "c.yaml"), "--debug-report", "pr-1"]).IsSuccess);
        Assert.All(AgentCommands.All.Where(c => c.Name != "discover"),
            c => Assert.Contains(c.Arguments, a => a.Name == AgentDebugRun.ArgumentName && a.Type == "debug-session" && !a.Required));
        Assert.DoesNotContain(AgentCommands.All.Single(c => c.Name == "discover").Arguments, a => a.Name == AgentDebugRun.ArgumentName);
    }

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
