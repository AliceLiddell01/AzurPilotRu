using System.Diagnostics;
using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;

namespace AzurPilot.App.OperatorServices;

/// <summary>Один экземпляр MuMu в подтверждённом inventory и его host-side наблюдение.</summary>
/// <param name="Instance">Найденный экземпляр с provider identity.</param>
/// <param name="Observation">Доказанное состояние либо <see langword="null"/>, если его не удалось наблюдать.</param>
/// <param name="Failure">Ограниченный отказ наблюдения либо <see langword="null"/> при успехе.</param>
public sealed record AzurPilotOperatorMuMuInstance(
    MuMuInstance Instance,
    MuMuInstanceState? Observation,
    ApplicationFailure? Failure);

/// <summary>Inventory MuMu, полученный одним production discovery и enumeration path.</summary>
/// <param name="InstallationVersion">Версия найденной установки.</param>
/// <param name="Instances">Подтверждённые экземпляры; отказ состояния отдельного экземпляра остаётся в его записи.</param>
public sealed record AzurPilotOperatorMuMuInventory(
    string InstallationVersion,
    IReadOnlyList<AzurPilotOperatorMuMuInstance> Instances);

/// <summary>Результаты двух поддерживаемых ROI одного фактического screenshot capture.</summary>
/// <param name="Capture">Проверенные метаданные захвата без native frame.</param>
/// <param name="Regions">Статистика FullFrame и Center, полученная production scanner-ом.</param>
public sealed record AzurPilotOperatorScreenshotAnalysis(
    AndroidScreenshotCaptureInfo Capture,
    IReadOnlyList<ScreenshotRegionScanResult> Regions);

/// <summary>
/// Связывает пользовательские действия AzurPilot с уже существующими production capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Сервис не хранит выбранный экземпляр и не принимает Android endpoint от presentation layer. Каждая
/// операция получает exact MuMu identity, заново проверяет её через lifecycle resolver и, если действие
/// адресовано Android, разрешает endpoint только для этой пары установки и identity. Так instance и
/// endpoint нельзя смешать из пользовательского ввода.
/// </para>
/// <para>
/// Диагностика и inventory выполняют только наблюдения. Подготовка transport является отдельным явным
/// use case; screenshot capture связывает production capture и native ROI scanner в одном ограниченном
/// времени жизни кадра.
/// </para>
/// </remarks>
public sealed class AzurPilotOperatorService
{
    private const string DiagnosticsActivity = "azurpilot.operator.diagnostics";
    private const string InventoryActivity = "azurpilot.operator.mumu.inventory";
    private const string MuMuLifecycleActivity = "azurpilot.operator.mumu.lifecycle";
    private const string AndroidObserveActivity = "azurpilot.operator.android.observe";
    private const string AndroidPrepareActivity = "azurpilot.operator.android.prepare";
    private const string GameObserveActivity = "azurpilot.operator.azurlane.observe";
    private const string GameLifecycleActivity = "azurpilot.operator.azurlane.lifecycle";
    private const string ScreenshotActivity = "azurpilot.operator.screenshot";

    private readonly AzurPilotDiagnosticService _diagnostics;
    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly IAndroidHost _androidHost;
    private readonly AndroidReadinessService _androidReadiness;
    private readonly AzurLaneGameStateService _gameState;
    private readonly AzurLaneGameLifecycleService _gameLifecycle;
    private readonly IAndroidScreenshotHost _screenshots;

    /// <summary>Создаёт operator use cases поверх singleton-сервисов одного application host.</summary>
    public AzurPilotOperatorService(
        AzurPilotDiagnosticService diagnostics,
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
        AndroidReadinessService androidReadiness,
        AzurLaneGameStateService gameState,
        AzurLaneGameLifecycleService gameLifecycle,
        IAndroidScreenshotHost screenshots)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(androidReadiness);
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(gameLifecycle);
        ArgumentNullException.ThrowIfNull(screenshots);

        _diagnostics = diagnostics;
        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
        _androidHost = androidHost;
        _androidReadiness = androidReadiness;
        _gameState = gameState;
        _gameLifecycle = gameLifecycle;
        _screenshots = screenshots;
    }

    /// <summary>Повторно собирает существующий read-only diagnostic snapshot.</summary>
    /// <returns>Фактически наблюдённое состояние приложения, конфигурации, native, MuMu, Android и игры.</returns>
    public AzurPilotDiagnosticReport RefreshDiagnostics()
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(DiagnosticsActivity);
        using (operation.StartStep(AzurPilotOperation.NativeDiagnosticsActivityName))
        {
            return _diagnostics.Capture();
        }
    }

    /// <summary>Обнаруживает установку и inventory экземпляров MuMu, затем отдельно наблюдает каждый exact instance.</summary>
    /// <returns>Inventory с состояниями либо production failure discovery/enumeration.</returns>
    public ApplicationResult<AzurPilotOperatorMuMuInventory> DiscoverMuMuInstances()
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(InventoryActivity);
        using Activity? step = operation.StartStep("azurpilot.operator.mumu.enumerate");

        ApplicationResult<MuMuInstallation> installation = _muMuHost.DiscoverInstallation();
        if (installation.IsFailure)
        {
            return ApplicationResult<AzurPilotOperatorMuMuInventory>.Failure(installation.FailureInfo!);
        }

        MuMuInstallation value = installation.Value!;
        ApplicationResult<IReadOnlyList<MuMuInstance>> enumeration = _muMuHost.EnumerateInstances(value);
        if (enumeration.IsFailure)
        {
            return ApplicationResult<AzurPilotOperatorMuMuInventory>.Failure(enumeration.FailureInfo!);
        }

        List<AzurPilotOperatorMuMuInstance> instances = new(enumeration.Value!.Count);
        foreach (MuMuInstance instance in enumeration.Value)
        {
            ApplicationResult<MuMuInstanceState> observation = _muMuHost.ObserveInstanceState(value, instance.Id);
            instances.Add(observation.IsSuccess
                ? new AzurPilotOperatorMuMuInstance(instance, observation.Value, Failure: null)
                : new AzurPilotOperatorMuMuInstance(instance, Observation: null, observation.FailureInfo));
        }

        return ApplicationResult<AzurPilotOperatorMuMuInventory>.Success(
            new AzurPilotOperatorMuMuInventory(value.Version, Array.AsReadOnly(instances.ToArray())));
    }

    /// <summary>Выполняет start, stop или restart exact MuMu identity с production postcondition.</summary>
    public async Task<ApplicationResult<MuMuLifecycleOutcome>> RunMuMuLifecycleAsync(
        MuMuInstanceId instanceId,
        MuMuLifecycleOperation operationKind,
        CancellationToken cancellationToken)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(MuMuLifecycleActivity);
        using (operation.StartStep("azurpilot.operator.mumu.resolve"))
        {
            ApplicationResult<ResolvedMuMuTarget> target = ResolveMuMuTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<MuMuLifecycleOutcome>.Failure(target.FailureInfo!);
            }

            ResolvedMuMuTarget value = target.Value!;
            using (operation.StartStep("azurpilot.operator.mumu.execute"))
            {
                return operationKind switch
                {
                    MuMuLifecycleOperation.Start => await _muMuLifecycle.StartAsync(
                        value.Installation, value.Instance, cancellationToken).ConfigureAwait(false),
                    MuMuLifecycleOperation.Stop => await _muMuLifecycle.StopAsync(
                        value.Installation, value.Instance, cancellationToken).ConfigureAwait(false),
                    MuMuLifecycleOperation.Restart => await _muMuLifecycle.RestartAsync(
                        value.Installation, value.Instance, cancellationToken).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, "Неизвестная операция MuMu."),
                };
            }
        }
    }

    /// <summary>Наблюдает Android transport/readiness exact выбранного MuMu instance без подключения.</summary>
    public ApplicationResult<AndroidReadinessFacts> ObserveAndroid(MuMuInstanceId instanceId)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(AndroidObserveActivity);
        using (operation.StartStep("azurpilot.operator.android.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AndroidReadinessFacts>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.android.observe"))
            {
                return _androidReadiness.Observe(target.Value!.Endpoint);
            }
        }
    }

    /// <summary>Явно подготавливает transport только для выбранного target и проверяет готовность Android.</summary>
    public async Task<ApplicationResult<AndroidTransportPreparationOutcome>> PrepareAndroidAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(AndroidPrepareActivity);
        using (operation.StartStep("azurpilot.operator.android.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AndroidTransportPreparationOutcome>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.android.prepare"))
            {
                return await _androidReadiness.PrepareTransportAsync(
                    target.Value!.Endpoint,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Наблюдает Azur Lane Global/EN на endpoint-е, разрешённом для exact MuMu identity.</summary>
    public ApplicationResult<AzurLaneGameObservation> ObserveAzurLane(MuMuInstanceId instanceId)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(GameObserveActivity);
        using (operation.StartStep("azurpilot.operator.azurlane.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AzurLaneGameObservation>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.azurlane.observe"))
            {
                return _gameState.Observe(target.Value!.Endpoint);
            }
        }
    }

    /// <summary>Запускает Azur Lane Global/EN и возвращает доказанный итог существующей orchestration.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StartAzurLaneAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
        => RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Start, cancellationToken);

    /// <summary>Останавливает Azur Lane Global/EN и возвращает доказанный итог существующей orchestration.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StopAzurLaneAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
        => RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Stop, cancellationToken);

    /// <summary>Перезапускает Azur Lane Global/EN и возвращает доказанный итог существующей orchestration.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RestartAzurLaneAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
        => RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Restart, cancellationToken);

    /// <summary>Захватывает screenshot exact target и анализирует FullFrame и Center native ROI.</summary>
    public async Task<ApplicationResult<AzurPilotOperatorScreenshotAnalysis>> CaptureAndScanAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(ScreenshotActivity);
        using (operation.StartStep("azurpilot.operator.screenshot.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AzurPilotOperatorScreenshotAnalysis>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.screenshot.capture"))
            {
                ApplicationResult<CapturedAndroidScreenshot> capture = await _screenshots.CaptureScreenshotAsync(
                    target.Value!.Instance.Id,
                    target.Value.Endpoint,
                    cancellationToken).ConfigureAwait(false);
                if (capture.IsFailure)
                {
                    return ApplicationResult<AzurPilotOperatorScreenshotAnalysis>.Failure(capture.FailureInfo!);
                }

                using CapturedAndroidScreenshot screenshot = capture.Value!;
                ScreenshotAnalysisSource source = ScreenshotAnalysisSource.ForCapture(screenshot.Info);

                using (operation.StartStep("azurpilot.operator.screenshot.full_frame"))
                {
                    ApplicationResult<ScreenshotRegionScanResult> fullFrame = ScreenshotRegionScanner.Scan(
                        screenshot.Frame,
                        source,
                        ScreenshotDiagnosticRegion.FullFrame);
                    if (fullFrame.IsFailure)
                    {
                        return ApplicationResult<AzurPilotOperatorScreenshotAnalysis>.Failure(fullFrame.FailureInfo!);
                    }

                    using (operation.StartStep("azurpilot.operator.screenshot.center"))
                    {
                        ApplicationResult<ScreenshotRegionScanResult> center = ScreenshotRegionScanner.Scan(
                            screenshot.Frame,
                            source,
                            ScreenshotDiagnosticRegion.Center);
                        if (center.IsFailure)
                        {
                            return ApplicationResult<AzurPilotOperatorScreenshotAnalysis>.Failure(center.FailureInfo!);
                        }

                        ScreenshotRegionScanResult[] results = [fullFrame.Value!, center.Value!];
                        return ApplicationResult<AzurPilotOperatorScreenshotAnalysis>.Success(
                            new AzurPilotOperatorScreenshotAnalysis(
                                screenshot.Info,
                                Array.AsReadOnly(results)));
                    }
                }
            }
        }
    }

    private async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RunAzurLaneLifecycleAsync(
        MuMuInstanceId instanceId,
        AzurLaneAction action,
        CancellationToken cancellationToken)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(GameLifecycleActivity);
        using (operation.StartStep("azurpilot.operator.azurlane.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.azurlane.execute"))
            {
                return action switch
                {
                    AzurLaneAction.Start => await _gameLifecycle.StartAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken).ConfigureAwait(false),
                    AzurLaneAction.Stop => await _gameLifecycle.StopAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken).ConfigureAwait(false),
                    AzurLaneAction.Restart => await _gameLifecycle.RestartAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестная операция Azur Lane."),
                };
            }
        }
    }

    private ApplicationResult<ResolvedMuMuTarget> ResolveMuMuTarget(MuMuInstanceId instanceId)
    {
        ApplicationResult<MuMuInstallation> installation = _muMuHost.DiscoverInstallation();
        if (installation.IsFailure)
        {
            return ApplicationResult<ResolvedMuMuTarget>.Failure(installation.FailureInfo!);
        }

        MuMuInstallation value = installation.Value!;
        MuMuInstanceResolution resolution = _muMuLifecycle.Resolve(
            value,
            MuMuInstanceSelection.Explicit(instanceId));
        if (resolution.IsFailed)
        {
            return ApplicationResult<ResolvedMuMuTarget>.Failure(resolution.Failure!);
        }

        return ApplicationResult<ResolvedMuMuTarget>.Success(new ResolvedMuMuTarget(value, resolution.Instance!));
    }

    private ApplicationResult<ResolvedAndroidTarget> ResolveAndroidTarget(MuMuInstanceId instanceId)
    {
        ApplicationResult<ResolvedMuMuTarget> resolved = ResolveMuMuTarget(instanceId);
        if (resolved.IsFailure)
        {
            return ApplicationResult<ResolvedAndroidTarget>.Failure(resolved.FailureInfo!);
        }

        ResolvedMuMuTarget target = resolved.Value!;
        ApplicationResult<AndroidAdbExecutable> adb = _androidHost.DiscoverAdbExecutable(target.Installation);
        if (adb.IsFailure)
        {
            return ApplicationResult<ResolvedAndroidTarget>.Failure(adb.FailureInfo!);
        }

        ApplicationResult<AndroidEndpoint> endpoint = _androidReadiness.ResolveEndpoint(
            target.Installation,
            target.Instance.Id);
        if (endpoint.IsFailure)
        {
            return ApplicationResult<ResolvedAndroidTarget>.Failure(endpoint.FailureInfo!);
        }

        return ApplicationResult<ResolvedAndroidTarget>.Success(
            new ResolvedAndroidTarget(target.Installation, target.Instance, endpoint.Value!));
    }

    private readonly record struct ResolvedMuMuTarget(MuMuInstallation Installation, MuMuInstance Instance);

    private readonly record struct ResolvedAndroidTarget(
        MuMuInstallation Installation,
        MuMuInstance Instance,
        AndroidEndpoint Endpoint);

    private enum AzurLaneAction
    {
        Start,
        Stop,
        Restart,
    }
}
