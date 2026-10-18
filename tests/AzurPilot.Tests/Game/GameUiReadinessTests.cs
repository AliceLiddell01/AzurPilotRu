using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Windows;
using AzurPilot.Windows.Game;
using Xunit;

namespace AzurPilot.Tests.Game;

[Collection(InteropCollection.Name)]
public sealed class GameUiReadinessTests
{
    [Fact]
    public async Task ReadyRequiresIndependentStableFramesAndNoInput()
    {
        using ReadyHost host = new("main");
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(false, progress);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Null(result.Failure);
        Assert.InRange(result.StableFrames, 3, 5);
        Assert.Equal(result.Observations, result.Evidence.Select(e => e.FrameId).Distinct().Count());
        Assert.Equal(result.Observations, host.Viewports);
        Assert.True(result.Elapsed >= TimeSpan.FromSeconds(1.5));
        Assert.NotEqual(result.FirstReadyFrame!.FrameId, result.LastObservation!.Frame.FrameId);
        Assert.Equal(0, result.TapAttempts);
        Assert.False(progress.MutationAttempted);
    }

    [Theory]
    [InlineData("login", 2)]
    [InlineData("download-confirm", 1)]
    [InlineData("dock", 1)]
    public async Task EnsureUsesBoundedKnownTransitionsThenIndependentReady(string initial, int taps)
    {
        using ReadyHost host = new(initial) { AdvanceOnTap = true };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Null(result.Failure);
        Assert.Equal(taps, host.Taps);
        Assert.Equal(taps, result.TapAttempts);
        Assert.True(progress.ChangeProven);
        Assert.All(result.Actions, a => Assert.True(a.ChangeProven));
        Assert.DoesNotContain(result.Actions, a => a.After!.Frame.FrameId == result.LastObservation!.Frame.FrameId);
        Assert.True(result.StableFrames >= 3);
    }

    [Theory]
    [InlineData("login", GameUiReadinessState.AwaitingInput)]
    [InlineData("server", GameUiReadinessState.AwaitingInput)]
    [InlineData("download-confirm", GameUiReadinessState.AwaitingInput)]
    [InlineData("download", GameUiReadinessState.Loading)]
    [InlineData("login-transition", GameUiReadinessState.Transition)]
    [InlineData("selection", GameUiReadinessState.RequiresUserAction)]
    [InlineData("unverified-update", GameUiReadinessState.RequiresUserAction)]
    [InlineData("main-overlay", GameUiReadinessState.Unknown)]
    [InlineData("main-unknown-overlay", GameUiReadinessState.Unknown)]
    public async Task ReadOnlyDistinguishesStatesWithoutMutation(string initial, GameUiReadinessState expected)
    {
        using ReadyHost host = new(initial);
        GameUiReadinessOutcome result = await host.Run(false, new());
        Assert.Equal(expected, result.State);
        Assert.Equal(0, host.Taps);
        Assert.Equal(1, host.Captures);
    }

    [Theory]
    [InlineData("wrong-target")]
    [InlineData("wrong-endpoint")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("replayed-id")]
    [InlineData("replayed-time")]
    [InlineData("geometry")]
    [InlineData("resized")]
    [InlineData("viewport")]
    [InlineData("native")]
    [InlineData("transport")]
    public async Task InvalidEvidenceNeverProvesReadyOrTaps(string fault)
    {
        using ReadyHost host = new("main") { Fault = fault };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.NotNull(result.Failure);
        Assert.Equal(0, host.Taps);
    }

    [Theory]
    [InlineData(GameMatchStatus.Ambiguous)]
    [InlineData(GameMatchStatus.TechnicalFailure)]
    [InlineData(GameMatchStatus.GeometryRejected)]
    public async Task UnknownNegativeGuardNeverAuthorizesConfirm(GameMatchStatus status)
    {
        using ReadyHost host = new("download-confirm") { Guard = status };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(0, host.Taps);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData(false, "loading_no_visual_activity")]
    [InlineData(true, "readiness_deadline")]
    public async Task PassiveLoadingIsBoundedAndNeverUsesTap(bool activity, string reason)
    {
        using ReadyHost host = new("download") { Activity = activity };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Loading, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.InRange(result.Evidence.Count, 1, 16);
        Assert.InRange(result.Observations, 2, 160);
        Assert.Equal(0, result.TapAttempts);
        Assert.Empty(result.Actions);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task IntermittentMainAndUnknownNeverAccumulateReadyFrames()
    {
        using ReadyHost host = new("main") { Fault = "unstable" };
        GameUiReadinessOutcome result = await host.Run(false, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(0, result.StableFrames);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task RevisitedActionStopsBeforeSecondTap()
    {
        using ReadyHost host = new("login") { AdvanceOnTap = true, Fault = "cycle" };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal("action_cycle_prevented", result.Reason);
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(1, host.Taps);
        Assert.Equal(1, result.TapAttempts);
        Assert.True(progress.ChangeProven);
    }

    [Fact]
    public async Task ConfirmRechecksPurposeAfterClassificationBeforeInput()
    {
        using ReadyHost host = new("download-confirm") { Fault = "purpose-changed" };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("required_asset_unproven", Assert.Single(result.Actions).Failure!.Details!["reason"]);
        Assert.Equal(0, host.Taps);
        Assert.Equal(0, result.TapAttempts);
    }

    [Fact]
    public async Task MissingGuardIsUnknownAndNeverMeansAbsent()
    {
        using ReadyHost host = new("download-confirm") { Fault = "guard-missing" };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal(0, host.Taps);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task ColdInitialUnknownWaitsForFreshKnownUiWithoutGuessingOrTap()
    {
        using ReadyHost host = new("main") { InitialUnknownFrames = 40 };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.True(result.Elapsed > TimeSpan.FromSeconds(20));
        Assert.True(result.Observations > 40);
        Assert.Equal(0, host.Taps);
        Assert.True(result.StableFrames >= 3);
    }

    [Fact]
    public async Task InitialUnknownBudgetDoesNotExtendUnknownAfterKnownState()
    {
        using ReadyHost host = new("main") { Fault = "unknown-after-known" };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal("state_unproven_or_no_progress", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 12, 15);
        Assert.Equal(0, host.Taps);
    }

    [Theory]
    [InlineData("no-change")]
    [InlineData("tap-failure")]
    [InlineData("cancel-tap")]
    [InlineData("post-transport")]
    public async Task UnprovenEffectPreservesAttemptAndNeverRetries(string fault)
    {
        using ReadyHost host = new("login") { Fault = fault, AdvanceOnTap = true };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.NotNull(result.Failure);
        Assert.True(progress.MutationAttempted);
        Assert.False(progress.ChangeProven);
        Assert.Equal(1, result.TapAttempts);
        Assert.Equal(1, host.Taps);
    }

    [Theory]
    [InlineData(720, "main", "main")]
    [InlineData(1080, "login", "login")]
    [InlineData(1440, "server", "server")]
    [InlineData(2160, "download-confirm", "download-confirm")]
    [InlineData(2160, "main-overlay", null)]
    [InlineData(2160, "main-unknown-overlay", null)]
    [InlineData(2160, "download", "download")]
    [InlineData(2160, "login-transition", "login-transition")]
    [InlineData(2160, "unverified-update", "unverified-update")]
    public void NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(int height, string fixture, string? expected)
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", $"{fixture}-{height}.png")));
        GameUiReadinessPolicy policy = GameAssetCatalog.Default.Readiness;
        GameUiObservation observed = new GameUiRecognizer(GameAssetCatalog.Default).Observe(frame,
            GameUiRecognizerTests.Provenance(frame), policy.AssetIds);
        Assert.Equal(expected, policy.Classify(observed)?.Id);
        if (fixture.StartsWith("main-", StringComparison.Ordinal))
        {
            Assert.Equal(GameMatchStatus.Unique, observed.Find("main.dock")!.Status);
            Assert.Equal(GameMatchStatus.Unique, observed.Find("main.depot")!.Status);
            Assert.NotEqual(GameMatchStatus.Unique, observed.Find("main.clear-center")!.Status);
        }
    }

    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void EveryDataDrivenFixtureHasSameMeaningAtSyntheticScales(int height)
    {
        foreach ((string fixture, string? expected) in new (string, string?)[]
        {
            ("main", "main"), ("dock", "dock"), ("selection", "selection"), ("login", "login"),
            ("server", "server"), ("download", "download"), ("download-confirm", "download-confirm"),
            ("login-transition", "login-transition"), ("main-overlay", null), ("main-unknown-overlay", null),
            ("unverified-update", "unverified-update"),
        })
        {
            NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, fixture, expected);
        }
    }

    internal sealed class ReadyClock : TimeProvider
    {
        internal long Now = TimeSpan.TicksPerDay;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            return Now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(Now);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime <= TimeSpan.FromSeconds(1) && dueTime >= TimeSpan.Zero)
            {
                Now += dueTime.Ticks;
                _ = ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                return true;
            }

            public void Dispose() { }
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class ReadyHost(string initial) : IGameUiHost, IDisposable
    {
        internal ReadyClock Clock { get; } = new();
        internal bool AdvanceOnTap { get; init; }
        internal bool Activity { get; init; }
        internal string Fault { get; init; } = "";
        internal GameMatchStatus? Guard { get; init; }
        internal int Captures { get; private set; }
        internal int Viewports { get; private set; }
        internal int Taps { get; private set; }
        internal int InitialUnknownFrames { get; init; }
        private Guid _firstId;
        private long _firstTime;
        private readonly CancellationTokenSource _cancel = new();
        public void Dispose() => _cancel.Dispose();
        internal MuMuInstanceId Target { get; } = MuMuInstanceId.FromIndex("7");
        internal AndroidEndpoint Endpoint { get; } = AndroidTestContext.Endpoint;
        internal Action? OnCapture { get; set; }

        internal async Task<GameUiReadinessOutcome> Run(bool ensure, GameActionProgress progress, bool allowInitialTransition = false)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, TestContext.Current.CancellationToken);
            return await new GameUiReadinessService(this, GameAssetCatalog.Default.Readiness,
                GameAssetCatalog.Default.GetAction, Clock).RunAsync(Target, Endpoint, ensure, progress, linked.Token, allowInitialTransition);
        }

        public Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
            IReadOnlyList<string> assets, CancellationToken cancellationToken)
        {
            Assert.Equal(Target, instanceId);
            Assert.Equal(Endpoint, endpoint);
            OnCapture?.Invoke();
            Captures++;
            Clock.Now += TimeSpan.TicksPerMillisecond;
            if (Fault == "transport" || (Fault == "post-transport" && Taps > 0))
            {
                return Task.FromResult(ApplicationResult<GameUiObservation>.Failure(GameUiActionRunner.Failure("transport_lost")));
            }

            string state = AdvanceOnTap && Fault is not ("no-change" or "tap-failure") && Taps > 0
                ? initial == "login" && Taps == 1 ? "server"
                    : initial == "download-confirm" && Captures == 3 ? "download" : "main" : initial;
            if (Fault == "unstable" && Captures % 2 == 0)
            {
                state = "main-unknown-overlay";
            }

            if (Fault == "cycle" && Taps > 0 && Captures >= 4)
            {
                state = "login";
            }

            if (Captures <= InitialUnknownFrames || (Fault == "unknown-after-known" && Captures > 1))
            {
                state = "unknown";
            }

            string[] present = state switch
            {
                "main" => ["main.dock", "main.depot", "main.battle", "main.clear-center", "main.clear-left"],
                "main-overlay" => ["main.dock", "main.depot", "main.battle", "main.clear-left", "update.header", "update.download", "update.confirm"],
                "main-unknown-overlay" => ["main.dock", "main.depot", "main.battle", "main.clear-left"],
                "login" => ["login.enter", "login.logo", "login.start"],
                "server" => ["login.server", "login.logo", "login.start"],
                "login-transition" => ["login.success", "login.server", "login.logo", "login.start"],
                "download-confirm" => ["update.header", "update.download", "update.confirm"],
                "unverified-update" => ["update.header", "update.confirm"],
                "download" => ["loading.download"],
                "dock" => ["dock.header"],
                "selection" => ["dock.header", "dock.selection-confirm"],
                _ => [],
            };
            Guid id = Guid.NewGuid();
            long timestamp = Clock.Now;
            if (Captures == 1) { _firstId = id; _firstTime = timestamp; }

            if (Fault == "replayed-id" && Captures > 1)
            {
                id = _firstId;
            }

            if (Fault == "replayed-time" && Captures > 1)
            {
                timestamp = _firstTime;
            }

            if (Fault == "stale")
            {
                timestamp -= TimeSpan.FromSeconds(13).Ticks;
            }

            GameFrameProvenance frame = new(id, Fault == "wrong-target" ? MuMuInstanceId.FromIndex("8") : Target,
                Fault == "wrong-endpoint" ? new("other-host", 20312) : Endpoint,
                Clock.GetUtcNow().AddSeconds(Fault == "future" ? 1 : 0), timestamp,
                Fault == "geometry" ? 1000 : Fault == "resized" && Captures > 1 ? 1280 : 3840,
                Fault == "resized" && Captures > 1 ? 720 : 2160);
            GameUiObservation observation = new(frame, [.. assets.Where(id => Fault != "guard-missing" || id != "dock.selection-confirm")
                .Select(id => new GameUiMatch(id, "test",
                Fault == "native" ? GameMatchStatus.TechnicalFailure : id == "dock.selection-confirm" && Guard is not null
                    ? Guard.Value : Fault == "purpose-changed" && Captures > 1 && id == "update.download" ? GameMatchStatus.Absent
                        : present.Contains(id, StringComparer.Ordinal) ? GameMatchStatus.Unique : GameMatchStatus.Absent,
                new(10, 10, 100, 80), new(20, 20, 80, 60), 1, 0, 0, "test"))])
            { VisualActivity = Activity ? Captures * 0.1 : 1 };
            return Task.FromResult(ApplicationResult<GameUiObservation>.Success(observation));
        }

        public Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
        {
            Viewports++;
            return Task.FromResult(ApplicationResult<bool>.Success(Fault != "viewport"));
        }

        public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
        {
            Assert.True(tap.TryConsume());
            Assert.False(tap.TryConsume());
            Taps++;
            if (Fault == "cancel-tap")
            {
                _cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(ApplicationResult<AndroidCommandOutcome>.Success(new(Fault == "tap-failure" ? 1 : 0, "")));
        }
    }
}
