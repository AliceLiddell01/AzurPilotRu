using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.Game;

public sealed class GameUiActionRunnerTests
{
    private static readonly MuMuInstanceId Target = MuMuInstanceId.FromIndex("7");
    private static readonly AndroidEndpoint Endpoint = new("test-host", 16419);
    private static readonly GameUiAction Action = new("open", "source", "post", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(2));

    [Fact(DisplayName = "Игровой runner требует явные часы")]
    public void TimeProviderIsNotOptional()
    {
        var parameter = Assert.Single(
            typeof(GameUiActionRunner)
                .GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters()),
            static parameter => parameter.ParameterType == typeof(TimeProvider));

        Assert.False(parameter.IsOptional);
    }

    [Fact]
    public async Task SuccessRequiresOneTapAndIndependentPostcondition()
    {
        Fixture host = new();
        GameActionProgress progress = new();
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint, Action, progress, TestContext.Current.CancellationToken);
        Assert.Null(result.Failure);
        Assert.True(result.ChangeProven);
        Assert.True(progress.ChangeProven);
        Assert.Equal(1, host.Taps);
        Assert.Equal(2, host.Captures);
        Assert.Equal(2, host.MappingChecks);
        Assert.NotEqual(result.Before!.Frame.FrameId, result.After!.Frame.FrameId);
        Assert.Equal(new GameInputPoint(49, 39), result.Point);
        GameActionTiming timing = result.Timing!;
        Assert.Equal((2, 2, 1), (timing.Work.Captures, timing.Work.ViewportChecks, timing.Work.Taps));
        Assert.Null(timing.LastUnchangedFrameAtMs);
        Assert.True(timing.TapCompletedAtMs <= timing.ProofFrameAtMs && timing.ProofFrameAtMs <= timing.ElapsedMs);
    }

    [Fact]
    public async Task PreviousProofFrameIsSourceWithoutDuplicateCaptureButWithFreshChecks()
    {
        Fixture host = new();
        ApplicationResult<GameUiObservation> proof = await host.ObserveAsync(Target, Endpoint, ["source", "post"],
            TestContext.Current.CancellationToken);
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint, Action, new(),
            proof.Value, TestContext.Current.CancellationToken);
        Assert.True(result.ChangeProven);
        Assert.Same(proof.Value, result.Before);
        Assert.Equal(0, result.SourceObservations);
        Assert.Equal(2, host.Captures);
        Assert.Equal(2, host.MappingChecks);
        Assert.Equal(1, result.Timing!.Work.Captures);
    }

    [Fact]
    public async Task StaleProofFrameNeverAuthorizesTap()
    {
        Fixture host = new();
        ApplicationResult<GameUiObservation> proof = await host.ObserveAsync(Target, Endpoint, ["source", "post"],
            TestContext.Current.CancellationToken);
        host.Clock.Timestamp += 12_001;
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint, Action, new(),
            proof.Value, TestContext.Current.CancellationToken);
        Assert.Equal("observation_stale", result.Failure!.Details!["reason"]);
        Assert.Equal(0, host.Taps);
        Assert.Equal(1, host.Captures);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("ambiguous")]
    [InlineData("technical")]
    [InlineData("stale")]
    [InlineData("wrong-target")]
    [InlineData("wrong-endpoint")]
    [InlineData("portrait")]
    [InlineData("unsafe-point")]
    [InlineData("mapping")]
    [InlineData("already-post")]
    public async Task UnprovenPreconditionNeverSendsInput(string scenario)
    {
        Fixture host = new() { Scenario = scenario };
        GameActionProgress progress = new();
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint, Action, progress, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Failure);
        Assert.False(result.ChangeProven);
        Assert.False(progress.MutationAttempted);
        Assert.Equal(0, host.Taps);
        Assert.Equal(1, host.Captures);
    }

    [Theory]
    [InlineData("adb-failed")]
    [InlineData("capture-failed")]
    [InlineData("post-unknown")]
    [InlineData("replayed-frame")]
    [InlineData("post-wrong-target")]
    [InlineData("post-resized")]
    [InlineData("cancel-input")]
    public async Task FailedOrUnknownEffectNeverRepeatsTapOrClaimsNoEffect(string scenario)
    {
        Fixture host = new() { Scenario = scenario };
        GameActionProgress progress = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        host.Cancellation = cancellation;
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint, Action, progress, cancellation.Token);
        Assert.NotNull(result.Failure);
        Assert.True(result.MutationAttempted);
        Assert.True(progress.MutationAttempted);
        Assert.False(result.ChangeProven);
        Assert.False(progress.ChangeProven);
        Assert.Equal(1, host.Taps);
        Assert.InRange(host.Captures, 1, 2);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Timestamp = 100_000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp()
        {
            return Timestamp;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddMilliseconds(Timestamp);
        }
    }

    [Theory]
    [InlineData(GameMatchStatus.Unique)]
    [InlineData(GameMatchStatus.Ambiguous)]
    [InlineData(GameMatchStatus.TechnicalFailure)]
    [InlineData(GameMatchStatus.GeometryRejected)]
    public async Task ExcludedSelectionOrUnknownGuardPreventsTap(GameMatchStatus guard)
    {
        Fixture host = new() { GuardBefore = guard };
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint,
            Action with { ExcludedAssetIds = ["guard"] }, new(), TestContext.Current.CancellationToken);
        Assert.Equal("excluded_asset_present_or_unproven", result.Failure!.Details!["reason"]);
        Assert.Equal(0, host.Taps);
        Assert.Equal(0, host.MappingChecks);
        Assert.False(result.ChangeProven);
    }

    [Fact]
    public async Task SelectionGuardAfterTapDoesNotBecomeSuccessfulPostcondition()
    {
        Fixture host = new() { GuardBefore = GameMatchStatus.Absent, GuardAfter = GameMatchStatus.Unique };
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint,
            Action with { ExcludedAssetIds = ["guard"] }, new(), TestContext.Current.CancellationToken);
        Assert.Equal("excluded_asset_present_or_unproven", result.Failure!.Details!["reason"]);
        Assert.Equal(1, host.Taps);
        Assert.True(result.MutationAttempted);
        Assert.False(result.ChangeProven);
    }

    [Fact]
    public async Task SeparateTapAreaIsMappedInsideFrameAndNeverOntoAnchor()
    {
        // 1920x1080: логическая (100,100)-(200,140) округляется внутрь до (150,150)-(300,210).
        Fixture host = new() { ExpectedPoint = new(224, 179) };
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint,
            Action with { TapArea = new GameRect(100, 100, 200, 140) }, new(), TestContext.Current.CancellationToken);
        Assert.Null(result.Failure);
        Assert.Equal(1, host.Taps);
        Assert.Equal(new GameInputPoint(224, 179), result.Point);
    }

    [Fact]
    public async Task SeparateTapAreaOverlappingDetectedAnchorIsRejectedBeforeInput()
    {
        Fixture host = new() { SourceBounds = new(140, 140, 400, 400) };
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint,
            Action with { TapArea = new GameRect(100, 100, 200, 140) }, new(), TestContext.Current.CancellationToken);
        Assert.Equal("unsafe_action_region", result.Failure!.Details!["reason"]);
        Assert.Equal(0, host.Taps);
        Assert.Equal(0, host.MappingChecks);
    }

    [Theory]
    [InlineData(GameMatchStatus.Absent)]
    [InlineData(GameMatchStatus.Ambiguous)]
    public async Task UnprovenRequiredContextNeverUsesSeparateTapArea(GameMatchStatus context)
    {
        Fixture host = new() { RequiredBefore = context };
        GameActionOutcome result = await new GameUiActionRunner(host, host.Clock).RunAsync(Target, Endpoint,
            Action with { TapArea = new GameRect(100, 100, 200, 140), RequiredAssetIds = ["context"] }, new(),
            TestContext.Current.CancellationToken);
        Assert.Equal("required_asset_unproven", result.Failure!.Details!["reason"]);
        Assert.Equal(0, host.Taps);
    }

    private sealed class Fixture : IGameUiHost
    {
        internal Clock Clock { get; } = new();
        internal string Scenario { get; init; } = "success";
        internal GameMatchStatus? GuardBefore { get; init; }
        internal GameMatchStatus? GuardAfter { get; init; }
        internal int Taps { get; private set; }
        internal int Captures { get; private set; }
        internal int MappingChecks { get; private set; }
        internal CancellationTokenSource? Cancellation { get; set; }
        internal GameInputPoint ExpectedPoint { get; init; } = new(49, 39);
        internal GamePixelRect SourceBounds { get; init; } = new(10, 10, 100, 80);
        internal GameMatchStatus? RequiredBefore { get; init; }
        private GameUiObservation? _before;

        public Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
            IReadOnlyList<string> assets, CancellationToken cancellationToken)
        {
            Assert.Equal(Target, instanceId);
            Assert.Equal(Endpoint, endpoint);
            Assert.Equal((GuardBefore is not null || GuardAfter is not null ? 3 : 2) + (RequiredBefore is not null ? 1 : 0), assets.Count);
            Assert.Equal("source", assets[0]);
            Assert.Equal("post", assets[1]);
            Captures++;
            Clock.Timestamp += 1;
            bool post = Captures > 1;
            if (post && Scenario == "capture-failed")
            {
                return Task.FromResult(ApplicationResult<GameUiObservation>.Failure(GameUiActionRunner.Failure("capture_failed", true)));
            }

            GameFrameProvenance frame = new(Guid.NewGuid(), Target, Endpoint, Clock.GetUtcNow(), Clock.GetTimestamp(), 1920, 1080);
            if (!post)
            {
                frame = Scenario switch
                {
                    "wrong-target" => frame with { InstanceId = MuMuInstanceId.FromIndex("8") },
                    "wrong-endpoint" => frame with { Endpoint = new("other-host", 16419) },
                    "stale" => frame with { CaptureStartedTimestamp = Clock.Timestamp - 12_001 },
                    "portrait" => frame with { Width = 1080, Height = 1920 },
                    _ => frame,
                };
            }
            else
            {
                if (Scenario == "replayed-frame")
                {
                    return Task.FromResult(ApplicationResult<GameUiObservation>.Success(_before!));
                }

                if (Scenario == "post-wrong-target")
                {
                    frame = frame with { InstanceId = MuMuInstanceId.FromIndex("8") };
                }

                if (Scenario == "post-resized")
                {
                    frame = frame with { Width = 3840, Height = 2160 };
                }

                if (Scenario == "post-unknown")
                {
                    Clock.Timestamp += 3000;
                }
            }

            GameMatchStatus source = post ? GameMatchStatus.Absent : Scenario switch
            {
                "absent" => GameMatchStatus.Absent,
                "ambiguous" => GameMatchStatus.Ambiguous,
                "technical" => GameMatchStatus.TechnicalFailure,
                _ => GameMatchStatus.Unique,
            };
            GamePixelRect safe = Scenario == "unsafe-point" ? new(0, 0, 1921, 20) : new(20, 20, 80, 60);
            GameUiObservation observation = new(frame,
                [new("source", "test", source, SourceBounds, safe, 1, 0, 0, "test"),
                new("post", "test", (post && Scenario != "post-unknown") || Scenario == "already-post" ? GameMatchStatus.Unique : GameMatchStatus.Absent,
                    null, null, 1, 0, 0, "test")]);
            if (GuardBefore is not null || GuardAfter is not null)
            {
                observation = observation with
                {
                    Matches = [.. observation.Matches,
                    new("guard", "test", (post ? GuardAfter : GuardBefore) ?? GameMatchStatus.Absent, null, null, 1, 0, 0, "test")]
                };
            }

            if (RequiredBefore is not null)
            {
                observation = observation with
                {
                    Matches = [.. observation.Matches, new("context", "test", RequiredBefore.Value, null, null, 1, 0, 0, "test")]
                };
            }

            _before ??= observation;
            return Task.FromResult(ApplicationResult<GameUiObservation>.Success(observation));
        }

        public Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
        {
            MappingChecks++;
            return Task.FromResult(ApplicationResult<bool>.Success(Scenario != "mapping"));
        }

        public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
        {
            Assert.Equal(Endpoint, tap.Frame.Endpoint);
            Assert.Equal(ExpectedPoint, tap.Point);
            Assert.True(tap.SafeBounds.Contains(tap.Point));
            Assert.True(tap.TryConsume());
            Assert.False(tap.TryConsume());
            Taps++;
            if (Scenario == "cancel-input")
            {
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(ApplicationResult<AndroidCommandOutcome>.Success(new(Scenario == "adb-failed" ? 1 : 0, "")));
        }
    }
}
