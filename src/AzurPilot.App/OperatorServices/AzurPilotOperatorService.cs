using System.Diagnostics;
using AzurPilot.App.Diagnostics;
using AzurPilot.App.LogViewer;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;

namespace AzurPilot.App.OperatorServices;

/// <summary>Один экземпляр MuMu в подтверждённом inventory и его host-side наблюдение.</summary>
/// <param name="Instance">Найденный экземпляр с provider identity.</param>
/// <param name="Observation">Доказанное состояние либо <see langword="null"/>, если его не удалось наблюдать.</param>
/// <param name="Failure">Ограниченный отказ наблюдения либо <see langword="null"/> при успехе.</param>
public sealed record AzurPilotOperatorMuMuInstance(
    MuMuInstance Instance,
    MuMuInstanceState? Observation,
    ApplicationFailure? Failure);

/// <summary>Список экземпляров MuMu, полученный через общий путь обнаружения и перечисления.</summary>
/// <param name="InstallationVersion">Версия найденной установки.</param>
/// <param name="Instances">Подтверждённые экземпляры; отказ состояния отдельного экземпляра остаётся в его записи.</param>
public sealed record AzurPilotOperatorMuMuInventory(
    string InstallationVersion,
    IReadOnlyList<AzurPilotOperatorMuMuInstance> Instances);

/// <summary>Результаты двух поддерживаемых ROI одного фактического захвата снимка экрана.</summary>
/// <param name="Capture">Проверенные метаданные захвата без native frame.</param>
/// <param name="Regions">Статистика FullFrame и Center, полученная сканером приложения.</param>
public sealed record AzurPilotOperatorScreenshotAnalysis(
    AndroidScreenshotCaptureInfo Capture,
    IReadOnlyList<ScreenshotRegionScanResult> Regions);

/// <summary>Последний этап явной подготовки Android для выбранного экземпляра MuMu.</summary>
public enum AzurPilotOperatorAndroidPreparationStage
{
    /// <summary>Разрешение точного идентификатора в найденной установке.</summary>
    ResolveMuMu,
    /// <summary>Наблюдение состояния на стороне хоста без изменений.</summary>
    ObserveMuMu,
    /// <summary>Строгий запуск через существующий жизненный цикл.</summary>
    StartMuMu,
    /// <summary>Разрешение точного endpoint и подготовка Android.</summary>
    PrepareAndroid,
    /// <summary>Подтверждение доступного shell и завершённой загрузки Android.</summary>
    CheckAndroidBoot,
    /// <summary>Готовность Android доказана.</summary>
    Completed,
}

/// <summary>Ограниченный итог подготовки, сохраняющий завершённые этапы и исходный отказ приложения.</summary>
/// <param name="InstanceId">Явно выбранная identity.</param>
/// <param name="Stage">Этап завершения либо отказа.</param>
/// <param name="InitialMuMuState">Исходное наблюдение; null означает отсутствие доказательства.</param>
/// <param name="MuMuStartRequested">Запрашивался ли строгий lifecycle; факт изменения состояния сам по себе не утверждается.</param>
/// <param name="MuMuStartOutcome">Доказанный итог lifecycle либо null, если успеха запуска нет.</param>
/// <param name="AndroidPreparation">Доказанная готовность Android либо null при отказе.</param>
/// <param name="Failure">Отказ приложения либо предварительной проверки; null при полном успехе.</param>
public sealed record AzurPilotOperatorAndroidPreparationOutcome(
    MuMuInstanceId InstanceId,
    AzurPilotOperatorAndroidPreparationStage Stage,
    MuMuLifecycleState? InitialMuMuState,
    bool MuMuStartRequested,
    MuMuLifecycleOutcome? MuMuStartOutcome,
    AndroidTransportPreparationOutcome? AndroidPreparation,
    ApplicationFailure? Failure)
{
    /// <summary>Успех означает завершённую подготовку с доказанными transport, shell и boot.</summary>
    public bool IsSuccess => Stage == AzurPilotOperatorAndroidPreparationStage.Completed && Failure is null;

    /// <summary>MuMu мог остаться запущенным; отмена и отказ Android не выполняют компенсирующую остановку.</summary>
    public bool MuMuMayRemainRunning => InitialMuMuState == MuMuLifecycleState.Running || MuMuStartRequested;

    /// <summary>Новое host-side наблюдение после отказа/отмены, либо отсутствие доказательства.</summary>
    public MuMuLifecycleState? PostflightMuMuState { get; init; }
}

/// <summary>
/// Связывает пользовательские действия AzurPilot с уже существующими возможностями приложения.
/// </summary>
/// <remarks>
/// <para>
/// Сервис получает выбранный экземпляр явно и не принимает Android endpoint от слоя представления. Каждая
/// операция получает точный идентификатор MuMu, заново проверяет его через lifecycle resolver и, если действие
/// адресовано Android, разрешает endpoint только для этой пары установки и identity. Так instance и
/// endpoint нельзя смешать из пользовательского ввода.
/// </para>
/// <para>
/// Диагностика и inventory выполняют только наблюдения. Подготовка transport является отдельным явным
/// use case; захват снимка экрана связывает продуктовый захват и сканер ROI native-границы в одном ограниченном
/// времени жизни кадра.
/// </para>
/// </remarks>
public sealed partial class AzurPilotOperatorService
{
    // Устанавливается только composition root изолированного интерактивного запуска.
    // Отказ до аренды также исключает записи в пользовательский runtime-каталог.
    internal bool ExternalMutationsDisabled { get; set; }
    private MuMuInstanceId? _liveEnvironmentTarget;
    private bool _livePreparationAttempted;

    // CLI задаёт намерение, но полномочие появляется только после наблюдения через продуктовый provider.
    internal ApplicationResult<bool> InitializeLiveEnvironment(MuMuInstanceId instanceId)
    {
        _liveEnvironmentTarget = null;
        _livePreparationAttempted = false;
        if (!ExternalMutationsDisabled)
        {
            return ApplicationResult<bool>.Failure(IsolatedMutationFailure());
        }

        ApplicationResult<MuMuInstanceState> observed = ObserveMuMu(instanceId);
        if (observed.IsFailure)
        {
            return ApplicationResult<bool>.Failure(observed.FailureInfo!);
        }

        if (observed.Value!.State != MuMuLifecycleState.Stopped)
        {
            return ApplicationResult<bool>.Failure(IsolatedMutationFailure());
        }

        _liveEnvironmentTarget = instanceId;
        return ApplicationResult<bool>.Success(true);
    }

    private const string DiagnosticsActivity = "azurpilot.operator.diagnostics";
    private const string InventoryActivity = "azurpilot.operator.mumu.inventory";
    private const string MuMuLifecycleActivity = "azurpilot.operator.mumu.lifecycle";
    private const string AndroidObserveActivity = "azurpilot.operator.android.observe";
    private const string AndroidPrepareActivity = "azurpilot.operator.android.prepare";
    private const string SelectedAndroidPrepareActivity = "azurpilot.operator.android.prepare_selected";
    private const string GameObserveActivity = "azurpilot.operator.azurlane.observe";
    private const string GameLifecycleActivity = "azurpilot.operator.azurlane.lifecycle";
    private const string GameStartActivity = "azurpilot.operator.azurlane.start";
    private const string ScreenshotActivity = "azurpilot.operator.screenshot";
    private const string ResolveTargetActivity = "azurpilot.operator.target.resolve";

    private readonly AzurPilotDiagnosticService _diagnostics;
    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly IAndroidHost _androidHost;
    private readonly AndroidReadinessService _androidReadiness;
    private readonly AzurLaneGameStateService _gameState;
    private readonly AzurLaneGameLifecycleService _gameLifecycle;
    private readonly IAndroidScreenshotHost _screenshots;
    private readonly AzurPilotConfigurationSnapshot _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly Core.Game.IGameUiHost _gameUi;
    private readonly AzurPilotLogViewerSession? _logViewer;
    private readonly Core.Game.IGameUiTrace? _gameUiTrace;

    /// <summary>Создаёт сценарии оператора поверх singleton-сервисов одного хоста приложения.</summary>
    public AzurPilotOperatorService(
        AzurPilotDiagnosticService diagnostics,
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
        AndroidReadinessService androidReadiness,
        AzurLaneGameStateService gameState,
        AzurLaneGameLifecycleService gameLifecycle,
        IAndroidScreenshotHost screenshots,
        AzurPilotConfigurationSnapshot configuration,
        TimeProvider timeProvider,
        Core.Game.IGameUiHost gameUi,
        AzurPilotLogViewerSession? logViewer = null,
        Core.Game.IGameUiTrace? gameUiTrace = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(androidReadiness);
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(gameLifecycle);
        ArgumentNullException.ThrowIfNull(screenshots);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(gameUi);

        _diagnostics = diagnostics;
        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
        _androidHost = androidHost;
        _androidReadiness = androidReadiness;
        _gameState = gameState;
        _gameLifecycle = gameLifecycle;
        _screenshots = screenshots;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _gameUi = gameUi;
        _logViewer = logViewer;
        _gameUiTrace = gameUiTrace;
    }

    /// <summary>Неизменяемая конфигурация текущего хоста приложения.</summary>
    public AzurPilotConfigurationSnapshot Configuration => _configuration;

    /// <summary>Наблюдает точный MuMu без изменения состояния.</summary>
    public ApplicationResult<MuMuInstanceState> ObserveMuMu(MuMuInstanceId instanceId)
    {
        ApplicationResult<ResolvedMuMuTarget> target = ResolveMuMuTarget(instanceId);
        return target.IsFailure ? ApplicationResult<MuMuInstanceState>.Failure(target.FailureInfo!)
            : _muMuHost.ObserveInstanceState(target.Value!.Installation, instanceId);
    }

    /// <summary>Целая MuMu orchestration под общей для TUI и CLI межпроцессной арендой.</summary>
    public Task<ApplicationResult<MuMuLifecycleOutcome>> RunMuMuLifecycleAsync(
        MuMuInstanceId instanceId, MuMuLifecycleOperation operationKind, CancellationToken cancellationToken)
    {
        return WithMutationLeaseAsync(instanceId,
                async () =>
                {
                    if (ExternalMutationsDisabled && (_liveEnvironmentTarget != instanceId || !_livePreparationAttempted))
                    {
                        return ApplicationResult<MuMuLifecycleOutcome>.Failure(IsolatedMutationFailure());
                    }

                    ApplicationResult<MuMuLifecycleOutcome> result = await RunMuMuLifecycleCoreAsync(
                        instanceId, operationKind, cancellationToken).ConfigureAwait(false);
                    if (ExternalMutationsDisabled && result.IsSuccess)
                    {
                        _liveEnvironmentTarget = null;
                    }

                    return result;
                },
                operationKind == MuMuLifecycleOperation.Stop && _liveEnvironmentTarget == instanceId
                    && _livePreparationAttempted, cancellationToken);
    }

    /// <summary>Подготовка transport под арендой окружения.</summary>
    public Task<ApplicationResult<AndroidTransportPreparationOutcome>> PrepareAndroidAsync(
        MuMuInstanceId instanceId, CancellationToken cancellationToken, IProgress<AndroidReadinessStage>? progress = null,
        AndroidMutationProgress? mutationProgress = null)
    {
        return WithMutationLeaseAsync(instanceId,
                () => PrepareAndroidCoreAsync(instanceId, cancellationToken, progress, mutationProgress), cancellationToken);
    }

    /// <summary>Подготовка окружения и postflight под одной арендой, без повторного захвата во вложенных шагах.</summary>
    public async Task<AzurPilotOperatorAndroidPreparationOutcome> PrepareSelectedAndroidAsync(
        MuMuInstanceId instanceId, CancellationToken cancellationToken,
        IProgress<AzurPilotOperatorAndroidPreparationStage>? progress = null,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? onOutcome = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        if (ExternalMutationsDisabled && (_liveEnvironmentTarget != instanceId || _livePreparationAttempted))
        {
            return new(instanceId, AzurPilotOperatorAndroidPreparationStage.ResolveMuMu,
                null, false, null, null, IsolatedMutationFailure());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await PrepareSelectedAndroidCoreAsync(instanceId, cancellationToken, progress, onOutcome, preparationMutationProgress).ConfigureAwait(false);
        }

        ApplicationResult<FileStream> acquired = WindowsInstanceMutationLease.TryAcquire(instanceId);
        if (acquired.IsFailure)
        {
            return new(instanceId, AzurPilotOperatorAndroidPreparationStage.ResolveMuMu,
                null, false, null, null, acquired.FailureInfo);
        }

        using FileStream lease = acquired.Value!;
        return await PrepareSelectedAndroidUnderLeaseAsync(instanceId, cancellationToken, progress, onOutcome, preparationMutationProgress).ConfigureAwait(false);
    }

    /// <summary>Подготовка окружения и запуск игры в одной критической секции.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StartAzurLaneAsync(
        MuMuInstanceId instanceId, CancellationToken cancellationToken,
        IProgress<AzurPilotOperatorAndroidPreparationStage>? progress = null, Action? onGameStarting = null,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? onPreparationCompleted = null,
        AndroidMutationProgress? mutationProgress = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        return WithMutationLeaseAsync(instanceId,
                () => StartAzurLaneCoreAsync(instanceId, cancellationToken, progress, onGameStarting, onPreparationCompleted, mutationProgress, preparationMutationProgress), cancellationToken);
    }

    private Task<ApplicationResult<T>> WithMutationLeaseAsync<T>(
        MuMuInstanceId instanceId, Func<Task<ApplicationResult<T>>> action, CancellationToken cancellationToken)
    {
        return WithMutationLeaseAsync(instanceId, action, false, cancellationToken);
    }

    private async Task<ApplicationResult<T>> WithMutationLeaseAsync<T>(
        MuMuInstanceId instanceId, Func<Task<ApplicationResult<T>>> action, bool allowIsolatedStop,
        CancellationToken cancellationToken)
    {
        if (ExternalMutationsDisabled && !allowIsolatedStop)
        {
            return ApplicationResult<T>.Failure(IsolatedMutationFailure());
        }

        cancellationToken.ThrowIfCancellationRequested();
        ApplicationResult<FileStream> acquired = WindowsInstanceMutationLease.TryAcquire(instanceId);
        if (acquired.IsFailure)
        {
            return ApplicationResult<T>.Failure(acquired.FailureInfo!);
        }

        using FileStream lease = acquired.Value!;
        cancellationToken.ThrowIfCancellationRequested();
        return await action().ConfigureAwait(false);
    }

    private static ApplicationFailure IsolatedMutationFailure()
    {
        return new()
        {
            Code = ApplicationFailure.InvalidArgument,
            Message = "Изменение состояния запрещено. Изолированная UI-сессия требует исходный Stopped точного экземпляра; Stop разрешён только после начала подготовки в этой сессии.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ApplicationFailure.MutationAttemptedDetailKey] = "false",
            },
        };
    }

    /// <summary>Повторно открывает окно просмотра журнала без изменения состояния; отключённая настройка применяется только при запуске.</summary>
    public Task<ApplicationResult<bool>> ReopenLogViewerAsync(CancellationToken cancellationToken)
        => _logViewer?.OpenAsync(cancellationToken) ?? Task.FromResult(ApplicationResult<bool>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.LogViewerUnavailable,
            Message = "Включите отдельную лог-консоль в настройках и перезапустите приложение.",
        }));

    /// <summary>Сохраняет настройки следующего запуска; DI снимок текущей сессии не меняется.</summary>
    public ApplicationResult<AzurPilotConfigurationSnapshot> SaveConfiguration(
        AzurPilotConfigurationSnapshot expected, AzurPilotConfiguration configuration, out string? backupPath)
    {
        string defaultYamlPath = AzurPilotConfigurationPath.GetDefaultRuntimePath();
        string defaultJsonPath = AzurPilotConfigurationPath.GetLegacyJsonRuntimePath();
        string configurationDirectory = _configuration.FilePath is string source
            ? Path.GetDirectoryName(source)!
            : Path.GetDirectoryName(defaultYamlPath)!;
        string yamlPath = Path.Combine(configurationDirectory, Path.GetFileName(defaultYamlPath));
        string jsonPath = Path.Combine(configurationDirectory, Path.GetFileName(defaultJsonPath));
        if (ExternalMutationsDisabled)
        {
            ApplicationResult validation = WindowsConfigurationIsolation.TryValidate(_configuration.FilePath);
            if (validation.IsFailure)
            {
                backupPath = null;
                return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(validation.FailureInfo!);
            }
        }

        return AzurPilotConfigurationStore.Save(yamlPath, jsonPath, expected, configuration, out backupPath);
    }

    /// <summary>Повторно собирает снимок диагностики без изменения состояния.</summary>
    /// <returns>Фактически наблюдённое состояние приложения, конфигурации, native, MuMu, Android и игры.</returns>
    public AzurPilotDiagnosticReport RefreshDiagnostics()
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(DiagnosticsActivity);
        using (operation.StartStep(AzurPilotOperation.NativeDiagnosticsActivityName))
        {
            return _diagnostics.Capture();
        }
    }

    /// <summary>Обнаруживает установку и список экземпляров MuMu, затем отдельно наблюдает каждый точный экземпляр.</summary>
    /// <returns>Список с состояниями либо отказ обнаружения или перечисления.</returns>
    public ApplicationResult<AzurPilotOperatorMuMuInventory> DiscoverMuMuInstances(
        int maximumInstances = int.MaxValue, CancellationToken cancellationToken = default)
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

        if (enumeration.Value!.Count > maximumInstances)
        {
            return ApplicationResult<AzurPilotOperatorMuMuInventory>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.AgentOutputLimitExceeded,
                Message = "Inventory превышает предел наблюдений за один вызов.",
            });
        }

        List<AzurPilotOperatorMuMuInstance> instances = new(enumeration.Value!.Count);
        foreach (MuMuInstance instance in enumeration.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResult<MuMuInstanceState> observation = _muMuHost.ObserveInstanceState(value, instance.Id);
            instances.Add(observation.IsSuccess
                ? new AzurPilotOperatorMuMuInstance(instance, observation.Value, Failure: null)
                : new AzurPilotOperatorMuMuInstance(instance, Observation: null, observation.FailureInfo));
        }

        return ApplicationResult<AzurPilotOperatorMuMuInventory>.Success(
            new AzurPilotOperatorMuMuInventory(value.Version, Array.AsReadOnly(instances.ToArray())));
    }

    /// <summary>
    /// Разрешает единственный целевой экземпляр по правилам выбора, не изменяя состояние.
    /// </summary>
    /// <param name="selection">Семантика выбора: автоматический либо явный по stable identity.</param>
    /// <returns>Подтверждённый идентификатор экземпляра либо отказ его определения.</returns>
    /// <remarks>
    /// <para>
    /// Это read-only шаг пользовательского действия «Запустить окружение»: он позволяет предъявить
    /// оператору точную identity до подтверждения, не требуя ручного <c>/instances</c> и <c>/select</c>.
    /// </para>
    /// <para>
    /// Автоматический выбор не является выбором «наиболее вероятного»: при нуле экземпляров он даёт
    /// <see cref="ApplicationFailure.MuMuInstanceNotFound"/>, при двух и более —
    /// <see cref="ApplicationFailure.MuMuInstanceAmbiguous"/>. Первый, последний и vmindex <c>0</c> не
    /// выбираются никогда, поэтому неоднозначный target не доходит до изменения состояния.
    /// </para>
    /// </remarks>
    public ApplicationResult<MuMuInstanceId> ResolveTarget(MuMuInstanceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        using AzurPilotOperation operation = AzurPilotOperation.Start(ResolveTargetActivity);
        using Activity? step = operation.StartStep("azurpilot.operator.target.resolve");

        ApplicationResult<MuMuInstallation> installation = _muMuHost.DiscoverInstallation();
        if (installation.IsFailure)
        {
            return ApplicationResult<MuMuInstanceId>.Failure(installation.FailureInfo!);
        }

        MuMuInstanceResolution resolution = _muMuLifecycle.Resolve(installation.Value!, selection);
        return resolution.IsFailed
            ? ApplicationResult<MuMuInstanceId>.Failure(resolution.Failure!)
            : ApplicationResult<MuMuInstanceId>.Success(resolution.Instance!.Id);
    }

    /// <summary>Выполняет start, stop или restart для точного экземпляра MuMu с проверкой результата.</summary>
    private async Task<ApplicationResult<MuMuLifecycleOutcome>> RunMuMuLifecycleCoreAsync(
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
                return ApplicationResult<MuMuLifecycleOutcome>.Failure(
                    WithMutationAttempted(target.FailureInfo!, attempted: false));
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

    /// <summary>Наблюдает транспорт и готовность Android точного экземпляра MuMu без подключения.</summary>
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
    private async Task<ApplicationResult<AndroidTransportPreparationOutcome>> PrepareAndroidCoreAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        IProgress<AndroidReadinessStage>? progress = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using AzurPilotOperation operation = AzurPilotOperation.Start(AndroidPrepareActivity);
        using (operation.StartStep("azurpilot.operator.android.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.IsFailure)
            {
                return ApplicationResult<AndroidTransportPreparationOutcome>.Failure(target.FailureInfo!);
            }

            using (operation.StartStep("azurpilot.operator.android.prepare"))
            {
                return await _androidReadiness.PrepareTransportAsync(
                    target.Value!.Endpoint,
                    cancellationToken, progress, preparationMutationProgress).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Явно подготавливает Android: наблюдает точный MuMu, при доказанном Stopped запускает и доказывает готовность.</summary>
    /// <remarks>
    /// Lifecycle и readiness владеют своими deadline; новый retry или deadline не вводится. Отказ
    /// сохраняет частичный результат и не останавливает MuMu. Игра этим действием не запускается.
    /// </remarks>
    private async Task<AzurPilotOperatorAndroidPreparationOutcome> PrepareSelectedAndroidUnderLeaseAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        IProgress<AzurPilotOperatorAndroidPreparationStage>? progress = null,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? onOutcome = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        AzurPilotOperatorAndroidPreparationOutcome outcome = await PrepareSelectedAndroidCoreAsync(
            instanceId, cancellationToken, progress, onOutcome, preparationMutationProgress).ConfigureAwait(false);
        if (!outcome.IsSuccess && outcome.MuMuMayRemainRunning)
        {
            // Отмена ожидания не доказывает остановку MuMu. Новое read-only наблюдение не использует
            // отменённый token и не выполняет компенсирующее изменение состояния.
            ApplicationResult<ResolvedMuMuTarget> resolved = ResolveMuMuTarget(instanceId);
            if (resolved.IsSuccess)
            {
                ApplicationResult<MuMuInstanceState> observed = _muMuHost.ObserveInstanceState(
                    resolved.Value!.Installation, instanceId);
                outcome = outcome with { PostflightMuMuState = observed.IsSuccess ? observed.Value!.State : null };
            }
        }

        onOutcome?.Invoke(outcome);
        return outcome;
    }

    private async Task<AzurPilotOperatorAndroidPreparationOutcome> PrepareSelectedAndroidCoreAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        IProgress<AzurPilotOperatorAndroidPreparationStage>? progress,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? onOutcome = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(SelectedAndroidPrepareActivity);
        AzurPilotOperatorAndroidPreparationOutcome outcome = new(
            instanceId, AzurPilotOperatorAndroidPreparationStage.ResolveMuMu, null, false, null, null, null);

        try
        {
            progress?.Report(outcome.Stage);
            onOutcome?.Invoke(outcome);
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResult<ResolvedMuMuTarget> resolved = ResolveMuMuTarget(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved.IsFailure)
            {
                return outcome with { Failure = resolved.FailureInfo! };
            }

            ResolvedMuMuTarget target = resolved.Value!;
            outcome = outcome with { Stage = AzurPilotOperatorAndroidPreparationStage.ObserveMuMu };
            progress?.Report(outcome.Stage);
            ApplicationResult<MuMuInstanceState> observed = _muMuHost.ObserveInstanceState(
                target.Installation, target.Instance.Id);
            cancellationToken.ThrowIfCancellationRequested();
            if (observed.IsFailure)
            {
                return outcome with { Failure = observed.FailureInfo! };
            }

            MuMuLifecycleState initial = observed.Value!.State;
            outcome = outcome with { InitialMuMuState = initial };
            // Повторная проверка под арендой закрывает смену состояния после начального наблюдения.
            // Посторонние средства управления MuMu аренду не соблюдают; абсолютной атомарности нет.
            if (ExternalMutationsDisabled && (_liveEnvironmentTarget != instanceId
                || _livePreparationAttempted || initial != MuMuLifecycleState.Stopped))
            {
                _liveEnvironmentTarget = null;
                return outcome with { Failure = IsolatedMutationFailure() };
            }

            if (initial is not (MuMuLifecycleState.Running or MuMuLifecycleState.Stopped))
            {
                return outcome with
                {
                    Failure = MuMuFailures.StateUnprovenBeforeOperatorPreparation(instanceId, initial),
                };
            }

            if (initial == MuMuLifecycleState.Stopped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _livePreparationAttempted = ExternalMutationsDisabled;
                outcome = outcome with
                {
                    Stage = AzurPilotOperatorAndroidPreparationStage.StartMuMu,
                    MuMuStartRequested = true,
                };
                progress?.Report(outcome.Stage);
                onOutcome?.Invoke(outcome);
                ApplicationResult<MuMuLifecycleOutcome> started = await _muMuLifecycle.StartFromKnownStateAsync(
                    target.Installation, target.Instance, cancellationToken).ConfigureAwait(false);
                if (started.IsFailure)
                {
                    return outcome with { Failure = started.FailureInfo! };
                }

                outcome = outcome with { MuMuStartOutcome = started.Value! };
                onOutcome?.Invoke(outcome);
            }

            cancellationToken.ThrowIfCancellationRequested();
            outcome = outcome with { Stage = AzurPilotOperatorAndroidPreparationStage.PrepareAndroid };
            progress?.Report(outcome.Stage);
            onOutcome?.Invoke(outcome);
            ApplicationResult<AndroidTransportPreparationOutcome> prepared = await PrepareAndroidCoreAsync(
                instanceId, cancellationToken,
                new SynchronousProgress<AndroidReadinessStage>(stage =>
                {
                    outcome = outcome with
                    {
                        Stage = stage == AndroidReadinessStage.Boot
                            ? AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot
                            : AzurPilotOperatorAndroidPreparationStage.PrepareAndroid,
                    };
                    progress?.Report(outcome.Stage);
                }), preparationMutationProgress).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return outcome with { Failure = prepared.FailureInfo! };
            }

            AndroidTransportPreparationOutcome readiness = prepared.Value!;
            outcome = outcome with { AndroidPreparation = readiness };
            onOutcome?.Invoke(outcome);
            cancellationToken.ThrowIfCancellationRequested();
            if (readiness.FinalTransport != AndroidTransportState.Device
                || !readiness.Boot.ShellAvailable || readiness.Boot.BootCompleted != 1)
            {
                throw new InvalidOperationException("Подготовка Android вернула успех без доказанного postcondition.");
            }

            return outcome with { Stage = AzurPilotOperatorAndroidPreparationStage.Completed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return outcome with
            {
                Failure = new ApplicationFailure
                {
                    Code = ApplicationFailure.OperationCancelled,
                    Message = "Подготовка Android отменена. Запущенный MuMu автоматически не останавливается.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["instance_id"] = instanceId.ToString(),
                        ["phase"] = outcome.Stage.ToString(),
                    },
                },
            };
        }
    }

    /// <summary>Наблюдает Azur Lane Global/EN на endpoint-е, разрешённом для точного идентификатора MuMu.</summary>
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
    private async Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StartAzurLaneCoreAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        IProgress<AzurPilotOperatorAndroidPreparationStage>? progress = null,
        Action? onGameStarting = null,
        Action<AzurPilotOperatorAndroidPreparationOutcome>? onPreparationCompleted = null,
        AndroidMutationProgress? mutationProgress = null,
        AndroidMutationProgress? preparationMutationProgress = null)
    {
        using AzurPilotOperation operation = AzurPilotOperation.Start(GameStartActivity);
        AzurPilotOperatorAndroidPreparationOutcome preparation = await PrepareSelectedAndroidUnderLeaseAsync(
            instanceId, cancellationToken, progress, onPreparationCompleted, preparationMutationProgress).ConfigureAwait(false);
        if (!preparation.IsSuccess)
        {
            return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(preparation.Failure! with
            {
                Message = preparation.Failure!.Message + " Игра не запускалась. "
                    + (preparation.PostflightMuMuState switch
                    {
                        MuMuLifecycleState.Running => "Новое наблюдение: MuMu продолжает работать.",
                        MuMuLifecycleState.Stopped => "Новое наблюдение: MuMu остановлен.",
                        MuMuLifecycleState.Unknown when !preparation.MuMuMayRemainRunning =>
                            "MuMu этим действием не менялся.",
                        MuMuLifecycleState.Unknown => "Новое наблюдение не доказало состояние MuMu.",
                        null when !preparation.MuMuMayRemainRunning => "MuMu этим действием не менялся.",
                        null => "После отказа состояние MuMu не доказано наблюдением.",
                        _ => "После отказа состояние MuMu не доказано наблюдением.",
                    }),
            });
        }

        onGameStarting?.Invoke();
        return await RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Start, mutationProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Останавливает Azur Lane Global/EN и возвращает доказанный итог существующей orchestration.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> StopAzurLaneAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        AndroidMutationProgress? mutationProgress = null)
    {
        return WithMutationLeaseAsync(instanceId, () => RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Stop, mutationProgress, cancellationToken), cancellationToken);
    }

    /// <summary>Перезапускает Azur Lane Global/EN и возвращает доказанный итог существующей orchestration.</summary>
    public Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RestartAzurLaneAsync(
        MuMuInstanceId instanceId,
        CancellationToken cancellationToken,
        AndroidMutationProgress? mutationProgress = null)
    {
        return WithMutationLeaseAsync(instanceId, () => RunAzurLaneLifecycleAsync(instanceId, AzurLaneAction.Restart, mutationProgress, cancellationToken), cancellationToken);
    }

    /// <summary>Захватывает screenshot точного целевого экземпляра и анализирует FullFrame и Center native ROI.</summary>
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
        AndroidMutationProgress? mutationProgress,
        CancellationToken cancellationToken)
    {
        mutationProgress ??= new();
        using AzurPilotOperation operation = AzurPilotOperation.Start(GameLifecycleActivity);
        using (operation.StartStep("azurpilot.operator.azurlane.resolve"))
        {
            ApplicationResult<ResolvedAndroidTarget> target = ResolveAndroidTarget(instanceId);
            if (target.IsFailure)
            {
                return ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(
                    WithMutationAttempted(target.FailureInfo!, attempted: false));
            }

            using (operation.StartStep("azurpilot.operator.azurlane.execute"))
            {
                ApplicationResult<AzurLaneGameLifecycleOutcome> result = action switch
                {
                    AzurLaneAction.Start => await _gameLifecycle.StartAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken, mutationProgress).ConfigureAwait(false),
                    AzurLaneAction.Stop => await _gameLifecycle.StopAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken, mutationProgress).ConfigureAwait(false),
                    AzurLaneAction.Restart => await _gameLifecycle.RestartAsync(
                        target.Value!.Installation, target.Value.Endpoint, cancellationToken, mutationProgress).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестная операция Azur Lane."),
                };
                return result.IsFailure
                    ? ApplicationResult<AzurLaneGameLifecycleOutcome>.Failure(
                        WithMutationAttempted(result.FailureInfo!, mutationProgress.MutationAttempted))
                    : result;
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

    private static ApplicationFailure WithMutationAttempted(ApplicationFailure failure, bool attempted)
    {
        Dictionary<string, string> details = failure.Details is null
            ? new(StringComparer.Ordinal)
            : new(failure.Details, StringComparer.Ordinal);
        details[ApplicationFailure.MutationAttemptedDetailKey] = attempted ? "true" : "false";
        return failure with { Details = details };
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
