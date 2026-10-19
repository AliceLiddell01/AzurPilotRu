using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.LogViewer;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Agent;

[Trait("Category", "Agent")]
public sealed class AgentProcessTests
{
    [Fact]
    public async Task LegacyViewerAcceptsAgentAsOptionValue()
    {
        using AgentTestDirectory directory = new();
        using NamedPipeServerStream server = new("agent", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using Process process = directory.StartApplication("--log-viewer", "--log-viewer-pipe", "agent");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using StreamReader reader = new(server, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(AzurPilotLogViewerProtocol.HandshakeLine, await reader.ReadLineAsync(deadline.Token));
            server.Disconnect();
            AgentProcessRun run = await ReadAsync(process);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("Журнал AzurPilot", run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"schemaVersion\"", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task LegacyHelpRemainsHumanReadable()
    {
        using AgentTestDirectory directory = new();
        using Process process = directory.StartApplication("--help");
        AgentProcessRun run = await ReadAsync(process);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Использование:", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Theory]
    [InlineData("discover", 0)]
    [InlineData("unknown", AzurPilotExitCode.InvalidArgument)]
    public async Task RealExecutableWritesExactlyOneJsonDocument(string command, int exitCode)
    {
        using AgentTestDirectory directory = new();
        using Process process = directory.StartApplication("agent", command);
        var run = await ReadAsync(process);
        using JsonDocument json = AssertEnvelope(run, exitCode);
        Assert.Equal(command == "discover" ? "success" : "failure", json.RootElement.GetProperty("status").GetString());
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task RealExecutableRejectsInvalidConfigurationWithoutViewerOrBanner()
    {
        using AgentTestDirectory directory = new();
        string path = Path.Combine(directory.Root, "invalid.yaml");
        await File.WriteAllTextAsync(path, "invalid: true", TestContext.Current.CancellationToken);
        using Process process = directory.StartApplication("agent", "status", "--config", path);
        var run = await ReadAsync(process);
        using JsonDocument json = AssertEnvelope(run, AzurPilotExitCode.ConfigurationInvalid);
        Assert.Equal(ApplicationFailure.ConfigurationInvalid, json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("none", json.RootElement.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task RealExecutableReportsAbsentNativeAndCanReadSettingsWithoutIt()
    {
        using AgentTestDirectory directory = new(copyWithoutNative: true);
        string configPath = Path.Combine(directory.Root, "config.yaml");
        var config = AzurPilotConfigurationDefaults.Create();
        config = config with { Logging = config.Logging with { Viewer = config.Logging.Viewer with { Enabled = true } } };
        await File.WriteAllTextAsync(configPath, AzurPilotConfigurationYamlWriter.Write(config), TestContext.Current.CancellationToken);
        using Process diagnostics = directory.StartApplication("agent", "diagnostics", "--config", configPath);
        var run = await ReadAsync(diagnostics);
        using JsonDocument json = AssertEnvelope(run, AzurPilotExitCode.NativeUnavailable);
        Assert.Equal(ApplicationFailure.NativeUnavailable, json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        using Process settings = directory.StartApplication("agent", "settings", "--config", configPath);
        using JsonDocument settingsJson = AssertEnvelope(await ReadAsync(settings), 0);
        Assert.True(settingsJson.RootElement.GetProperty("data").GetProperty("logging").GetProperty("viewer").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task LegacyRedirectedEntryStillProducesHumanReport()
    {
        using AgentTestDirectory directory = new();
        using Process process = directory.StartApplication();
        var run = await ReadAsync(process);
        Assert.Contains("Итог:", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealExecutableDeadlineReturnsOneFailureDocument()
    {
        using AgentTestDirectory directory = new();
        using Process process = directory.StartApplication("agent", "status", "--config",
            Path.Combine(directory.Root, "missing.yaml"), "--timeout-ms", "1");
        using JsonDocument json = AssertEnvelope(await ReadAsync(process), AzurPilotExitCode.AgentDeadlineExceeded);
        Assert.Equal(ApplicationFailure.AgentDeadlineExceeded, json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("none", json.RootElement.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task RealExecutableWritesDebugReportWithoutChangingStdout()
    {
        using AgentTestDirectory directory = new();
        string configPath = Path.Combine(directory.Root, "config.yaml");
        await File.WriteAllTextAsync(configPath, AzurPilotConfigurationYamlWriter.Write(AzurPilotConfigurationDefaults.Create()),
            TestContext.Current.CancellationToken);

        // Без checkout отчёт создать нельзя: команда отклоняется до runtime одним документом.
        using Process outside = directory.StartApplication("agent", "settings", "--config", configPath, "--debug-report", "proc-test");
        using (JsonDocument rejected = AssertEnvelope(await ReadAsync(outside), AzurPilotExitCode.DebugReportUnavailable))
        {
            Assert.Equal(ApplicationFailure.DebugReportUnavailable, rejected.RootElement.GetProperty("failure").GetProperty("code").GetString());
            Assert.Equal("none", rejected.RootElement.GetProperty("effect").GetString());
        }

        _ = Directory.CreateDirectory(Path.Combine(directory.Root, ".git"));
        using Process plain = directory.StartApplication("agent", "settings", "--config", configPath);
        AgentProcessRun plainRun = await ReadAsync(plain);
        using JsonDocument plainJson = AssertEnvelope(plainRun, 0);
        Assert.False(Directory.Exists(Path.Combine(directory.Root, ".codex")));

        using Process debug = directory.StartApplication("agent", "settings", "--config", configPath, "--debug-report", "proc-test");
        AgentProcessRun debugRun = await ReadAsync(debug);
        using JsonDocument debugJson = AssertEnvelope(debugRun, 0);
        Assert.Equal(plainJson.RootElement.GetProperty("data").GetRawText(), debugJson.RootElement.GetProperty("data").GetRawText());

        string reportLine = Assert.Single(debugRun.Error.Split('\n'), l => l.Contains("\"AzurPilot.App.Agent.Debug\"", StringComparison.Ordinal));
        using JsonDocument report = JsonDocument.Parse(reportLine);
        Assert.Equal("complete", report.RootElement.GetProperty("diagnostics").GetString());
        string relative = report.RootElement.GetProperty("debugReport").GetString()!;
        Assert.StartsWith(".codex/local/reports/proc-test/", relative, StringComparison.Ordinal);
        string runDirectory = Path.Combine(directory.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(runDirectory, "manifest.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal(debugJson.RootElement.GetProperty("correlationId").GetString(),
            manifest.RootElement.GetProperty("run").GetProperty("correlationId").GetString());
        string arguments = manifest.RootElement.GetProperty("run").GetProperty("arguments").GetRawText();
        Assert.DoesNotContain(directory.Root.Replace("\\", "\\\\", StringComparison.Ordinal), arguments, StringComparison.OrdinalIgnoreCase);
        string trace = await File.ReadAllTextAsync(Path.Combine(runDirectory, "trace.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("\"host_started\"", trace, StringComparison.Ordinal);
        Assert.Contains("\"envelope_ready\"", trace, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(runDirectory, "summary.md")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "performance.json")));
        Assert.True(File.Exists(Path.Combine(directory.Root, ".codex", "local", "reports", "proc-test", "runs.jsonl")));
    }

    internal static JsonDocument AssertEnvelope(AgentProcessRun run, int exitCode)
    {
        Assert.Equal(exitCode, run.ExitCode);
        Assert.DoesNotContain('\u001b', run.Output);
        Assert.False(run.Output.StartsWith('\ufeff'));
        JsonDocument json = JsonDocument.Parse(run.Output);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(exitCode, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("correlationId").GetString()!);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("effect").ValueKind);
        Assert.True(json.RootElement.GetProperty("data").ValueKind is JsonValueKind.Object or JsonValueKind.Null);
        Assert.True(json.RootElement.GetProperty("failure").ValueKind is JsonValueKind.Object or JsonValueKind.Null);
        return json;
    }

    internal static async Task<AgentProcessRun> ReadAsync(Process process)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            throw;
        }

        return new(process.ExitCode, await output, await error);
    }
}

internal sealed record AgentProcessRun(int ExitCode, string Output, string Error);

internal sealed class AgentTestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "azurpilot-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly string _applicationDirectory;

    internal AgentTestDirectory(bool copyWithoutNative = false)
    {
        _ = Directory.CreateDirectory(Root);
        _applicationDirectory = Path.Combine(AppContext.BaseDirectory, "app-host");
        if (copyWithoutNative)
        {
            _applicationDirectory = Path.Combine(Root, "application");
            _ = Directory.CreateDirectory(_applicationDirectory);
            foreach (string source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "app-host")))
            {
                if (Path.GetFileName(source).Equals("AzurPilot.Native.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(source, Path.Combine(_applicationDirectory, Path.GetFileName(source)));
            }
        }
    }

    internal Process StartApplication(params string[] arguments)
        => Start(Path.Combine(_applicationDirectory, "AzurPilot.App.exe"), arguments);

    internal Process StartProbe(string role, string identity, string completion = "success")
        => Start(Path.Combine(AppContext.BaseDirectory, "native-absence-probe", "AzurPilot.NativeAbsenceProbe.exe"),
            ["--agent-mutation-seam", role, identity, Root, completion]);

    private Process Start(string executable, string[] arguments)
    {
        ProcessStartInfo info = new(executable)
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить проверочный процесс.");
    }

    internal async Task WaitForMutationAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(Root, "entered")))
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    internal void Release() => File.WriteAllText(Path.Combine(Root, "release"), "release");
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
