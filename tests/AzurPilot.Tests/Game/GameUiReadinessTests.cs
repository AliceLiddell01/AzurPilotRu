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
        Assert.True(result.StableFrames >= 3);
        Assert.Equal(result.Observations, result.Evidence.Select(e => e.FrameId).Distinct().Count());
        Assert.All(result.Evidence, e => Assert.True(e.ViewportProven));
        Assert.Equal(result.Observations, host.Viewports);
        Assert.True(host.Clock.GetElapsedTime(result.FirstReadyFrame!.CaptureStartedTimestamp,
            result.LastObservation!.Frame.CaptureStartedTimestamp) >= TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, result.TapAttempts);
        Assert.False(progress.MutationAttempted);
    }

    [Theory]
    [InlineData("login", new[] { "login.enter", "login.start" })]
    [InlineData("download-confirm", new[] { "update.download" })]
    [InlineData("dock", new[] { "dock.close" })]
    public async Task EnsureUsesBoundedKnownTransitionsThenIndependentReady(string initial, string[] expectedActions)
    {
        using ReadyHost host = new(initial);
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Null(result.Failure);
        Assert.Equal(expectedActions, result.Actions.Select(a => a.ActionId));
        Assert.Equal(expectedActions.Length, host.Taps);
        Assert.Equal(expectedActions.Length, result.TapAttempts);
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
    [InlineData("loading", GameUiReadinessState.Loading)]
    [InlineData("login-transition", GameUiReadinessState.Transition)]
    [InlineData("selection", GameUiReadinessState.RequiresUserAction)]
    [InlineData("unverified-update", GameUiReadinessState.RequiresUserAction)]
    [InlineData("main-overlay", GameUiReadinessState.Unknown)]
    [InlineData("main-unknown-overlay", GameUiReadinessState.RequiresUserAction)]
    [InlineData("main-sidebar-overlay", GameUiReadinessState.RequiresUserAction)]
    [InlineData("login-and-server", GameUiReadinessState.Unknown)]
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
        using ReadyHost host = new("download-confirm") { GuardAssetId = "dock.selection-confirm", Guard = status };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(0, host.Taps);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData("loading", null)]
    [InlineData("loading", "unknown")]
    [InlineData("download", null)]
    public async Task StartupOrUnconfirmedLoadingIsPassiveAndBoundedWithoutProgress(string initial, string? flicker)
    {
        // Полная полоса обычного входа, рост полосы без подтверждённого Confirm и мерцание распознавания
        // не продлевают срок, отсчитанный от первого входа в загрузку.
        using ReadyHost host = new(initial) { Progress = "endless", Flicker = flicker };
        _ = host.Timed.Remove("download");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Loading, result.State);
        Assert.Equal("loading_unfinished", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 120, 125);
        Assert.Equal(0, host.Taps);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task StartupLoadingWaitsPassivelyUntilIndependentReady()
    {
        using ReadyHost host = new("loading") { Timed = { ["loading"] = (TimeSpan.FromSeconds(90), "main") } };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.True(result.Elapsed > TimeSpan.FromSeconds(90));
        Assert.Equal(0, host.Taps);
        Assert.Contains(result.Evidence, e => e.StateId == "loading" && e.ProgressLevel is null);
    }

    [Fact]
    public async Task RecognizedStartupLoadingKeepsInitialTransitionForFollowingUnknownScreen()
    {
        // Неизвестный экран между стартовой загрузкой и логином остаётся частью стартового перехода.
        using ReadyHost host = new("loading")
        {
            Timed = { ["loading"] = (TimeSpan.FromSeconds(10), "unknown"), ["unknown"] = (TimeSpan.FromSeconds(30), "main") },
        };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Equal(0, host.Taps);
    }

    [Theory]
    [InlineData("stalled", "loading_unfinished")]
    [InlineData("oscillating", "loading_no_progress")]
    [InlineData("reset", "loading_no_progress")]
    public async Task ConfirmedDownloadIgnoresFluctuationAndResetWithoutNewMaximum(string progress, string reason)
    {
        // Колебания выше прежнего порога, циклы снижения/роста и сброс к нулю дают не больше одного нового максимума.
        using ReadyHost host = new("download-confirm") { Progress = progress };
        _ = host.Timed.Remove("download");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Loading, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 120, 135);
        Assert.Equal(1, host.Taps);
        Assert.InRange(result.Evidence.Count, 1, 16);
    }

    [Fact]
    public async Task DownloadAfterLoginActionIsAlternativeOutcomeAndStaysPassive()
    {
        // Скачивание как альтернативный исход входа не является подтверждённым Confirm и не продлевается ростом полосы.
        using ReadyHost host = new("login") { Progress = "endless" };
        host.OnTap["login"] = "download";
        _ = host.Timed.Remove("download");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("loading_unfinished", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 120, 135);
        Assert.Equal(1, host.Taps);
        Assert.Equal("login.enter", Assert.Single(result.Actions).ActionId);
    }

    [Fact]
    public async Task ConfirmedDownloadGrowthExtendsWaitUntilIndependentReady()
    {
        using ReadyHost host = new("download-confirm") { Progress = "slow", Timed = { ["download"] = (TimeSpan.FromSeconds(200), "main") } };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.True(result.Elapsed > TimeSpan.FromSeconds(200));
        Assert.Equal(1, host.Taps);
        Assert.Equal("update.download", Assert.Single(result.Actions).ActionId);
    }

    [Fact]
    public async Task EndlessConfirmedGrowthStopsAtServiceLimit()
    {
        using ReadyHost host = new("download-confirm") { Progress = "endless" };
        _ = host.Timed.Remove("download");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("readiness_maximum_duration", result.Reason);
        Assert.Equal(1, host.Taps);
    }

    [Fact]
    public async Task UnknownFrameRevokesConfirmedDownloadContext()
    {
        // После любого кадра вне загрузки рост полосы больше не продлевает ожидание.
        using ReadyHost host = new("download-confirm") { Progress = "slow", Flicker = "unknown" };
        _ = host.Timed.Remove("download");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("loading_unfinished", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 120, 135);
        Assert.Equal(1, host.Taps);
    }

    [Fact]
    public async Task PassiveFramesSkipViewportButEveryReadyCandidateProvesIt()
    {
        using ReadyHost host = new("download") { Progress = "slow", Timed = { ["download"] = (TimeSpan.FromSeconds(10), "main") } };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Contains(result.Evidence, e => e.State == GameUiReadinessState.Loading && !e.ViewportProven);
        Assert.All(result.Evidence.Where(e => e.State == GameUiReadinessState.Ready), e => Assert.True(e.ViewportProven));
        Assert.Equal(result.Evidence.Count(e => e.State == GameUiReadinessState.Ready), host.Viewports);
    }

    [Theory]
    [InlineData(2000, 0)]
    [InlineData(100, 1)]
    public async Task SlowCaptureIsNotFollowedByExtraPause(int captureMilliseconds, int minimumWaits)
    {
        using ReadyHost host = new("main") { CaptureCost = TimeSpan.FromMilliseconds(captureMilliseconds) };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Equal(minimumWaits == 0 ? 0 : result.Work!.Waits, result.Waits);
        Assert.True(result.Waits >= minimumWaits);
        IReadOnlyList<long> captures = [.. result.Evidence.Select(e => e.CaptureTimestamp)];
        Assert.All(captures.Zip(captures.Skip(1)), p => Assert.True(host.Clock.GetElapsedTime(p.First, p.Second) >= TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task IntermittentMainAndUnknownNeverAccumulateReadyFrames()
    {
        using ReadyHost host = new("main") { Flicker = "main-unknown-overlay" };
        GameUiReadinessOutcome result = await host.Run(false, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(0, result.StableFrames);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task GeometryRejectedAfterLoadingDoesNotReportStaleLoadingState()
    {
        using ReadyHost host = new("loading") { Fault = "late-geometry" };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("geometry_rejected", result.Reason);
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
    }

    [Fact]
    public async Task FlickeringUnknownScreenDoesNotRestartInactivityLimit()
    {
        // Чередование Unknown и распознанного перехода не должно перезапускать срок бездействия при каждой смене.
        using ReadyHost host = new("unknown") { Flicker = "login-transition" };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal("state_unproven_or_no_progress", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 12, 20);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task RevisitedActionStopsBeforeSecondTap()
    {
        using ReadyHost host = new("login") { Timed = { ["server"] = (TimeSpan.FromSeconds(1), "login") } };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal("action_cycle_prevented", result.Reason);
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Equal(1, host.Taps);
        Assert.Equal(1, result.TapAttempts);
        Assert.True(progress.ChangeProven);
    }

    [Theory]
    [InlineData("login", "login.enter")]
    [InlineData("server", "login.start")]
    [InlineData("download-confirm", "update.download")]
    [InlineData("dock", "dock.close")]
    public async Task ActionUsesClassifiedFrameWithFreshViewportInsteadOfDuplicateCapture(string initial, string actionId)
    {
        using ReadyHost host = new(initial);
        GameUiReadinessOutcome result = await host.Run(true, new());
        GameActionOutcome action = result.Actions[0];
        Assert.Equal(actionId, action.ActionId);
        Assert.Equal(0, action.SourceObservations);
        Assert.Contains(result.Evidence, e => e.FrameId == action.Before!.Frame.FrameId && !e.ViewportProven);
        Assert.Equal(action.Before!.Frame.FrameId, host.FirstViewportFrame);
        Assert.True(action.ChangeProven);
    }

    [Fact]
    public async Task MissingGuardIsUnknownAndNeverMeansAbsent()
    {
        using ReadyHost host = new("download-confirm") { GuardAssetId = "dock.selection-confirm", MissingGuard = true };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal(0, host.Taps);
        Assert.Empty(result.Actions);
    }

    [Theory]
    [InlineData("overlay.dialog-close", null)]
    [InlineData("overlay.dialog-close", GameMatchStatus.Ambiguous)]
    [InlineData("overlay.dialog-close", GameMatchStatus.TechnicalFailure)]
    [InlineData("overlay.dialog-close", GameMatchStatus.GeometryRejected)]
    [InlineData("overlay.sidebar-open", null)]
    [InlineData("overlay.sidebar-open", GameMatchStatus.Ambiguous)]
    [InlineData("overlay.sidebar-open", GameMatchStatus.TechnicalFailure)]
    [InlineData("overlay.sidebar-open", GameMatchStatus.GeometryRejected)]
    public async Task OverlayGuardUncertaintyNeverProvesReadyOrTaps(string asset, GameMatchStatus? status)
    {
        using ReadyHost host = new("main") { GuardAssetId = asset, Guard = status, MissingGuard = status is null };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.NotNull(result.Failure);
        Assert.Equal(0, result.TapAttempts);
        Assert.Empty(result.Actions);
    }

    [Theory]
    [InlineData("main.dock", null)]
    [InlineData("main.dock", GameMatchStatus.Absent)]
    [InlineData("main.depot", null)]
    [InlineData("main.depot", GameMatchStatus.Absent)]
    [InlineData("main.battle", null)]
    [InlineData("main.battle", GameMatchStatus.Absent)]
    public async Task StableMainRequiresEveryIndependentAnchor(string asset, GameMatchStatus? status)
    {
        using ReadyHost host = new("main") { GuardAssetId = asset, Guard = status, MissingGuard = status is null };
        GameUiReadinessOutcome result = await host.Run(false, new());
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal(0, result.StableFrames);
        Assert.Equal(0, host.Taps);
    }

    [Theory]
    [InlineData("login-dialog")]
    [InlineData("server-dialog")]
    [InlineData("dock-dialog")]
    public async Task DialogOverActionScreenNeverAuthorizesTap(string initial)
    {
        using ReadyHost host = new(initial);
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Empty(result.Actions);
        Assert.Equal(0, host.Taps);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("server")]
    [InlineData("dock")]
    public async Task DialogAppearingAfterTapFailsPostconditionWithoutSecondTap(string initial)
    {
        using ReadyHost host = new(initial) { DialogAfterTap = true };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal("excluded_asset_present_or_unproven", Assert.Single(result.Actions).Failure!.Details!["reason"]);
        Assert.Equal(1, host.Taps);
        Assert.True(progress.MutationAttempted);
        Assert.False(progress.ChangeProven);
    }

    [Fact]
    public async Task StartUsesStaticAnchorAndSeparateTapAreaOnlyOnServerStage()
    {
        using ReadyHost host = new("server");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        GameTap tap = Assert.Single(host.TapPermits);
        GameUiAction start = GameAssetCatalog.Default.GetAction("login.start");
        GamePixelRect area = new GameGeometry(3840, 2160).MapInside(start.TapArea!.Value);
        Assert.Equal("login.criware", tap.AssetId);
        Assert.Equal(area, tap.SafeBounds);
        Assert.True(area.Contains(tap.Point));
        GamePixelRect anchor = result.Actions[0].Before!.Find("login.criware")!.Bounds!.Value;
        Assert.False(anchor.Contains(tap.Point));
    }

    [Fact]
    public void CatalogActionsHaveExactlyOneInputRegionAndStartAvoidsVisibleContext()
    {
        GameAssetCatalog catalog = GameAssetCatalog.Default;
        foreach (GameUiAction action in catalog.Actions)
        {
            Assert.True(action.TapArea is null ^ catalog.GetAsset(action.SourceAssetId).SafeTapOffset is null, action.Id);
        }

        GameUiAction start = catalog.GetAction("login.start");
        Assert.Equal("login.criware", start.SourceAssetId);
        Assert.Contains("login.server", start.RequiredAssetIds!);
        Assert.Contains("login.enter", start.ExcludedAssetIds!);
        Assert.All(start.RequiredAssetIds!.Append(start.SourceAssetId),
            id => Assert.False(GameGeometry.Intersects(start.TapArea!.Value, catalog.GetAsset(id).Search), id));
        Assert.DoesNotContain(catalog.Assets, a => a.Id == "login.start");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GameMatchStatus.Absent)]
    [InlineData(GameMatchStatus.Ambiguous)]
    public async Task StaticAnchorVisibleOnLoginScreenNeverAuthorizesStart(GameMatchStatus? enter)
    {
        // CRIWARE виден и на экране LOG IN: без доказанной стадии SERVER и отсутствия LOG IN нет tap по отдельной области.
        using ReadyHost host = new("login") { GuardAssetId = "login.enter", Guard = enter, MissingGuard = enter is null };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.Empty(result.Actions);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task LoginAndServerTogetherNeverAuthorizeEitherTap()
    {
        using ReadyHost host = new("login-and-server");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Empty(result.Actions);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task ColdInitialUnknownWaitsForFreshKnownUiWithoutGuessingOrTap()
    {
        using ReadyHost host = new("main") { InitialUnknown = TimeSpan.FromSeconds(60) };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.True(result.Elapsed > TimeSpan.FromSeconds(60));
        Assert.Equal(0, host.Taps);
        Assert.True(result.StableFrames >= 3);
    }

    [Fact]
    public async Task InitialUnknownIsBoundedSeparatelyFromCallerDeadline()
    {
        using ReadyHost host = new("main") { InitialUnknown = TimeSpan.FromMinutes(10) };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal("initial_transition_unproven", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 120, 125);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task InitialUnknownBudgetDoesNotExtendUnknownAfterKnownState()
    {
        using ReadyHost host = new("main") { Timed = { ["main"] = (TimeSpan.FromSeconds(1), "unknown") } };
        GameUiReadinessOutcome result = await host.Run(true, new(), allowInitialTransition: true);
        Assert.Equal(GameUiReadinessState.Unknown, result.State);
        Assert.Equal("state_unproven_or_no_progress", result.Reason);
        Assert.InRange(result.Elapsed.TotalSeconds, 12, 16);
        Assert.Equal(0, host.Taps);
    }

    [Fact]
    public async Task LoginToastAfterTapIsExpectedTransitionAndNeverProvesReady()
    {
        using ReadyHost host = new("login")
        {
            OnTap = { ["login"] = "login-transition" },
            Timed = { ["login-transition"] = (TimeSpan.FromSeconds(3), "server") },
        };
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        Assert.Equal(2, host.Taps);
        Assert.Equal(GameMatchStatus.Unique, result.Actions[0].After!.Find("login.success")!.Status);
        Assert.True(result.Actions[0].ChangeProven);
        Assert.Contains(result.Evidence, e => e.StateId == "login-transition" && e.State == GameUiReadinessState.Transition);
        Assert.True(result.StableFrames >= 3);
        Assert.Equal(GameMatchStatus.Absent, result.LastObservation!.Find("login.success")!.Status);
    }

    [Fact]
    public async Task CancelBeforeStartTapKeepsZeroTapAttempts()
    {
        using ReadyHost host = new("server") { Fault = "cancel-viewport" };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.Failure!.Code);
        Assert.Equal(0, host.Taps);
        Assert.Equal(0, result.TapAttempts);
        Assert.False(progress.MutationAttempted);
        Assert.False(progress.ChangeProven);
    }

    [Theory]
    [InlineData("no-change")]
    [InlineData("tap-failure")]
    [InlineData("cancel-tap")]
    [InlineData("post-transport")]
    public async Task UnprovenEffectPreservesAttemptAndNeverRetries(string fault)
    {
        using ReadyHost host = new("login") { Fault = fault };
        GameActionProgress progress = new();
        GameUiReadinessOutcome result = await host.Run(true, progress);
        Assert.NotEqual(GameUiReadinessState.Ready, result.State);
        Assert.NotNull(result.Failure);
        Assert.True(progress.MutationAttempted);
        Assert.False(progress.ChangeProven);
        Assert.Equal(1, result.TapAttempts);
        Assert.Equal(1, host.Taps);
    }

    [Fact]
    public async Task PhasesUseOneMonotonicTimelineAndMarkMissingStates()
    {
        using ReadyHost host = new("login");
        GameUiReadinessOutcome result = await host.Run(true, new());
        Assert.Equal(GameUiReadinessState.Ready, result.State);
        GameUiPhase[] observed = [.. result.Phases.Where(p => p.Status == GameUiPhaseStatus.Observed)];
        Assert.Equal(["state:login", "action:login.enter", "state:server", "action:login.start", "state:main"],
            observed.Select(p => p.Id));
        Assert.All(observed.Zip(observed.Skip(1)), p => Assert.True(p.First.ExitedAtMs <= p.Second.EnteredAtMs));
        Assert.All(observed, p => Assert.True(p.EnteredAtMs <= p.ExitedAtMs && p.ElapsedMs >= 0));
        Assert.Equal("stable_main_ui", observed[^1].EndReason);
        Assert.Equal("change_proven", observed[1].EndReason);
        Assert.Equal(result.Work!.Captures, observed.Sum(p => p.Work!.Captures));
        Assert.Equal(result.Work.ViewportChecks, observed.Sum(p => p.Work!.ViewportChecks));
        Assert.Equal(2, observed.Sum(p => p.Work!.Taps));
        Assert.Equal(2, observed.Where(p => p.Id.StartsWith("action:", StringComparison.Ordinal)).Sum(p => p.Work!.Taps));
        Assert.Contains(result.Phases, p => p.Id == "state:download" && p.Status == GameUiPhaseStatus.NotObserved
            && p.ElapsedMs is null && p.Work is null);
        Assert.Contains(result.Phases, p => p.Id == "action:update.download" && p.Status == GameUiPhaseStatus.NotObserved);
        Assert.Equal(result.Phases.Count, result.Phases.Select(p => p.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(720, "main", "main")]
    [InlineData(1080, "login", "login")]
    [InlineData(1440, "server", "server")]
    [InlineData(2160, "download-confirm", "download-confirm")]
    [InlineData(2160, "main-overlay", null)]
    [InlineData(2160, "main-unknown-overlay", "blocked-dialog")]
    [InlineData(2160, "main-sidebar-overlay", "blocked-sidebar")]
    [InlineData(2160, "download", "download")]
    [InlineData(2160, "loading", "loading")]
    [InlineData(2160, "loading-real-2", "loading")]
    [InlineData(2160, "loading-real-3", "loading")]
    [InlineData(2160, "login-transition", "login-transition")]
    [InlineData(2160, "unverified-update", "unverified-update")]
    public void NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(int height, string fixture, string? expected)
    {
        GameUiObservation observed = ObserveFixture(height, fixture);
        GameUiReadinessPolicy policy = GameAssetCatalog.Default.Readiness;
        Assert.Equal(expected, policy.Classify(observed)?.Id);
        if (fixture.Contains("overlay", StringComparison.Ordinal))
        {
            Assert.Equal(GameMatchStatus.Unique, observed.Find("main.dock")!.Status);
            Assert.Equal(GameMatchStatus.Unique, observed.Find("main.depot")!.Status);
            Assert.Equal(GameMatchStatus.Unique, observed.Find("main.battle")!.Status);
            Assert.Equal(GameMatchStatus.Unique, observed.Find(fixture == "main-sidebar-overlay"
                ? "overlay.sidebar-open" : "overlay.dialog-close")!.Status);
            Assert.NotEqual(GameUiReadinessState.Ready, policy.Classify(observed)?.State);
            if (fixture == "main-unknown-overlay")
            {
                Assert.Equal(GameMatchStatus.Absent, observed.Find("update.header")!.Status);
                Assert.Equal(GameMatchStatus.Absent, observed.Find("update.download")!.Status);
                Assert.Equal(GameMatchStatus.Absent, observed.Find("update.confirm")!.Status);
            }
        }
    }

    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void StaticLoginAnchorIsSharedButOnlyServerStageSelectsStart(int height)
    {
        GameUiReadinessPolicy policy = GameAssetCatalog.Default.Readiness;
        GameUiObservation login = ObserveFixture(height, "login");
        GameUiObservation server = ObserveFixture(height, "server");
        Assert.Equal(GameMatchStatus.Unique, login.Find("login.criware")!.Status);
        Assert.Equal(GameMatchStatus.Unique, server.Find("login.criware")!.Status);
        Assert.Equal("login.enter", policy.Classify(login)!.ActionId);
        Assert.Equal("login.start", policy.Classify(server)!.ActionId);
        foreach (string negative in new[] { "main", "download", "download-confirm", "dock" })
        {
            Assert.Equal(GameMatchStatus.Absent, ObserveFixture(height, negative).Find("login.criware")!.Status);
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
            ("server", "server"), ("download", "download"), ("loading", "loading"), ("download-confirm", "download-confirm"),
            ("login-transition", "login-transition"), ("main-overlay", null), ("main-unknown-overlay", "blocked-dialog"),
            ("main-sidebar-overlay", "blocked-sidebar"),
            ("unverified-update", "unverified-update"),
        })
        {
            NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, fixture, expected);
        }
    }

    // Синтетический фон меняется только вне вставленных crops: это доказывает независимость от фона вне
    // шаблонов, но не устойчивость к изменению пикселей внутри шаблона. Overlay fixtures не воспроизводят
    // затемнение настоящего UI и проверяют только guards каталога.
    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void MainAnchorsIgnoreBackgroundOutsideCropsAndOverlayGuardsBlockReady(int height)
    {
        foreach (string fixture in new[] { "main", "main-background-light", "main-background-dark" })
        {
            NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, fixture, "main");
        }

        NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, "main-overlay", null);
        NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, "main-unknown-overlay", "blocked-dialog");
        NativeFixturesClassifySupportedStatesAndRejectMainUnderOverlay(height, "main-sidebar-overlay", "blocked-sidebar");
    }

    private static GameUiObservation ObserveFixture(int height, string fixture)
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot, "tests", "fixtures", "game", $"{fixture}-{height}.png")));
        return new GameUiRecognizer(GameAssetCatalog.Default).Observe(frame,
            GameUiRecognizerTests.Provenance(frame), GameAssetCatalog.Default.Readiness.AssetIds);
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

    /// <summary>
    /// Событийная модель экрана: состояние меняется только нажатием, течением времени на экране
    /// или явным мерцанием UI. Номер захвата не определяет переходы.
    /// </summary>
    internal sealed class ReadyHost : IGameUiHost, IDisposable
    {
        private static readonly Dictionary<string, string[]> Screens = new(StringComparer.Ordinal)
        {
            ["main"] = ["main.dock", "main.depot", "main.battle"],
            ["main-overlay"] = ["main.dock", "main.depot", "main.battle", "overlay.dialog-close", "update.header", "update.download", "update.confirm"],
            ["main-unknown-overlay"] = ["main.dock", "main.depot", "main.battle", "overlay.dialog-close"],
            ["main-sidebar-overlay"] = ["main.dock", "main.depot", "main.battle", "overlay.sidebar-open"],
            ["login"] = ["login.enter", "login.logo", "login.criware"],
            ["server"] = ["login.server", "login.logo", "login.criware"],
            ["login-and-server"] = ["login.enter", "login.server", "login.logo", "login.criware"],
            ["login-transition"] = ["login.success", "login.server", "login.logo", "login.criware"],
            ["login-dialog"] = ["login.enter", "login.logo", "login.criware", "overlay.dialog-close"],
            ["server-dialog"] = ["login.server", "login.logo", "login.criware", "overlay.dialog-close"],
            ["dock-dialog"] = ["dock.header", "overlay.dialog-close"],
            ["download-confirm"] = ["update.header", "update.download", "update.confirm"],
            ["unverified-update"] = ["update.header", "update.confirm"],
            ["download"] = ["loading.download"],
            ["loading"] = ["loading.startup"],
            ["dock"] = ["dock.header"],
            ["selection"] = ["dock.header", "dock.selection-confirm"],
            ["unknown"] = [],
        };

        private readonly CancellationTokenSource _cancel = new();
        private readonly long _started;
        private string _screen;
        private long _entered;
        private GameFrameProvenance? _previous;

        internal ReadyHost(string initial)
        {
            _started = Clock.Now;
            _screen = initial;
            _entered = Clock.Now;
        }

        internal ReadyClock Clock { get; } = new();
        /// <summary>Сколько монотонного времени занимает один захват.</summary>
        internal TimeSpan CaptureCost { get; init; } = TimeSpan.FromSeconds(2);
        /// <summary>Переход экрана после tap.</summary>
        internal Dictionary<string, string> OnTap { get; } = new(StringComparer.Ordinal)
        {
            ["login"] = "server",
            ["server"] = "main",
            ["download-confirm"] = "download",
            ["dock"] = "main",
        };
        /// <summary>Переход после пребывания на экране не меньше заданного времени.</summary>
        internal Dictionary<string, (TimeSpan After, string Next)> Timed { get; } = new(StringComparer.Ordinal)
        {
            ["download"] = (TimeSpan.FromSeconds(10), "main"),
        };
        internal TimeSpan InitialUnknown { get; init; }
        internal string? Flicker { get; init; }
        internal string Progress { get; init; } = "slow";
        internal bool DialogAfterTap { get; init; }
        internal string Fault { get; init; } = "";
        internal GameMatchStatus? Guard { get; init; }
        internal bool MissingGuard { get; init; }
        internal string GuardAssetId { get; init; } = "dock.selection-confirm";
        internal int Captures { get; private set; }
        internal int Viewports { get; private set; }
        internal int Taps => TapPermits.Count;
        internal List<GameTap> TapPermits { get; } = [];
        internal Guid? FirstViewportFrame { get; private set; }
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

        private void Move(string screen)
        {
            _screen = screen;
            _entered = Clock.Now;
        }

        private string Current()
        {
            if (Timed.TryGetValue(_screen, out (TimeSpan After, string Next) timed) && timed.Next is not null
                && Clock.GetElapsedTime(_entered) >= timed.After)
            {
                Move(timed.Next);
            }

            TimeSpan sinceStart = Clock.GetElapsedTime(_started);
            if (sinceStart < InitialUnknown)
            {
                _entered = Clock.Now;
                return "unknown";
            }

            return Flicker is not null && (long)sinceStart.TotalSeconds / 2 % 2 == 1 ? Flicker : _screen;
        }

        /// <summary>Доля заполнения, квантованная сегментами полосы так же, как измерение host.</summary>
        private double Level(TimeSpan onScreen)
        {
            double seconds = onScreen.TotalSeconds;
            double fill = Progress switch
            {
                "stalled" => 0.25,
                // Колебание на четверть полосы — намного выше одного сегмента — с возвратом к прежнему значению.
                "oscillating" => (long)seconds / 2 % 2 == 0 ? 0.25 : 0.5,
                "endless" => seconds / 400,
                // Пилообразный сброс к нулю без доказанного нового этапа.
                "reset" => seconds % 30 / 60,
                // Один сегмент примерно за 16 s: медленнее интервала кадров, но быстрее срока без роста.
                _ => seconds / 1000,
            };
            return Math.Floor(Math.Min(fill, 1) * 64) / 64;
        }

        public Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
            IReadOnlyList<string> assets, CancellationToken cancellationToken)
        {
            Assert.Equal(Target, instanceId);
            Assert.Equal(Endpoint, endpoint);
            OnCapture?.Invoke();
            Captures++;
            string screen = Current();
            long timestamp = Clock.Now;
            TimeSpan onScreen = Clock.GetElapsedTime(_entered);
            Clock.Now += CaptureCost.Ticks;
            if (Fault == "transport" || (Fault == "post-transport" && Taps > 0))
            {
                return Task.FromResult(ApplicationResult<GameUiObservation>.Failure(GameUiActionRunner.Failure("transport_lost")));
            }

            bool later = Clock.GetElapsedTime(_started) > CaptureCost;
            GameFrameProvenance frame = new(Guid.NewGuid(), Fault == "wrong-target" ? MuMuInstanceId.FromIndex("8") : Target,
                Fault == "wrong-endpoint" ? new("other-host", 20312) : Endpoint,
                DateTimeOffset.UnixEpoch.AddTicks(timestamp).AddSeconds(Fault == "future" ? 13 : 0),
                Fault == "stale" ? timestamp - TimeSpan.FromSeconds(13).Ticks : timestamp,
                Fault == "geometry" || (Fault == "late-geometry" && Captures > 2) ? 1000 : Fault == "resized" && later ? 1280 : 3840,
                Fault == "resized" && later ? 720 : 2160);
            if (_previous is not null && Fault == "replayed-id")
            {
                frame = frame with { FrameId = _previous.FrameId };
            }

            if (_previous is not null && Fault == "replayed-time")
            {
                frame = frame with { CaptureStartedTimestamp = _previous.CaptureStartedTimestamp };
            }

            _previous ??= frame;
            string[] present = Screens[screen];
            GameUiObservation observation = new(frame, [.. assets.Where(id => !MissingGuard || id != GuardAssetId)
                .Select(id => new GameUiMatch(id, "test",
                    Fault == "native" ? GameMatchStatus.TechnicalFailure
                    : id == GuardAssetId && Guard is not null ? Guard.Value
                    : DialogAfterTap && Taps > 0 && id == "overlay.dialog-close" ? GameMatchStatus.Unique
                    : present.Contains(id, StringComparer.Ordinal) ? GameMatchStatus.Unique : GameMatchStatus.Absent,
                    new(10, 10, 100, 80), new(20, 20, 80, 60), 1, 0, 0, "test"))])
            {
                ProgressLevel = present.Contains("loading.download", StringComparer.Ordinal) ? Level(onScreen) : null,
            };
            return Task.FromResult(ApplicationResult<GameUiObservation>.Success(observation));
        }

        public Task<ApplicationResult<bool>> ValidateInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
        {
            Viewports++;
            FirstViewportFrame ??= frame.FrameId;
            if (Fault == "cancel-viewport")
            {
                _cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(ApplicationResult<bool>.Success(Fault != "viewport"));
        }

        public Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
        {
            Assert.True(tap.TryConsume());
            Assert.False(tap.TryConsume());
            TapPermits.Add(tap);
            if (Fault == "cancel-tap")
            {
                _cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Fault is not ("no-change" or "tap-failure") && OnTap.TryGetValue(_screen, out string? next))
            {
                Move(next);
            }

            return Task.FromResult(ApplicationResult<AndroidCommandOutcome>.Success(new(Fault == "tap-failure" ? 1 : 0, "")));
        }
    }
}
