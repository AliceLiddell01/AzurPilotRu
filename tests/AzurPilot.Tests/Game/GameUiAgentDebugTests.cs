using System.Buffers.Binary;
using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.MuMu;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed partial class AzurPilotOperatorServiceTests
{
    private const string DebugSession = "test-debug";

    [Fact(DisplayName = "Debug Mode: каждый capture dock-roundtrip даёт JPEG, связанный с trace и envelope")]
    public async Task DebugDockRoundtripLinksEveryCaptureToJpegAndTrace()
    {
        using TemporaryCheckout checkout = new();
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        OperatorFixture? fixture = null;

        AgentRunResult result = await AgentRunner.RunWithDebugAsync(
            ["agent", "game-dock-roundtrip", "--target", "mumu:7", "--debug-report", DebugSession],
            debug =>
            {
                Assert.NotNull(debug);
                fixture = CreateDebugFixture(debug, MuMuLifecycleTestContext.Instance("7"));
                fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
                fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
                QueueReadyAndroid(fixture);
                QueueGameCapture(fixture, "main");
                fixture.Runner.EnqueueOutcome(0, "");
                QueueGameCapture(fixture, "dock");
                QueueViewport(fixture);
                fixture.Runner.EnqueueOutcome(0, "");
                QueueGameCapture(fixture, "main");
                return (run, token) => run(fixture.Service, token);
            },
            checkout.Path,
            TestContext.Current.CancellationToken);

        AgentEnvelope envelope = result.Envelope;
        Assert.True(envelope.ExitCode == 0, string.Join("\n", File.ReadLines(Path.Combine(checkout.Resolve(result.Debug!.RelativeRunDirectory), "trace.jsonl")).Where(l => !l.Contains("\"log\"", StringComparison.Ordinal)).Select(l => l.Length > 400 ? l[..400] : l)));
        Assert.Equal("changed", envelope.Effect);
        Assert.NotNull(result.Debug);
        Assert.Equal("complete", result.Debug.Status);
        Assert.Empty(result.Debug.IncompleteReasons);
        Assert.Equal(3, fixture!.ScreenshotHost.CaptureCount);

        string runDirectory = checkout.Resolve(result.Debug.RelativeRunDirectory);
        foreach (string file in new[] { "manifest.json", "trace.jsonl", "performance.json", "summary.md" })
        {
            Assert.True(File.Exists(Path.Combine(runDirectory, file)), file);
        }

        string[] jpegs = Directory.GetFiles(Path.Combine(runDirectory, "frames"), "*.jpg");
        Assert.Equal(3, jpegs.Length);
        foreach (string jpeg in jpegs)
        {
            Assert.Equal((Core.Game.GameGeometry.LogicalWidth, Core.Game.GameGeometry.LogicalHeight), JpegSize(File.ReadAllBytes(jpeg)));
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")));
        JsonElement root = manifest.RootElement;
        Assert.Equal("completed", root.GetProperty("state").GetString());
        Assert.Equal("complete", root.GetProperty("diagnostics").GetProperty("status").GetString());
        Assert.Equal("mumu:7", root.GetProperty("run").GetProperty("resolvedTarget").GetString());
        Assert.Equal(envelope.CorrelationId, root.GetProperty("run").GetProperty("correlationId").GetString());
        Assert.Equal(3, root.GetProperty("counters").GetProperty("captured").GetInt32());
        Assert.Equal(3, root.GetProperty("counters").GetProperty("rendered").GetInt32());
        JsonElement[] frames = [.. root.GetProperty("frames").EnumerateArray()];
        Assert.All(frames, f =>
        {
            Assert.Equal("rendered", f.GetProperty("status").GetString());
            Assert.True(File.Exists(Path.Combine(runDirectory, f.GetProperty("file").GetString()!)));
        });

        using JsonDocument output = JsonDocument.Parse(AgentJson.Serialize(ref envelope));
        JsonElement data = output.RootElement.GetProperty("data");
        HashSet<string> envelopeFrames = new(StringComparer.Ordinal);
        foreach (string action in new[] { "open", "close" })
        {
            foreach (string side in new[] { "before", "after" })
            {
                _ = envelopeFrames.Add(Guid.Parse(data.GetProperty(action).GetProperty(side).GetProperty("frame")
                    .GetProperty("frameId").GetString()!).ToString("N"));
            }
        }

        Assert.Equal(envelopeFrames.Order(StringComparer.Ordinal),
            frames.Select(f => f.GetProperty("frameId").GetString()!).Order(StringComparer.Ordinal));

        JsonElement[] trace = [.. File.ReadLines(Path.Combine(runDirectory, "trace.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)];
        Assert.Equal(Enumerable.Range(1, trace.Length).Select(i => (long)i), trace.Select(e => e.GetProperty("seq").GetInt64()));
        Assert.True(trace.Zip(trace.Skip(1)).All(p => p.First.GetProperty("tMs").GetDouble() <= p.Second.GetProperty("tMs").GetDouble()));
        string[] kinds = [.. trace.Select(e => e.GetProperty("kind").GetString()!)];
        Assert.Equal("run_started", kinds[0]);
        Assert.Equal(3, kinds.Count(k => k == "capture_completed"));
        foreach (string stage in new[] { "tap_planned", "tap_permitted", "tap_attempted", "tap_issued", "tap_completed" })
        {
            Assert.Equal(2, kinds.Count(k => k == stage));
        }

        Assert.DoesNotContain("tap_result_unproven", kinds);
        Assert.True(Array.IndexOf(kinds, "tap_planned") < Array.IndexOf(kinds, "tap_permitted"));
        Assert.True(Array.IndexOf(kinds, "tap_permitted") < Array.IndexOf(kinds, "tap_attempted"));
        Assert.True(Array.IndexOf(kinds, "tap_attempted") < Array.IndexOf(kinds, "tap_issued"));
        Assert.True(Array.IndexOf(kinds, "tap_issued") < Array.IndexOf(kinds, "tap_completed"));

        // Каждый ввод привязан к кадру, по которому он был разрешён, а этот кадр имеет JPEG.
        HashSet<string> rendered = [.. frames.Select(f => f.GetProperty("frame").GetString()!)];
        Assert.All(trace.Where(e => e.GetProperty("kind").GetString() is "tap_planned" or "tap_attempted" or "tap_completed"), e =>
        {
            Assert.Contains(e.GetProperty("frame").GetString()!, rendered);
            Assert.True(e.TryGetProperty("frameId", out _));
        });
        Assert.All(trace.Where(e => e.GetProperty("kind").GetString() == "tap_planned"),
            e => Assert.True(e.TryGetProperty("actionId", out _)));

        string index = Path.Combine(checkout.Path, ".codex", "local", "reports", DebugSession, "runs.jsonl");
        _ = Assert.Single(File.ReadAllLines(index));
    }

    [Fact(DisplayName = "Debug Mode не меняет envelope и без флага не создаёт файлов")]
    public async Task DebugModeKeepsEnvelopeAndWritesNothingWithoutFlag()
    {
        using TemporaryCheckout checkout = new();
        AgentRunResult plain = await RunAbsentScreenshotAsync(checkout, null);
        Assert.Null(plain.Debug);
        Assert.False(Directory.Exists(Path.Combine(checkout.Path, ".codex")));

        AgentRunResult debug = await RunAbsentScreenshotAsync(checkout, DebugSession);
        Assert.NotNull(debug.Debug);
        Assert.Equal(plain.Envelope.ExitCode, debug.Envelope.ExitCode);
        Assert.Equal(plain.Envelope.Effect, debug.Envelope.Effect);
        Assert.Equal(plain.Envelope.Status, debug.Envelope.Status);
        AgentEnvelope plainEnvelope = plain.Envelope;
        AgentEnvelope debugEnvelope = debug.Envelope;
        using JsonDocument plainJson = JsonDocument.Parse(AgentJson.Serialize(ref plainEnvelope));
        using JsonDocument debugJson = JsonDocument.Parse(AgentJson.Serialize(ref debugEnvelope));
        Assert.Equal(plainJson.RootElement.EnumerateObject().Select(p => p.Name), debugJson.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(plainJson.RootElement.GetProperty("failure").GetProperty("code").GetString(),
            debugJson.RootElement.GetProperty("failure").GetProperty("code").GetString());

        // Отказ capture без изображения не порождает JPEG и фиксируется счётчиком.
        string runDirectory = checkout.Resolve(debug.Debug.RelativeRunDirectory);
        Assert.Empty(Directory.GetFiles(Path.Combine(runDirectory, "frames")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")));
        JsonElement counters = manifest.RootElement.GetProperty("counters");
        Assert.Equal(0, counters.GetProperty("captured").GetInt32());
        Assert.Equal(1, counters.GetProperty("captureFailures").GetInt32());
        Assert.Equal("complete", manifest.RootElement.GetProperty("diagnostics").GetProperty("status").GetString());
        Assert.Contains(File.ReadLines(Path.Combine(runDirectory, "trace.jsonl")), line => line.Contains("\"capture_failed\"", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Debug Mode: параллельные прогоны одной сессии получают разные каталоги и общий индекс")]
    public async Task ParallelDebugRunsShareSessionIndex()
    {
        using TemporaryCheckout checkout = new();
        AgentRunResult[] results = await Task.WhenAll(
            RunAbsentScreenshotAsync(checkout, DebugSession),
            RunAbsentScreenshotAsync(checkout, DebugSession));
        Assert.All(results, r => Assert.NotNull(r.Debug));
        Assert.NotEqual(results[0].Debug!.RelativeRunDirectory, results[1].Debug!.RelativeRunDirectory);
        string[] lines = File.ReadAllLines(Path.Combine(checkout.Path, ".codex", "local", "reports", DebugSession, "runs.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("runId").GetString()).Distinct().Count());
    }

    [Fact(DisplayName = "Debug Mode вне checkout отклоняется до запуска runtime")]
    public async Task DebugReportOutsideCheckoutIsRejectedBeforeRuntime()
    {
        string outside = Path.Combine(Path.GetTempPath(), "azurpilot-no-checkout-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(outside);
        try
        {
            AgentRunResult result = await AgentRunner.RunWithDebugAsync(
                ["agent", "screenshot-scan", "--target", "mumu:7", "--debug-report", DebugSession],
                _ => (_, _) => throw new InvalidOperationException("Runtime не должен запускаться."),
                outside,
                TestContext.Current.CancellationToken);
            Assert.Equal(AzurPilotExitCode.DebugReportUnavailable, result.Envelope.ExitCode);
            Assert.Equal("none", result.Envelope.Effect);
            Assert.Null(result.Debug);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact(DisplayName = "Сбой учёта debug не меняет результат вызова и делает отчёт неполным")]
    public async Task DiagnosticFailureIsContainedAndReported()
    {
        using TemporaryCheckout checkout = new();
        string correlationId = Guid.NewGuid().ToString("N");
        AgentDebugRun run = AgentDebugRun.Create(DebugSession, correlationId, "status", null, ["status"], 1000,
            TimeProvider.System, checkout.Path).Value!;
        using (run)
        {
            // Исключение учёта не выходит из Guard: вызывающий production-код продолжает работу.
            run.Guard("test", () => throw new InvalidOperationException("сбой наблюдателя"));
            Assert.Equal(1, run.DiagnosticFailures);

            AgentDebugCompletion completion = await run.CompleteAsync(AgentJson.Envelope(correlationId, "status", null, new(null, null)));
            Assert.Equal("incomplete", completion.Status);
            Assert.Contains("diagnostic_failures:1", completion.IncompleteReasons);
            Assert.Contains(File.ReadLines(Path.Combine(run.RunDirectory, "trace.jsonl")),
                line => line.Contains("\"diagnostic_failed\"", StringComparison.Ordinal));
        }
    }

    private static async Task<AgentRunResult> RunAbsentScreenshotAsync(TemporaryCheckout checkout, string? session)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        string[] args = session is null
            ? ["agent", "screenshot-scan", "--target", "mumu:7"]
            : ["agent", "screenshot-scan", "--target", "mumu:7", "--debug-report", session];
        return await AgentRunner.RunWithDebugAsync(args, debug =>
        {
            OperatorFixture fixture = CreateDebugFixture(debug, MuMuLifecycleTestContext.Instance("7"));
            fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
            fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, endpoint));
            fixture.Runner.EnqueueBinaryOutcome([], exitCode: 1, standardError: $"error: device '{endpoint}' not found");
            return (run, token) => run(fixture.Service, token);
        }, checkout.Path, TestContext.Current.CancellationToken);
    }

    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        Assert.True(jpeg is [0xFF, 0xD8, ..] && jpeg[^2] == 0xFF && jpeg[^1] == 0xD9);
        for (int offset = 2; offset + 9 < jpeg.Length;)
        {
            Assert.Equal(0xFF, jpeg[offset]);
            byte marker = jpeg[offset + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 2));
            if (marker is 0xC0 or 0xC2)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 7)), BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 5)));
            }

            offset += 2 + length;
        }

        throw new InvalidDataException("JPEG без SOF.");
    }

    /// <summary>Временный каталог с метаданными Git: корень отчётов debug ищется по <c>.git</c>.</summary>
    private sealed class TemporaryCheckout : IDisposable
    {
        internal TemporaryCheckout()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "azurpilot-debug-tests", Guid.NewGuid().ToString("N"));
            string git = System.IO.Path.Combine(Path, ".git");
            _ = Directory.CreateDirectory(System.IO.Path.Combine(git, "refs", "heads"));
            File.WriteAllText(System.IO.Path.Combine(git, "HEAD"), "ref: refs/heads/test\n");
            File.WriteAllText(System.IO.Path.Combine(git, "refs", "heads", "test"), new string('a', 40) + "\n");
        }

        internal string Path { get; }

        internal string Resolve(string relative)
        {
            return System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
