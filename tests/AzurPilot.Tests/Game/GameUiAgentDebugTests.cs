using System.Buffers.Binary;
using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;
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

        // Каждая стадия tap_issued связана с тем же кадром и действием, что tap_attempted того же ввода, без аргументов процесса.
        JsonElement[] attempted = [.. trace.Where(e => e.GetProperty("kind").GetString() == "tap_attempted")];
        JsonElement[] issued = [.. trace.Where(e => e.GetProperty("kind").GetString() == "tap_issued")];
        JsonElement[] completed = [.. trace.Where(e => e.GetProperty("kind").GetString() == "tap_completed")];
        Assert.Equal(attempted.Length, issued.Length);
        for (int i = 0; i < issued.Length; i++)
        {
            foreach (string key in new[] { "frame", "frameId", "actionId" })
            {
                Assert.Equal(attempted[i].GetProperty(key).GetString(), issued[i].GetProperty(key).GetString());
                Assert.Equal(attempted[i].GetProperty(key).GetString(), completed[i].GetProperty(key).GetString());
            }

            JsonElement issuedData = issued[i].GetProperty("data");
            Assert.Equal("tap_permit", issuedData.GetProperty("correlation").GetString());
            Assert.False(issuedData.TryGetProperty("arguments", out _));
            Assert.False(issuedData.TryGetProperty("point", out _));
        }

        Assert.Contains("## Легенда JPEG", File.ReadAllText(Path.Combine(runDirectory, "summary.md")), StringComparison.Ordinal);
        AssertInventoryExact(runDirectory);
        string index = Path.Combine(checkout.Path, ".codex", "local", "reports", DebugSession, "runs.jsonl");
        _ = Assert.Single(File.ReadAllLines(index));
    }

    [Fact(DisplayName = "Инвентарь manifest.files точен и после неполного прогона с ошибкой индекса сессии")]
    public async Task ManifestInventoryIsExactAfterIncompleteRun()
    {
        using TemporaryCheckout checkout = new();
        string correlationId = Guid.NewGuid().ToString("N");
        AgentDebugRun run = AgentDebugRun.Create(DebugSession, correlationId, "status", null, ["status"], 1000,
            TimeProvider.System, checkout.Path).Value!;
        using (run)
        {
            // Каталог на месте индекса делает добавление строки невозможным: manifest и summary переписываются.
            _ = Directory.CreateDirectory(Path.Combine(run.SessionDirectory, AgentDebugRun.SessionIndexFile));
            AgentDebugCompletion completion = await run.CompleteAsync(AgentJson.Envelope(correlationId, "status", null, new(null, null)));
            Assert.Equal("incomplete", completion.Status);
            Assert.Contains("session_index_write_failed", completion.IncompleteReasons);
            AssertInventoryExact(run.RunDirectory);
        }
    }

    [Fact(DisplayName = "tap_issued связан с кадром и действием только внутри области своего разрешения")]
    public async Task TapIssuedIsCorrelatedOnlyWithinItsPermit()
    {
        using TemporaryCheckout checkout = new();
        string correlationId = Guid.NewGuid().ToString("N");
        AgentDebugRun run = AgentDebugRun.Create(DebugSession, correlationId, "status", null, ["status"], 1000,
            TimeProvider.System, checkout.Path).Value!;
        using (run)
        {
            Guid frameId = Guid.NewGuid();
            GameFrameProvenance frame = new(frameId, MuMuInstanceId.FromIndex("7"), new("explicit-host", 20312),
                DateTimeOffset.UtcNow, 1, 3840, 2160);
            GameTap tap = new(frame, "dock.open", "main.dock", new GamePixelRect(0, 0, 10, 10), new GameInputPoint(5, 5), TimeSpan.FromSeconds(1));
            ScriptedProcessRunner inner = new();
            AgentDebugProcessRunner runner = new(inner, run, "android");
            string[] tapArguments = ["-s", "host:1", "shell", "input", "tap", "123", "456"];

            // Вне области разрешения связь не выдумывается: кадр и действие отсутствуют.
            _ = await runner.RunAsync(Request(tapArguments), TestContext.Current.CancellationToken);
            AgentDebugTapContext.Current = new(tap, "f0007");
            try
            {
                // Другой процесс внутри области стадию issued не получает и область не расходует.
                _ = await runner.RunAsync(Request(["shell", "getprop"]), TestContext.Current.CancellationToken);
                _ = await runner.RunAsync(Request(tapArguments), TestContext.Current.CancellationToken);
                // Повторный запуск процесса ввода той же области связь не наследует.
                _ = await runner.RunAsync(Request(tapArguments), TestContext.Current.CancellationToken);
                // Отказ запуска процесса: tap не был отправлен, стадия issued не фиксируется.
                AgentDebugTapContext.Current = new(tap, "f0007");
                inner.FailNext = true;
                _ = await runner.RunAsync(Request(tapArguments), TestContext.Current.CancellationToken);
            }
            finally
            {
                AgentDebugTapContext.Current = null;
            }

            _ = await run.CompleteAsync(AgentJson.Envelope(correlationId, "status", null, new(null, null)));
            string traceText = File.ReadAllText(Path.Combine(run.RunDirectory, "trace.jsonl"));
            JsonElement[] issued = [.. traceText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("kind").GetString() == "tap_issued")];
            Assert.Equal(3, issued.Length);
            Assert.Equal("none", issued[0].GetProperty("data").GetProperty("correlation").GetString());
            Assert.False(issued[0].TryGetProperty("frameId", out _));
            Assert.Equal("tap_permit", issued[1].GetProperty("data").GetProperty("correlation").GetString());
            Assert.Equal(frameId.ToString("N"), issued[1].GetProperty("frameId").GetString());
            Assert.Equal("f0007", issued[1].GetProperty("frame").GetString());
            Assert.Equal("dock.open", issued[1].GetProperty("actionId").GetString());
            Assert.Equal("none", issued[2].GetProperty("data").GetProperty("correlation").GetString());
            Assert.False(issued[2].TryGetProperty("actionId", out _));
            Assert.DoesNotContain("\"456\"", string.Join('\n', issued.Select(e => e.GetRawText())));
        }
    }

    [Fact(DisplayName = "Остаток порога сессии ограничивает JPEG прогона на границах и не меняет результат команды")]
    public async Task SessionThresholdBoundsRunFramesWithoutChangingOutcome()
    {
        using TemporaryCheckout checkout = new();
        AgentRunResult reference = await RunDockRoundtripAsync(checkout, "budget-reference");
        long[] sizes = FrameBytes(checkout, reference);
        Assert.Equal(3, sizes.Length);

        // Чуть ниже порога: допуск пройден, бюджет JPEG — один байт, ни один кадр не помещается.
        AgentRunResult justBelow = await RunDockRoundtripAsync(checkout, "budget-below", PaddedSession(checkout, "budget-below", 1));
        AssertBounded(checkout, justBelow, reference, rendered: 0, "frames_missing:3/3");
        Assert.DoesNotContain(justBelow.Debug!.IncompleteReasons, r => r.StartsWith("frames_disabled", StringComparison.Ordinal));

        // Достижение порога во время прогона: первый кадр помещается, следующие нет.
        long budget = sizes[0] + (sizes[1] / 2);
        AgentRunResult during = await RunDockRoundtripAsync(checkout, "budget-during", PaddedSession(checkout, "budget-during", budget));
        AssertBounded(checkout, during, reference, rendered: 1, "frames_missing:2/3");
        Assert.True(Directory.GetFiles(Path.Combine(checkout.Resolve(during.Debug!.RelativeRunDirectory), "frames"), "*.jpg")
            .Sum(f => new FileInfo(f).Length) <= budget);

        // Порог исчерпан до запуска: JPEG отключены, отчёт честно неполный.
        AgentRunResult exhausted = await RunDockRoundtripAsync(checkout, "budget-exhausted", PaddedSession(checkout, "budget-exhausted", 0));
        AssertBounded(checkout, exhausted, reference, rendered: 0, "frames_disabled:session_limit");
    }

    [Fact(DisplayName = "Параллельные прогоны одной сессии не превышают собственный бюджет JPEG и не меняют результат")]
    public async Task ParallelRunsStayWithinOwnFrameBudget()
    {
        using TemporaryCheckout checkout = new();
        AgentRunResult reference = await RunDockRoundtripAsync(checkout, "budget-parallel-reference");
        long[] sizes = FrameBytes(checkout, reference);
        long budget = sizes[0] + (sizes[1] / 2);
        AgentDebugSessionLimits limits = PaddedSession(checkout, "budget-parallel", budget);

        AgentRunResult[] runs = await Task.WhenAll(
            RunDockRoundtripAsync(checkout, "budget-parallel", limits),
            RunDockRoundtripAsync(checkout, "budget-parallel", limits));
        foreach (AgentRunResult run in runs)
        {
            string runDirectory = checkout.Resolve(run.Debug!.RelativeRunDirectory);
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")));
            long own = manifest.RootElement.GetProperty("settings").GetProperty("frameByteBudget").GetInt64();
            Assert.True(own <= budget);
            long used = Directory.GetFiles(Path.Combine(runDirectory, "frames"), "*.jpg").Sum(f => new FileInfo(f).Length);
            Assert.True(used <= own, $"JPEG {used} Б при бюджете {own} Б");
            Assert.Equal(reference.Envelope.ExitCode, run.Envelope.ExitCode);
            Assert.Equal(reference.Envelope.Effect, run.Envelope.Effect);
            AssertInventoryExact(runDirectory);
        }

        Assert.Equal(2, File.ReadAllLines(Path.Combine(checkout.Path, ".codex", "local", "reports", "budget-parallel", "runs.jsonl")).Length);
    }

    [Fact(DisplayName = "Аннотации JPEG не закрывают заливкой ROI и содержат только короткие ASCII-метки")]
    public void AnnotationsNeverOpaquelyCoverFrameAndStayShortAscii()
    {
        // Регрессия: сплошные панели шапки и подвала превращали область нижнего меню JPEG в чёрную полосу,
        // хотя распознавание видело исходный кадр. Подписи рисуются только штрихом, без заливки прямоугольником.
        AgentDebugFrameRecord record = new()
        {
            Sequence = 1,
            InstanceId = MuMuInstanceId.FromIndex("7"),
            Endpoint = new("explicit-host", 20312),
            CaptureStartedTimestamp = 1,
            CaptureStartedAtUtc = DateTimeOffset.UtcNow,
            Width = 3840,
            Height = 2160,
            PngBytes = 1,
            CommandMs = 1,
            CaptureMs = 1,
            CapturedAtMs = 1,
            FrameId = Guid.NewGuid(),
            Observation = new(new(Guid.NewGuid(), MuMuInstanceId.FromIndex("7"), new("explicit-host", 20312), DateTimeOffset.UtcNow, 1, 3840, 2160),
            [
                new("main.depot", "global-en-white", GameMatchStatus.Unique, new GamePixelRect(1008, 1986, 1422, 2106), null, 1, -1, 0.01, "Unique"),
                new("dock.header", "global-en-white", GameMatchStatus.Absent, null, null, 0.1, -1, 60, "Absent"),
            ]),
        };
        record.Marks.Add(new("action_source", "dock.open", "source=reused_classified_frame"));
        record.Marks.Add(new("postcondition", "dock.open", "postcondition=unchanged"));

        IReadOnlyList<Windows.NativeFrameAnnotation> annotations = AgentDebugAnnotations.Build(record);
        Assert.DoesNotContain(annotations, a => a.Kind == Windows.NativeAnnotationKind.FilledRectangle);
        Windows.NativeFrameAnnotation[] texts = [.. annotations.Where(a => a.Kind == Windows.NativeAnnotationKind.Text)];
        Assert.NotEmpty(texts);
        Assert.All(texts, t =>
        {
            Assert.True(t.Text!.Length <= 63);
            Assert.All(t.Text, c => Assert.InRange(c, ' ', '~'));
        });
        string[] joined = [.. texts.Select(t => t.Text!)];
        Assert.DoesNotContain(joined, t => t.Contains("not drawn", StringComparison.Ordinal) || t.Contains("legend:", StringComparison.Ordinal)
            || t.Contains("no new capture", StringComparison.Ordinal));
        Assert.All(AgentDebugAnnotations.Legend, line => Assert.Matches("[А-Яа-яЁё]", line));
    }

    private static async Task<AgentRunResult> RunDockRoundtripAsync(TemporaryCheckout checkout, string session,
        AgentDebugSessionLimits? limits = null)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("explicit-host", 20312);
        return await AgentRunner.RunWithDebugAsync(
            ["agent", "game-dock-roundtrip", "--target", "mumu:7", "--debug-report", session],
            debug =>
            {
                OperatorFixture fixture = CreateDebugFixture(debug, MuMuLifecycleTestContext.Instance("7"));
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
            limits,
            TestContext.Current.CancellationToken);
    }

    private static long[] FrameBytes(TemporaryCheckout checkout, AgentRunResult result)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(checkout.Resolve(result.Debug!.RelativeRunDirectory), "manifest.json")));
        return [.. manifest.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("bytes").GetInt64())];
    }

    /// <summary>Сессия с уже занятыми байтами; допуск оставляет прогону ровно <paramref name="remaining"/> байтов.</summary>
    private static AgentDebugSessionLimits PaddedSession(TemporaryCheckout checkout, string session, long remaining)
    {
        const long Occupied = 1000;
        string directory = Path.Combine(checkout.Path, ".codex", "local", "reports", session);
        _ = Directory.CreateDirectory(directory);
        string padding = Path.Combine(directory, "padding.bin");
        if (!File.Exists(padding))
        {
            File.WriteAllBytes(padding, new byte[Occupied]);
        }

        return new(Occupied + remaining, 20_000);
    }

    private static void AssertBounded(TemporaryCheckout checkout, AgentRunResult limited, AgentRunResult reference, int rendered, string reason)
    {
        Assert.Equal(reference.Envelope.ExitCode, limited.Envelope.ExitCode);
        Assert.Equal(reference.Envelope.Effect, limited.Envelope.Effect);
        Assert.Equal(reference.Envelope.Status, limited.Envelope.Status);
        Assert.Equal("incomplete", limited.Debug!.Status);
        Assert.Contains(reason, limited.Debug.IncompleteReasons);
        string runDirectory = checkout.Resolve(limited.Debug.RelativeRunDirectory);
        Assert.Equal(rendered, Directory.GetFiles(Path.Combine(runDirectory, "frames"), "*.jpg").Length);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")));
        JsonElement counters = manifest.RootElement.GetProperty("counters");
        Assert.Equal(3, counters.GetProperty("captured").GetInt32());
        Assert.Equal(rendered, counters.GetProperty("rendered").GetInt32());
        if (!reason.StartsWith("frames_disabled", StringComparison.Ordinal))
        {
            Assert.Equal(3 - rendered, counters.GetProperty("skippedReasons").GetProperty("session_limit").GetInt32());
        }

        AssertInventoryExact(runDirectory);
    }

    /// <summary>Каждая пара (path, bytes) инвентаря существует и равна итоговому размеру файла после закрытия отчёта.</summary>
    private static void AssertInventoryExact(string runDirectory)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")));
        JsonElement[] files = [.. manifest.RootElement.GetProperty("files").EnumerateArray()];
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.GetProperty("path").GetString() == "manifest.json");
        foreach (JsonElement file in files)
        {
            string path = Path.Combine(runDirectory, file.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path);
            Assert.Equal(new FileInfo(path).Length, file.GetProperty("bytes").GetInt64());
        }

        // Инвентарь полон: все файлы отчёта, кроме самого манифеста, перечислены.
        string[] actual = [.. Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(runDirectory, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => f != "manifest.json").Order(StringComparer.Ordinal)];
        Assert.Equal(actual, files.Select(f => f.GetProperty("path").GetString()!).Order(StringComparer.Ordinal));
    }

    private static WindowsProcessRequest Request(string[] arguments)
    {
        return new() { ExecutablePath = Path.Combine(Path.GetTempPath(), "adb.exe"), Arguments = arguments, Timeout = TimeSpan.FromSeconds(1) };
    }

    private sealed class ScriptedProcessRunner : IWindowsProcessRunner
    {
        internal bool FailNext { get; set; }

        public Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(ApplicationResult<WindowsProcessOutcome>.Failure(new()
                {
                    Code = ApplicationFailure.DebugReportUnavailable,
                    Message = "Процесс не запущен.",
                }));
            }

            return Task.FromResult(ApplicationResult<WindowsProcessOutcome>.Success(new()
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutputTruncated = false,
                StandardErrorTruncated = false,
            }));
        }
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

    [Fact(DisplayName = "Отчёт Debug Mode фиксирует envelope, замещённый при превышении лимита вывода")]
    public async Task ReportRecordsEnvelopeReplacedByOutputLimit()
    {
        using TemporaryCheckout checkout = new();
        string correlationId = Guid.NewGuid().ToString("N");
        AgentDebugRun run = AgentDebugRun.Create(DebugSession, correlationId, "status", null, ["status"], 1000,
            TimeProvider.System, checkout.Path).Value!;
        using (run)
        {
            AgentEnvelope oversized = AgentJson.Envelope(correlationId, "status", null,
                new(new string('x', AgentJson.MaximumOutputBytes), null));
            _ = await run.CompleteAsync(oversized);

            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.RunDirectory, "manifest.json")));
            Assert.Equal(AzurPilotExitCode.AgentOutputLimitExceeded, manifest.RootElement.GetProperty("command").GetProperty("exitCode").GetInt32());
            string indexLine = File.ReadAllLines(Path.Combine(run.SessionDirectory, "runs.jsonl")).Single();
            using JsonDocument index = JsonDocument.Parse(indexLine);
            Assert.Equal(AzurPilotExitCode.AgentOutputLimitExceeded, index.RootElement.GetProperty("exitCode").GetInt32());
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
