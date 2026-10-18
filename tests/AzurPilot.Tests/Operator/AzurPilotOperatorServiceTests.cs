using System.Collections.Concurrent;
using System.Diagnostics;
using AzurPilot.App;
using AzurPilot.App.Diagnostics;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;
using AzurPilot.Core.MuMu;
using AzurPilot.Tests.Android;
using AzurPilot.Tests.Diagnostics;
using AzurPilot.Tests.MuMu;
using AzurPilot.Tests.MuMuWindows;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

[Collection(InteropCollection.Name)]
[Trait("Category", "Operator")]
public sealed partial class AzurPilotOperatorServiceTests
{
    [Fact(DisplayName = "Этапы подготовки следуют реальным production шагам, проверка загрузки не угадывается UI")]
    public async Task PreparationReportsActualStagesInOrder()
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        EnqueueAndroidReady(fixture, selected, new AndroidEndpoint("127.0.0.1", 20312));
        List<AzurPilotOperatorAndroidPreparationStage> stages = [];

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            selected, TestContext.Current.CancellationToken,
            new SynchronousProgress<AzurPilotOperatorAndroidPreparationStage>(stage =>
            {
                if (stage == AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot)
                {
                    Assert.DoesNotContain(fixture.Runner.Requests, request => request.Request.Arguments.Contains("getprop"));
                }

                stages.Add(stage);
            }));

        Assert.True(result.IsSuccess);
        Assert.Contains(AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot, stages);
        Assert.DoesNotContain(AzurPilotOperatorAndroidPreparationStage.StartMuMu, stages);
        Assert.True(stages.IndexOf(AzurPilotOperatorAndroidPreparationStage.ObserveMuMu)
            < stages.IndexOf(AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot));
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Запуск игры из Unknown не мутирует MuMu, Android и игру")]
    public async Task GameStartFromUnknownFailsBeforeAnyMutation()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Unknown);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await fixture.Service.StartAzurLaneAsync(
            MuMuInstanceId.FromIndex("7"), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuStateUnproven, result.FailureInfo!.Code);
        Assert.Contains("MuMu этим действием не менялся", result.FailureInfo.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("подтверждено как остановленное", result.FailureInfo.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory(DisplayName = "Ошибка запуска игры сообщает фактически наблюдённое postflight состояние MuMu")]
    [InlineData(MuMuLifecycleState.Stopped, "Новое наблюдение: MuMu остановлен.")]
    [InlineData(MuMuLifecycleState.Unknown, "Новое наблюдение не доказало состояние MuMu.")]
    public async Task GameStartReportsPostflightStateWhenPreparationFails(
        MuMuLifecycleState postflightState,
        string expectedPostflightMessage)
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.ObservationCount == 1 ? MuMuLifecycleState.Running : postflightState);
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selected, new AndroidEndpoint("127.0.0.1", 20312)));
        fixture.Runner.EnqueueOutcome(0, "offline\n");
        List<AzurPilotOperatorAndroidPreparationOutcome> preparationOutcomes = [];

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await fixture.Service.StartAzurLaneAsync(
            selected, CancellationToken.None, onPreparationCompleted: preparationOutcomes.Add);

        Assert.True(result.IsFailure);
        Assert.Contains(expectedPostflightMessage, result.FailureInfo!.Message, StringComparison.Ordinal);
        AzurPilotOperatorAndroidPreparationOutcome preparation = Assert.Single(
            preparationOutcomes, outcome => outcome.Failure is not null);
        Assert.Equal(postflightState, preparation.PostflightMuMuState);
        Assert.Equal(preparation, preparationOutcomes[^1]);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Подготовка Android и запуск игры сохраняют общий correlation identifier")]
    public async Task GameStartUsesOneCorrelationIdentityAcrossPreparationAndLifecycle()
    {
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        EnqueueAndroidReady(fixture, selected, new AndroidEndpoint("127.0.0.1", 20312));
        ConcurrentQueue<Activity> activities = new();

        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => string.Equals(
                source.Name, AzurPilotOperation.SourceName, StringComparison.Ordinal),
            Sample = static (ref _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName is "azurpilot.operator.azurlane.start"
                    or "azurpilot.operator.android.prepare_selected"
                    or "azurpilot.operator.azurlane.lifecycle")
                {
                    activities.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await fixture.Service.StartAzurLaneAsync(
            selected,
            CancellationToken.None,
            onGameStarting: () => fixture.MuMuHost.InstancesResult =
                ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]));

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, result.FailureInfo!.Code);
        Activity gameStart = Assert.Single(activities, activity =>
            activity.OperationName == "azurpilot.operator.azurlane.start");
        Activity preparation = Assert.Single(activities, activity =>
            activity.OperationName == "azurpilot.operator.android.prepare_selected");
        Activity lifecycle = Assert.Single(activities, activity =>
            activity.OperationName == "azurpilot.operator.azurlane.lifecycle");

        Assert.Equal(gameStart.TraceId, preparation.TraceId);
        Assert.Equal(gameStart.TraceId, lifecycle.TraceId);
        Assert.Equal(gameStart.SpanId, preparation.ParentSpanId);
        Assert.Equal(gameStart.SpanId, lifecycle.ParentSpanId);
    }

    private static readonly string[] AndroidReadinessProperties =
    [
        "sys.boot_completed",
        "ro.build.version.release",
        "ro.build.version.sdk",
    ];

    [Fact(DisplayName = "Подготовка выбранного Android при Running не запускает MuMu повторно или игру")]
    public async Task PrepareSelectedAndroidWhenRunningDoesNotMutateMuMuOrGame()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(
            MuMuLifecycleTestContext.Instance("3"), MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        EnqueueAndroidReady(fixture, selectedId, endpoint);

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            selectedId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurPilotOperatorAndroidPreparationStage.Completed, result.Stage);
        Assert.Equal(selectedId, result.InstanceId);
        Assert.Equal(MuMuLifecycleState.Running, result.InitialMuMuState);
        Assert.False(result.MuMuStartRequested);
        Assert.Null(result.MuMuStartOutcome);
        Assert.Equal(endpoint, result.AndroidPreparation!.Endpoint);
        Assert.True(result.MuMuMayRemainRunning);
        Assert.Equal(1, fixture.MuMuHost.ObservationCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        AssertAndroidPreparationCommandsOnly(fixture, selectedId, endpoint);
    }

    [Fact(DisplayName = "Подготовка Android запускает exact Stopped MuMu и доказывает transport, shell и boot")]
    public async Task PrepareSelectedAndroidStartsExactStoppedMuMuThenPreparesAndroid()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(
            MuMuLifecycleTestContext.Instance("3"), MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        fixture.MuMuHost.MutationHandler = request =>
        {
            Assert.Equal(selectedId, request.InstanceId);
            Assert.Empty(fixture.Runner.Requests);
            fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };
        EnqueueAndroidReady(fixture, selectedId, endpoint);

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            selectedId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Stopped, result.InitialMuMuState);
        Assert.True(result.MuMuStartRequested);
        Assert.Equal(MuMuLifecycleState.Running, result.MuMuStartOutcome!.FinalState);
        Assert.True(result.AndroidPreparation!.Boot.ShellAvailable);
        Assert.Equal(1, result.AndroidPreparation.Boot.BootCompleted);
        Assert.Equal(AndroidTransportState.Device, result.AndroidPreparation.FinalTransport);
        MuMuMutationRequest mutation = Assert.Single(fixture.MuMuHost.MutationRequests);
        Assert.Equal(selectedId, mutation.InstanceId);
        Assert.Equal(MuMuLifecycleMutation.Start, mutation.Mutation);
        AssertAndroidPreparationCommandsOnly(fixture, selectedId, endpoint);
    }

    [Fact(DisplayName = "Неизвестное исходное состояние запрещает lifecycle и Android preparation")]
    public async Task PrepareSelectedAndroidUnknownStateNeverMutatesOrResolvesAndroid()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            MuMuInstanceId.FromIndex("7"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AzurPilotOperatorAndroidPreparationStage.ObserveMuMu, result.Stage);
        Assert.Equal(ApplicationFailure.MuMuStateUnproven, result.Failure!.Code);
        ApplicationFailure expectedFailure = MuMuFailures.StateUnprovenBeforeOperatorPreparation(
            MuMuInstanceId.FromIndex("7"), MuMuLifecycleState.Unknown);
        Assert.Equal(expectedFailure.Message, result.Failure.Message);
        Assert.Equal(expectedFailure.IsRetryable, result.Failure.IsRetryable);
        Assert.Equal(expectedFailure.Details!.OrderBy(pair => pair.Key), result.Failure.Details!.OrderBy(pair => pair.Key));
        Assert.Equal(MuMuLifecycleState.Unknown, result.InitialMuMuState);
        Assert.False(result.MuMuStartRequested);
        Assert.Equal(1, fixture.MuMuHost.ObservationCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Отказ исходного наблюдения сохраняется без lifecycle и Android preparation")]
    public async Task PrepareSelectedAndroidObservationFailureIsNotStopped()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        ApplicationFailure failure = new()
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Control surface не позволила наблюдать экземпляр.",
        };
        fixture.MuMuHost.ObservationResult = ApplicationResult<MuMuInstanceState>.Failure(failure);

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            MuMuInstanceId.FromIndex("7"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(failure, result.Failure);
        Assert.Null(result.InitialMuMuState);
        Assert.False(result.MuMuStartRequested);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Гонка Stopped → Unknown под gate не запускает MuMu и не достигает Android")]
    public async Task PrepareSelectedAndroidStoppedToUnknownRaceFailsBeforeMutation()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(
            fixture.MuMuHost.ObservationCount == 1 ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Unknown);

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            MuMuInstanceId.FromIndex("7"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AzurPilotOperatorAndroidPreparationStage.StartMuMu, result.Stage);
        Assert.Equal(ApplicationFailure.MuMuStateUnproven, result.Failure!.Code);
        Assert.Equal(3, fixture.MuMuHost.ObservationCount);
        Assert.Equal(MuMuLifecycleState.Unknown, result.PostflightMuMuState);
        Assert.Null(result.MuMuStartOutcome);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Отказ Android после запуска сохраняет частичный итог и не выполняет stop")]
    public async Task PrepareSelectedAndroidFailurePreservesStartedMuMuWithoutCompensation()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        fixture.MuMuHost.MutationHandler = _ =>
        {
            fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, endpoint));
        fixture.Runner.EnqueueOutcome(0, "offline\n");

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            selectedId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AzurPilotOperatorAndroidPreparationStage.PrepareAndroid, result.Stage);
        Assert.Equal(ApplicationFailure.AndroidTransportNotReady, result.Failure!.Code);
        Assert.True(result.MuMuMayRemainRunning);
        Assert.Equal(MuMuLifecycleState.Running, result.MuMuStartOutcome!.FinalState);
        Assert.Null(result.AndroidPreparation);
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
        Assert.Equal(3, fixture.Runner.Requests.Count);
    }

    [Fact(DisplayName = "Отмена до подготовки не наблюдает target и не выполняет mutation")]
    public async Task PrepareSelectedAndroidPreCancelledDoesNoWork()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            MuMuInstanceId.FromIndex("7"), cancellation.Token);

        Assert.Equal(ApplicationFailure.OperationCancelled, result.Failure!.Code);
        Assert.False(result.IsSuccess);
        Assert.False(result.MuMuStartRequested);
        Assert.Equal(0, fixture.MuMuHost.ObservationCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Отмена после host-side запуска сохраняет MuMu и не подготавливает Android или игру")]
    public async Task PrepareSelectedAndroidCancellationAfterMuMuStartPreservesPartialState()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        using CancellationTokenSource cancellation = new();
        fixture.MuMuHost.ObservationHandler = instanceId =>
        {
            if (fixture.MuMuHost.MutationCount == 0)
            {
                return MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
            }

            cancellation.Cancel();
            return MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        };

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            MuMuInstanceId.FromIndex("7"), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.Failure!.Code);
        Assert.True(result.MuMuMayRemainRunning);
        Assert.Equal(MuMuLifecycleMutation.Start, Assert.Single(fixture.MuMuHost.MutationRequests).Mutation);
        Assert.Empty(fixture.Runner.Requests);
    }

    private static void EnqueueAndroidReady(OperatorFixture fixture, MuMuInstanceId instanceId, AndroidEndpoint endpoint)
    {
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(instanceId, endpoint));
        fixture.Runner.EnqueueOutcome(0, "device\n");
        fixture.Runner.EnqueueOutcome(0, "1\n");
        fixture.Runner.EnqueueOutcome(0, "15.0\n");
        fixture.Runner.EnqueueOutcome(0, "35\n");
    }

    [Fact(DisplayName = "Подготовка Android передаёт cancellation token единственному exact target connect")]
    public async Task PrepareSelectedAndroidPassesCancellationToExactTransportPreparation()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        using CancellationTokenSource cancellation = new();
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, endpoint));
        fixture.Runner.EnqueueOutcome(1, string.Empty, $"error: device '{endpoint}' not found");
        fixture.Runner.EnqueueFailure(new ApplicationFailure
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = "Подключение ADB отменено на process boundary.",
        });

        AzurPilotOperatorAndroidPreparationOutcome result = await fixture.Service.PrepareSelectedAndroidAsync(
            selectedId, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.Failure!.Code);
        Assert.Equal(AzurPilotOperatorAndroidPreparationStage.PrepareAndroid, result.Stage);
        Assert.True(result.MuMuMayRemainRunning);
        Assert.Equal(4, fixture.Runner.Requests.Count);
        RecordedProcessRequest connect = fixture.Runner.Requests[3];
        Assert.Equal(["connect", endpoint.ToString()], connect.Request.Arguments);
        Assert.Equal(cancellation.Token, connect.CancellationToken);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    private static void AssertAndroidPreparationCommandsOnly(
        OperatorFixture fixture, MuMuInstanceId instanceId, AndroidEndpoint endpoint)
    {
        Assert.Equal(6, fixture.Runner.Requests.Count);
        Assert.Equal(MuMuManagerCommandBuilder.BuildInstanceInfoArguments(instanceId).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());
        Assert.Equal(["-s", endpoint.ToString(), "get-state"],
            fixture.Runner.Requests[2].Request.Arguments);
        foreach (RecordedProcessRequest request in fixture.Runner.Requests.Skip(3))
        {
            Assert.Equal("-s", request.Request.Arguments[0]);
            Assert.Equal(endpoint.ToString(), request.Request.Arguments[1]);
            Assert.Equal("shell", request.Request.Arguments[2]);
            Assert.Equal("getprop", request.Request.Arguments[3]);
        }

        Assert.Equal(
            AndroidReadinessProperties,
            fixture.Runner.Requests.Skip(3).Select(static request => request.Request.Arguments[4]));
    }

    [Fact(DisplayName = "Screenshot разрешает exact target, сканирует оба production ROI и освобождает capture frame")]
    public async Task CaptureAndScanUsesExactTargetAndDisposesOneCapturedFrameAfterBothScans()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint selectedEndpoint = new("127.0.0.1", 20312);
        MuMuInstance selected = MuMuLifecycleTestContext.Instance("7", "selected");
        MuMuInstance neighbor = MuMuLifecycleTestContext.Instance("3", "neighbor");
        OperatorFixture fixture = CreateFixture(neighbor, selected);
        byte[] png = ScreenshotPngFixture.CreateRgb(
            16,
            9,
            static (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)(x + y)));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, selectedEndpoint));
        fixture.Runner.EnqueueBinaryOutcome(png);

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            selectedId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        AzurPilotOperatorScreenshotAnalysis analysis = result.Value!;
        Assert.Equal(selectedId, analysis.Capture.InstanceId);
        Assert.Equal(selectedEndpoint, analysis.Capture.Endpoint);
        Assert.Equal(png.Length, analysis.Capture.EncodedPngByteCount);
        Assert.Equal(
            new NativeFrameInfo(16, 9, 48, 432, AzurPilotNativeBridge.PixelFormatRgb8),
            analysis.Capture.FrameInfo);
        Assert.Equal(
            [ScreenshotDiagnosticRegion.FullFrame, ScreenshotDiagnosticRegion.Center],
            analysis.Regions.Select(static region => region.Region));
        Assert.Same(analysis.Regions[0].Source, analysis.Regions[1].Source);
        Assert.All(analysis.Regions, region =>
        {
            Assert.Equal(16u, region.FrameWidth);
            Assert.Equal(9u, region.FrameHeight);
            Assert.True(region.PixelCount > 0);
            Assert.Equal(ScreenshotAnalysisSourceKind.AndroidCapture, region.Source.Kind);
            Assert.Equal(selectedId, region.Source.InstanceId);
            Assert.Equal(selectedEndpoint, region.Source.Endpoint);
            Assert.Equal(analysis.Capture.FrameInfo, region.Source.CapturedFrameInfo);
        });

        Assert.Equal(1, fixture.ScreenshotHost.CaptureCount);
        Assert.Equal(selectedId, fixture.ScreenshotHost.LastInstanceId);
        Assert.Equal(selectedEndpoint, fixture.ScreenshotHost.LastEndpoint);
        using CapturedAndroidScreenshot captured = Assert.IsType<CapturedAndroidScreenshot>(
            fixture.ScreenshotHost.LastCapture);
        _ = Assert.Throws<ObjectDisposedException>(() => captured.Frame.GetInfo());
        Assert.DoesNotContain(
            typeof(AzurPilotOperatorScreenshotAnalysis).GetProperties(),
            static property => typeof(NativeFrame).IsAssignableFrom(property.PropertyType));

        Assert.Equal(3, fixture.Runner.Requests.Count);
        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(selectedId).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());
        Assert.Equal(
            AndroidTestContext.Installation.ControlExecutablePath,
            fixture.Runner.Requests[1].Request.ExecutablePath);
        RecordedProcessRequest screenshotRequest = fixture.Runner.Requests[2];
        Assert.Equal(AndroidInstallationLayout.GetAdbExecutablePath(AndroidTestContext.Installation.InstallRoot),
            screenshotRequest.Request.ExecutablePath);
        Assert.Equal(
            ["-s", selectedEndpoint.ToString(), "exec-out", "screencap", "-p"],
            screenshotRequest.Request.Arguments);
        Assert.Equal(WindowsProcessStandardOutputMode.Binary, screenshotRequest.Request.StandardOutputMode);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Неизвестная identity отклоняется до ADB и screenshot capture без mutation")]
    public async Task UnknownTargetDoesNotReachAndroidOrScreenshotHost()
    {
        MuMuInstanceId unknownId = MuMuInstanceId.FromIndex("99");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            unknownId,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, result.FailureInfo!.Code);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Equal(0, fixture.MuMuHost.MutationCount);
        Assert.Equal(0, fixture.MuMuHost.ObservationCount);
    }

    [Fact(DisplayName = "Ошибка endpoint exact identity прекращает screenshot до любых target mutations")]
    public async Task UnresolvableEndpointDoesNotCaptureOrMutateTarget()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(
            MuMuLifecycleTestContext.Instance("3"),
            MuMuLifecycleTestContext.Instance("7"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, "not a MuMu instance response");

        ApplicationResult<AzurPilotOperatorScreenshotAnalysis> result = await fixture.Service.CaptureAndScanAsync(
            selectedId,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AndroidEndpointUnavailable, result.FailureInfo!.Code);
        Assert.Equal(2, fixture.Runner.Requests.Count);
        Assert.Equal(
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(selectedId).ToArray(),
            fixture.Runner.Requests[1].Request.Arguments.ToArray());
        Assert.Equal(0, fixture.ScreenshotHost.CaptureCount);
        Assert.Empty(fixture.MuMuHost.MutationRequests);
    }

    [Fact(DisplayName = "Отмена сохраняет operation gate, пока асинхронный use case фактически не завершён")]
    public async Task CancellationRetainsOperationSlotUntilServiceTaskCompletes()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint selectedEndpoint = new("127.0.0.1", 20312);
        BlockingScreenshotHost screenshotHost = new();
        OperatorFixture fixture = CreateFixture(
            screenshotHost,
            MuMuLifecycleTestContext.Instance("7", "selected"));
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, selectedEndpoint));

        using OperatorUiController controller = new(fixture.Service, InitialDiagnostics());
        Assert.True(controller.Session.SelectInstance(selectedId));
        Assert.True(controller.TryStartOperation(OperatorUiAction.Screenshot));
        await screenshotHost.CaptureStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        controller.CancelOperation();
        await screenshotHost.CancellationRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.True(controller.Session.IsOperationActive);
        Assert.False(controller.TryStartOperation(OperatorUiAction.Instances));
        Assert.Equal(2, fixture.Runner.Requests.Count);

        screenshotHost.ReleaseCapture();
        await controller.WaitForOperationAsync();

        Assert.False(controller.CompleteOperation());
        Assert.False(controller.Session.IsOperationActive);
        Assert.Equal("Операция: отменена", controller.OperationText);
    }

    [Fact(DisplayName = "TUI сообщает о частичной подготовке, exact target и возможном запущенном MuMu")]
    public async Task ControllerPresentsPartialEnvironmentPreparationFailure()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        AndroidEndpoint endpoint = new("127.0.0.1", 20312);
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7", "selected"));
        fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        fixture.MuMuHost.MutationHandler = _ =>
        {
            fixture.MuMuHost.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
            return MuMuLifecycleTestContext.Command(0);
        };
        fixture.Runner.EnqueueOutcome(0, "Android Debug Bridge version 1.0.41\nVersion 35.0.2-test\n");
        fixture.Runner.EnqueueOutcome(0, InstanceResponse(selectedId, endpoint));
        fixture.Runner.EnqueueOutcome(0, "offline\n");

        using OperatorUiController controller = new(fixture.Service, InitialDiagnostics());
        Assert.True(controller.Session.SelectInstance(selectedId));
        Assert.True(controller.Session.RequestConfirmation(OperatorUiAction.EnvironmentPrepare));
        Assert.True(controller.Session.ConfirmMutation());
        Assert.True(controller.TryStartOperation(OperatorUiAction.EnvironmentPrepare));
        await controller.WaitForOperationAsync();
        Assert.False(controller.CompleteOperation());

        OperatorUiMessage result = controller.Session.SnapshotMessages()[^1];
        Assert.Equal(OperatorUiMessageKind.Error, result.Kind);
        Assert.Contains("mumu:7", result.Text);
        Assert.Contains("подготовки Android", result.Text);
        Assert.Contains("Новое наблюдение: MuMu продолжает работать", result.Text);
        Assert.Contains("Azur Lane не запускалась", result.Text);
        Assert.Contains(ApplicationFailure.AndroidTransportNotReady, result.Details);
        MuMuMutationRequest mutation = Assert.Single(fixture.MuMuHost.MutationRequests);
        Assert.Equal(MuMuLifecycleMutation.Start, mutation.Mutation);
        Assert.Equal(selectedId, mutation.InstanceId);
    }

    [Fact(DisplayName = "Отмена подготовки среды сохраняется в статусе и предупреждении TUI")]
    public async Task ControllerMarksCancelledEnvironmentPreparationAsCancelled()
    {
        MuMuInstanceId selectedId = MuMuInstanceId.FromIndex("7");
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7", "selected"));
        TaskCompletionSource observationAfterStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.MuMuHost.ObservationHandler = instanceId =>
        {
            if (fixture.MuMuHost.MutationCount == 0)
            {
                return MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
            }

            _ = observationAfterStart.TrySetResult();
            releaseObservation.Task.GetAwaiter().GetResult();
            return MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        };

        using OperatorUiController controller = new(fixture.Service, InitialDiagnostics());
        Assert.True(controller.Session.SelectInstance(selectedId));
        Assert.True(controller.Session.RequestConfirmation(OperatorUiAction.EnvironmentPrepare));
        Assert.True(controller.Session.ConfirmMutation());
        Assert.True(controller.TryStartOperation(OperatorUiAction.EnvironmentPrepare));
        await observationAfterStart.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        controller.CancelOperation();
        _ = releaseObservation.TrySetResult();
        await controller.WaitForOperationAsync();
        Assert.False(controller.CompleteOperation());

        Assert.Equal("Операция: отменена", controller.OperationText);
        OperatorUiMessage result = controller.Session.SnapshotMessages()[^1];
        Assert.Equal(OperatorUiMessageKind.Warning, result.Kind);
        Assert.Contains("остановлена на этапе", result.Text);
        Assert.Contains("отменена", result.Text);
        _ = Assert.Single(fixture.MuMuHost.MutationRequests);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact(DisplayName = "Диагностика не считает Android готовым только по Device transport")]
    public void ControllerRequiresShellAndCompletedBootForAndroidReadySummary()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        AzurPilotDiagnosticReport diagnostics = InitialDiagnostics();
        diagnostics = diagnostics with
        {
            Android = diagnostics.Android with
            {
                IsShellAvailable = false,
                BootCompleted = 0,
            },
        };

        using OperatorUiController controller = new(fixture.Service, diagnostics, OperatorUiMode.Diagnostic);

        OperatorUiMessage summary = controller.Session.SnapshotMessages()[^1];
        Assert.Equal(OperatorUiMessageKind.Warning, summary.Kind);
        Assert.Contains("Android — транспорт доступен; shell недоступен, загрузка выполняется", summary.Text);
        Assert.DoesNotContain("Android — готов", summary.Text);
        Assert.Contains("Запустить окружение", summary.Text);
    }

    [Fact(DisplayName = "Разрешение target использует snapshot конфигурации, а не диагностическую проекцию")]
    public async Task ControllerResolvesConfiguredTargetFromServiceConfiguration()
    {
        OperatorFixture fixture = CreateFixture(MuMuLifecycleTestContext.Instance("7"));
        using OperatorUiController controller = new(fixture.Service, InitialDiagnostics());

        Assert.True(controller.TryStartOperation(OperatorUiAction.ResolveTarget));
        await controller.WaitForOperationAsync();
        Assert.False(controller.CompleteOperation());

        Assert.Null(controller.LastOperationFailure);
        OperatorUiMessage result = controller.Session.SnapshotMessages()[^1];
        Assert.Contains("автоматически", result.Text);
        Assert.Contains("mumu:7", result.Text);
        Assert.DoesNotContain("mumu.instance", result.Text);
    }

    private static OperatorFixture CreateFixture(params MuMuInstance[] instances)
        => CreateFixtureCore(null, null, instances);

    private static OperatorFixture CreateFixture(
        IAndroidScreenshotHost? screenshotHost,
        params MuMuInstance[] instances)
        => CreateFixtureCore(screenshotHost, null, instances);

    private static OperatorFixture CreateFixtureWithGameUi(IGameUiHost gameUi, params MuMuInstance[] instances)
        => CreateFixtureCore(null, gameUi, instances);

    private static OperatorFixture CreateFixtureCore(
        IAndroidScreenshotHost? screenshotHost,
        IGameUiHost? gameUi,
        params MuMuInstance[] instances)
    {
        MuMuInstallation installation = AndroidTestContext.Installation;
        TestMuMuHost muMuHost = new()
        {
            InstallationResult = ApplicationResult<MuMuInstallation>.Success(installation),
            InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(instances),
        };
        MuMuTestTimeProvider timeProvider = new();
        MuMuLifecycleService muMuLifecycle = new(
            muMuHost,
            new MuMuInstanceMutationGate(),
            timeProvider,
            MuMuLifecycleTimings.Default,
            NullLogger<MuMuLifecycleService>.Instance);
        FakeWindowsProcessRunner runner = new();
        AndroidWindowsHost androidHost = new(runner, timeProvider);
        AndroidReadinessService readiness = new(
            androidHost,
            AndroidLifecycleTimings.Default,
            timeProvider,
            NullLogger<AndroidReadinessService>.Instance);
        AzurLaneGameStateService gameState = new(
            androidHost,
            timeProvider,
            NullLogger<AzurLaneGameStateService>.Instance);
        AzurLaneGameLifecycleService gameLifecycle = new(
            androidHost,
            gameState,
            new AndroidGameMutationGate(),
            timeProvider,
            AndroidLifecycleTimings.Default,
            NullLogger<AzurLaneGameLifecycleService>.Instance);
        ObservingScreenshotHost observingScreenshotHost = new(androidHost);
        IAndroidScreenshotHost activeScreenshotHost = screenshotHost ?? observingScreenshotHost;
        string missingConfigurationPath = Path.Combine(
            Path.GetTempPath(),
            "azurpilot-operator-tests",
            $"missing-{Guid.NewGuid():N}.json");
        AzurPilotConfigurationSnapshot configuration = AzurPilotConfigurationLoader.Load(
            missingConfigurationPath).Value!;
        AzurPilotDiagnosticService diagnostics = new(
            configuration,
            NativeBoundaryFailureMapper.Map,
            NativeBoundaryFailureMapper.MapIncompatibility,
            muMuHost,
            muMuLifecycle,
            androidHost,
            readiness,
            gameState);
        AzurPilotOperatorService service = new(
            diagnostics,
            muMuHost,
            muMuLifecycle,
            androidHost,
            readiness,
            gameState,
            gameLifecycle,
            activeScreenshotHost,
            configuration,
            timeProvider,
            gameUi: gameUi ?? new AzurPilot.Windows.Game.GameUiWindowsHost(activeScreenshotHost, androidHost));

        return new OperatorFixture(muMuHost, runner, observingScreenshotHost, service);
    }

    private static AzurPilotDiagnosticReport InitialDiagnostics()
        => new(
            new AzurPilotApplicationDiagnostics(
                "AzurPilot.App", "1.0.0", "test", "net10.0", "win-x64", "Windows", "X64", "X64", 1, true),
            new AzurPilotConfigurationDiagnostics(
                AzurPilotConfigurationSource.BuiltInDefaults, 2, 2, "valid", LogLevel.Information, null),
            new AzurPilotNativeDiagnostics(true, true, 3, new Version(5, 0), [], true, null, "compatible", null),
            new AzurPilotMuMuDiagnostics(
                true, "test", "supported", "mumu.instance", "mumu:7", "selected", MuMuLifecycleState.Running,
                "state=running", null),
            new AzurPilotAndroidDiagnostics(
                true, "adb", "127.0.0.1:20312", AndroidTransportState.Device, true, 1, "15", 35,
                "complete", "ready", null),
            new AzurPilotAzurLaneDiagnostics(
                "Azur Lane Global/EN", "com.YoStarEN.AzurLane", true, true, true,
                AzurLaneGameState.Foreground, "foreground", null));

    private static string InstanceResponse(MuMuInstanceId instanceId, AndroidEndpoint endpoint)
        => "{"
            + $"\"index\":\"{instanceId.Index}\","
            + "\"is_process_started\":true,"
            + "\"is_android_started\":true,"
            + $"\"adb_host_ip\":\"{endpoint.Host}\","
            + $"\"adb_port\":{endpoint.Port}"
            + "}";

    private sealed record OperatorFixture(
        TestMuMuHost MuMuHost,
        FakeWindowsProcessRunner Runner,
        ObservingScreenshotHost ScreenshotHost,
        AzurPilotOperatorService Service);

    private sealed class BlockingScreenshotHost : IAndroidScreenshotHost
    {
        private readonly TaskCompletionSource _captureRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCapture() => _captureRelease.TrySetResult();

        public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), CancellationRequested);
            _ = CaptureStarted.TrySetResult();
            await _captureRelease.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Отменённый screenshot не должен возвращать capture.");
        }
    }

    private sealed class ObservingScreenshotHost(AndroidWindowsHost inner) : IAndroidScreenshotHost
    {
        internal Action? OnCapture { get; set; }
        public int CaptureCount { get; private set; }

        public MuMuInstanceId? LastInstanceId { get; private set; }

        public AndroidEndpoint? LastEndpoint { get; private set; }

        public CapturedAndroidScreenshot? LastCapture { get; private set; }

        public async Task<ApplicationResult<CapturedAndroidScreenshot>> CaptureScreenshotAsync(
            MuMuInstanceId instanceId,
            AndroidEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            OnCapture?.Invoke();
            LastInstanceId = instanceId;
            LastEndpoint = endpoint;
            ApplicationResult<CapturedAndroidScreenshot> result = await inner.CaptureScreenshotAsync(
                instanceId,
                endpoint,
                cancellationToken);
            LastCapture = result.IsSuccess ? result.Value : null;
            return result;
        }
    }
}
