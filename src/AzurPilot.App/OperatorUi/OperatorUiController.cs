using System.Globalization;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App.OperatorUi;

/// <summary>Проверяемый без renderer coordinator операторских действий и production service results.</summary>
/// <remarks>Session обновляется только вызывающей стороной; завершённый task применяется через CompleteOperation.</remarks>
public sealed class OperatorUiController : IDisposable
{
    private readonly AzurPilotOperatorService _service;
    private CancellationTokenSource? _cancellation;
    private Task<OperationPresentation>? _activeTask;
    private bool _disposed;

    /// <summary>Создаёт coordinator с существующим startup snapshot.</summary>
    /// <param name="service">Production use cases.</param>
    /// <param name="initialDiagnostics">Уже собранная диагностика.</param>
    public OperatorUiController(AzurPilotOperatorService service, AzurPilotDiagnosticReport initialDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(initialDiagnostics);
        _service = service;
        Session.AddMessage("AzurPilot", "Введите / для меню действий. Выберите конкретный экземпляр через /instances.");
        OperationPresentation diagnostic = DescribeDiagnostics(initialDiagnostics);
        Session.AddMessage("Диагностика запуска", diagnostic.Summary, diagnostic.Kind, diagnostic.Details);
    }

    /// <summary>Чистое presentation-состояние с operation и confirmation gates.</summary>
    public OperatorUiSession Session { get; } = new();

    /// <summary>Последний фактически полученный inventory либо отсутствие observation.</summary>
    public AzurPilotOperatorMuMuInventory? Inventory { get; private set; }

    /// <summary>Краткий статус текущей либо последней операции.</summary>
    public string OperationText { get; private set; } = "Операция: нет";

    /// <summary>Task завершён и результат готов к применению к Session.</summary>
    public bool IsOperationCompleted => _activeTask?.IsCompleted == true;

    /// <summary>Запускает service use case только после успешного gate.</summary>
    /// <param name="action">Service action.</param>
    /// <returns>Операция получила единственный execution slot.</returns>
    public bool TryStartOperation(OperatorUiAction action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Session.TryBeginOperation(action, out MuMuInstanceId? target))
        {
            Session.AddMessage("Действие недоступно", Session.IsOperationActive
                ? "Уже выполняется операция. /cancel — отмена; повтор доступен после завершения."
                : "Нужен explicit target и, для изменения состояния, отдельное подтверждение.",
                OperatorUiMessageKind.Warning);
            return false;
        }

        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        string title = OperatorUiCommand.For(action).Title;
        OperationText = $"Выполняется: {title} · {target?.ToString() ?? "приложение"} · F4 отмена";
        Session.AddMessage("Начало операции", title + (target is null ? string.Empty : $" · {target}"));
        _activeTask = Task.Run(() => ExecuteOperationAsync(action, target, token), token);
        return true;
    }

    /// <summary>Передаёт cancellation, сохраняя execution slot до завершения task.</summary>
    public void CancelOperation()
    {
        if (_cancellation is null)
        {
            Session.AddMessage("Отмена", "Нет действующей операции.");
        }
        else if (!_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            OperationText = "Запрошена отмена; ожидается завершение операции.";
            Session.AddMessage("Отмена", "Запрос передан операции. Следующая операция доступна после её завершения.");
        }
    }

    /// <summary>Дожидается operation task без terminal dependency; результат применяет CompleteOperation.</summary>
    /// <returns>Task завершения; отказ/отмена представлены затем в Session.</returns>
    public async Task WaitForOperationAsync()
    {
        if (_activeTask is null)
        {
            return;
        }

        try
        {
            _ = await _activeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // CompleteOperation сохраняет cancellation как presentation result.
        }
        catch (Exception)
        {
            // CompleteOperation сохраняет unexpected failure, не оставляя task ненаблюдённым.
        }
    }

    /// <summary>Применяет только завершённый service result и освобождает operation slot.</summary>
    /// <returns>Обновился ли фактически наблюдённый inventory.</returns>
    public bool CompleteOperation()
    {
        if (_activeTask?.IsCompleted != true)
        {
            return false;
        }

        bool inventoryChanged = false;
        try
        {
            OperationPresentation result = _activeTask.GetAwaiter().GetResult();
            OperationText = result.Cancelled ? "Операция: отменена"
                : result.Success ? "Операция: завершена" : "Операция: отказ";
            string title = result.Cancelled ? "Отмена" : result.Kind switch
            {
                OperatorUiMessageKind.Success => "Готово",
                OperatorUiMessageKind.Warning => "Требует внимания",
                OperatorUiMessageKind.Error => "Отказ",
                OperatorUiMessageKind.Information => "Результат",
                _ => "Результат",
            };
            Session.AddMessage(title, result.Summary, result.Kind, result.Details);
            if (result.Inventory is not null)
            {
                Inventory = result.Inventory;
                inventoryChanged = true;
                Session.CompleteOperation();
                if (Session.SelectedInstance is MuMuInstanceId selected
                    && !Inventory.Instances.Any(instance => instance.Instance.Id == selected))
                {
                    _ = Session.SelectInstance(null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            OperationText = "Операция: отменена";
            Session.AddMessage("Отмена", "Операция завершила обработку отмены.", OperatorUiMessageKind.Warning);
        }
        catch (Exception exception)
        {
            OperationText = "Операция: ошибка";
            Session.AddMessage("Ошибка", "Неожиданная ошибка операции. Проверьте технические детали.",
                OperatorUiMessageKind.Error,
                $"Тип: {exception.GetType().FullName}\nСообщение: {exception.Message}");
        }
        finally
        {
            Session.CompleteOperation();
            _activeTask = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }

        return inventoryChanged;
    }

    /// <summary>Отменяет и дожидается операции, затем освобождает её cancellation owner.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        WaitForOperationAsync().GetAwaiter().GetResult();
        _ = CompleteOperation();
        _cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
    private async Task<OperationPresentation> ExecuteOperationAsync(
        OperatorUiAction action, MuMuInstanceId? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case OperatorUiAction.Diagnostics:
                return DescribeDiagnostics(_service.RefreshDiagnostics());
            case OperatorUiAction.Instances:
                ApplicationResult<AzurPilotOperatorMuMuInventory> inventory = _service.DiscoverMuMuInstances();
                if (inventory.IsFailure)
                {
                    return Failure(inventory.FailureInfo!);
                }

                AzurPilotOperatorMuMuInventory found = inventory.Value!;
                return new OperationPresentation(
                    true,
                    $"MuMu {found.InstallationVersion}: найдено экземпляров — {found.Instances.Count}. Выберите точный ID в списке.",
                    OperatorUiMessageKind.Success,
                    string.Join('\n', found.Instances.Select(DescribeInstance)),
                    found);
            case OperatorUiAction.MuMuStart:
            case OperatorUiAction.MuMuStop:
            case OperatorUiAction.MuMuRestart:
                MuMuLifecycleOperation operation = action == OperatorUiAction.MuMuStart
                    ? MuMuLifecycleOperation.Start
                    : action == OperatorUiAction.MuMuStop
                        ? MuMuLifecycleOperation.Stop
                        : MuMuLifecycleOperation.Restart;
                return Present(await _service.RunMuMuLifecycleAsync(target!.Value, operation, cancellationToken)
                    .ConfigureAwait(false), outcome =>
                    (
                        $"MuMu {target}: {DescribeAction(action)} завершено. "
                        + $"Состояние: {DescribeMuMuState(outcome.InitialState)} → {DescribeMuMuState(outcome.FinalState)}.",
                        $"Действие: {action}\nИсходное состояние: {outcome.InitialState}\n"
                        + $"Итоговое состояние: {outcome.FinalState}\nEvidence: {outcome.Evidence}"
                    ));
            case OperatorUiAction.AndroidObserve:
                return Present(_service.ObserveAndroid(target!.Value), facts =>
                    (
                        DescribeAndroidObservation(target.Value, facts.Transport.State, facts.Boot?.ShellAvailable,
                            facts.Boot?.BootCompleted),
                        $"Endpoint: {facts.Endpoint}\nTransport: {facts.Transport.State}\n"
                        + $"Shell: {Fact(facts.Boot?.ShellAvailable)}\n"
                        + $"boot_completed: {facts.Boot?.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                        + $"Android release: {facts.Boot?.AndroidRelease ?? "не наблюдалась"}\nEvidence: {facts.Evidence}"
                    ));
            case OperatorUiAction.AndroidPrepare:
                return Present(await _service.PrepareAndroidAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    outcome =>
                    (
                        $"Android готов для {target}: transport, shell и загрузка Android проверены.",
                        $"Endpoint: {outcome.Endpoint}\nTransport: {outcome.InitialTransportName} → {outcome.FinalTransportName}\n"
                        + $"Попытка connect: {Fact(outcome.ConnectAttempted)}\n"
                        + $"Shell: {Fact(outcome.Boot.ShellAvailable)}\n"
                        + $"boot_completed: {outcome.Boot.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                        + $"Evidence: {outcome.Evidence}"
                    ));
            case OperatorUiAction.EnvironmentPrepare:
                return DescribeEnvironmentPreparation(await _service.PrepareSelectedAndroidAsync(
                    target!.Value, cancellationToken).ConfigureAwait(false));
            case OperatorUiAction.GameObserve:
                return Present(_service.ObserveAzurLane(target!.Value), observation =>
                    (
                        $"Azur Lane Global/EN · {target}: {DescribeGameState(observation.State)}. "
                        + $"Установлена: {Fact(observation.Facts.Installed)}; процесс: {Fact(observation.Facts.ProcessRunning)}; "
                        + $"на переднем плане: {Fact(observation.Facts.Foreground)}.",
                        $"State: {observation.State}\n"
                        + $"Installed: {Fact(observation.Facts.Installed)}\nProcess: {Fact(observation.Facts.ProcessRunning)}\n"
                        + $"Foreground: {Fact(observation.Facts.Foreground)}\n"
                        + $"Evidence: {observation.Evidence}"
                    ));
            case OperatorUiAction.GameStart:
            case OperatorUiAction.GameStop:
            case OperatorUiAction.GameRestart:
                ApplicationResult<AzurLaneGameLifecycleOutcome> game;
                if (action == OperatorUiAction.GameStart)
                {
                    game = await _service.StartAzurLaneAsync(target!.Value, cancellationToken).ConfigureAwait(false);
                }
                else if (action == OperatorUiAction.GameStop)
                {
                    game = await _service.StopAzurLaneAsync(target!.Value, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    game = await _service.RestartAzurLaneAsync(target!.Value, cancellationToken).ConfigureAwait(false);
                }

                return Present(game, outcome =>
                    (
                        $"Azur Lane Global/EN · {target}: {DescribeAction(action)} завершено. "
                        + $"Состояние: {DescribeGameState(outcome.InitialState)} → {DescribeGameState(outcome.FinalState)}.",
                        $"Package: {outcome.Package}\nДействие: {action}\nИсходное состояние: {outcome.InitialState}\n"
                        + $"Итоговое состояние: {outcome.FinalState}\nEvidence: {outcome.Evidence}"
                    ));
            case OperatorUiAction.Screenshot:
                return Present(await _service.CaptureAndScanAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    analysis =>
                    (
                        $"Снимок MuMu {analysis.Capture.InstanceId} получен; FullFrame и Center проверены.",
                        $"Endpoint: {analysis.Capture.Endpoint}\nPNG: {analysis.Capture.EncodedPngByteCount} bytes\n"
                        + $"Время захвата UTC: {analysis.Capture.CapturedAtUtc:O}\n"
                        + string.Join('\n', analysis.Regions.Select(DescribeRegion))
                    ));
            case OperatorUiAction.SelectInstance:
            case OperatorUiAction.Cancel:
            case OperatorUiAction.Logs:
            case OperatorUiAction.Details:
            case OperatorUiAction.Help:
            case OperatorUiAction.Exit:
                throw new InvalidOperationException("Действие не является service operation.");
            default:
                throw new InvalidOperationException("Неизвестное действие операторской поверхности.");
        }
    }

    private static OperationPresentation Present<T>(
        ApplicationResult<T> result,
        Func<T, (string Summary, string Details)> describe)
        where T : notnull
    {
        if (result.IsFailure)
        {
            return Failure(result.FailureInfo!);
        }

        (string summary, string details) = describe(result.Value!);
        return new OperationPresentation(true, summary, OperatorUiMessageKind.Success, details);
    }

    private static OperationPresentation DescribeEnvironmentPreparation(
        AzurPilotOperatorAndroidPreparationOutcome outcome)
    {
        string initialState = outcome.InitialMuMuState is MuMuLifecycleState state
            ? DescribeMuMuState(state)
            : "не доказано";
        string muMuSummary = outcome.MuMuStartRequested
            ? outcome.MuMuStartOutcome is MuMuLifecycleOutcome start
                ? $"MuMu запущен из состояния «остановлен»; итог — {DescribeMuMuState(start.FinalState)}."
                : "Запуск MuMu был запрошен; итог запуска не доказан."
            : outcome.InitialMuMuState == MuMuLifecycleState.Running
                ? "MuMu уже работал; повторный запуск не выполнялся."
                : $"MuMu не запускался; наблюдённое состояние: {initialState}.";

        if (outcome.IsSuccess)
        {
            AndroidTransportPreparationOutcome readiness = outcome.AndroidPreparation!;
            string summary = $"Android для {outcome.InstanceId} готов: transport, shell и загрузка проверены. "
                + $"{muMuSummary} Azur Lane не запускалась.";
            string details = $"Target: {outcome.InstanceId}\nЭтап: {outcome.Stage}\n"
                + $"Исходное состояние MuMu: {outcome.InitialMuMuState}\n{muMuSummary}\n"
                + $"Endpoint: {readiness.Endpoint}\nTransport: {readiness.InitialTransportName} → {readiness.FinalTransportName}\n"
                + $"Попытка connect: {Fact(readiness.ConnectAttempted)}\n"
                + $"Shell: {Fact(readiness.Boot.ShellAvailable)}\n"
                + $"boot_completed: {readiness.Boot.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                + $"Evidence: {readiness.Evidence}\nAzur Lane: запуск не выполнялся.";
            return new OperationPresentation(true, summary, OperatorUiMessageKind.Success, details);
        }

        ApplicationFailure? failure = outcome.Failure;
        string reason = failure?.Message ?? "Подготовка не подтвердила готовность Android.";
        string partialState = outcome.MuMuMayRemainRunning
            ? "MuMu может остаться запущенным; автоматическая остановка не выполняется."
            : "Запуск MuMu не подтверждён.";
        string failedSummary = $"Подготовка {outcome.InstanceId} остановлена на этапе "
            + $"{DescribePreparationStage(outcome.Stage)}: {reason} {partialState} Azur Lane не запускалась.";
        string failedDetails = $"Target: {outcome.InstanceId}\nЭтап: {outcome.Stage}\n"
            + $"Исходное состояние MuMu: {outcome.InitialMuMuState?.ToString() ?? "не наблюдалось"}\n"
            + $"Запрос запуска MuMu: {Fact(outcome.MuMuStartRequested)}\n"
            + $"Итог MuMu: {outcome.MuMuStartOutcome?.FinalState.ToString() ?? "не доказан"}\n"
            + $"MuMu может остаться запущенным: {Fact(outcome.MuMuMayRemainRunning)}\n"
            + (failure is null ? "Код отказа: не предоставлен." : FormatFailureDetails(failure))
            + (outcome.AndroidPreparation is null ? string.Empty
                : $"\nEndpoint: {outcome.AndroidPreparation.Endpoint}\n"
                    + $"Transport: {outcome.AndroidPreparation.InitialTransportName} → "
                    + $"{outcome.AndroidPreparation.FinalTransportName}\n"
                    + $"Evidence: {outcome.AndroidPreparation.Evidence}")
            + "\nAzur Lane: запуск не выполнялся.";
        OperatorUiMessageKind kind = failure?.Code is ApplicationFailure.OperationCancelled
            or ApplicationFailure.MuMuStateUnproven
            ? OperatorUiMessageKind.Warning
            : OperatorUiMessageKind.Error;
        return new OperationPresentation(
            false,
            failedSummary,
            kind,
            failedDetails,
            Cancelled: failure?.Code == ApplicationFailure.OperationCancelled);
    }

    private static string DescribePreparationStage(AzurPilotOperatorAndroidPreparationStage stage) => stage switch
    {
        AzurPilotOperatorAndroidPreparationStage.ResolveMuMu => "разрешения выбранного MuMu",
        AzurPilotOperatorAndroidPreparationStage.ObserveMuMu => "наблюдения MuMu",
        AzurPilotOperatorAndroidPreparationStage.StartMuMu => "запуска MuMu",
        AzurPilotOperatorAndroidPreparationStage.PrepareAndroid => "подготовки Android",
        AzurPilotOperatorAndroidPreparationStage.Completed => "проверки готовности",
        _ => "неизвестном этапе",
    };

    private static OperationPresentation Failure(ApplicationFailure failure)
    {
        bool cancelled = failure.Code == ApplicationFailure.OperationCancelled;
        string summary = cancelled
            ? $"Операция отменена: {failure.Message}\n"
                + "Следующий шаг: проверьте завершённые этапы через /diagnostics и откройте /details."
            : $"Причина отказа: {failure.Message}\n"
                + (failure.IsRetryable
                    ? "Следующий шаг: устраните указанную причину и повторите действие."
                    : "Следующий шаг: проверьте состояние через /diagnostics и откройте /details.");
        return new OperationPresentation(
            false,
            summary,
            cancelled ? OperatorUiMessageKind.Warning : OperatorUiMessageKind.Error,
            $"Код: {failure.Code}\nПовтор имеет смысл: {Fact(failure.IsRetryable)}"
            + (failure.Details is null ? string.Empty : "\n" + string.Join('\n', failure.Details.Take(16)
                .Select(pair => $"{pair.Key}: {OperatorUiSession.CleanText(pair.Value, 256)}"))),
            Cancelled: cancelled);
    }

    private static string Fact(bool? value) => value.HasValue ? (value.Value ? "да" : "нет") : "не наблюдалось";

    private static OperationPresentation DescribeDiagnostics(AzurPilotDiagnosticReport report)
    {
        bool androidReady = IsAndroidReady(report.Android);
        bool warning = report.Native.Failure is not null || report.MuMu.Failure is not null
            || report.Android.Failure is not null || report.AzurLane.Failure is not null || !androidReady;
        string summary = "Приложение запущено. Состояние среды:\n"
            + $"MuMu — {DescribeMuMuDiagnostics(report.MuMu)}; Android — {DescribeAndroidDiagnostics(report.Android)};\n"
            + $"Azur Lane Global/EN — {DescribeGameDiagnostics(report.AzurLane)}.";
        string nextStep = report.MuMu.SelectedInstanceId is null
            ? "Следующий шаг: выберите точный экземпляр через /instances."
            : !androidReady
                ? "Следующий шаг: проверьте /android; подготовка доступна отдельной подтверждаемой командой /environment-prepare."
                : "Следующий шаг: используйте /android или /game для повторного наблюдения.";
        string fullDetails = $"Сборка: {report.Application.AssemblyName} {report.Application.AssemblyVersion}\n"
            + $"Runtime: {report.Application.RuntimeIdentifier}; OS: {report.Application.OperatingSystem}; "
            + $"архитектура процесса: {report.Application.ProcessArchitecture}\n"
            + $"Конфигурация: источник {report.Configuration.Source}; validation {report.Configuration.ValidationStatus}; "
            + $"schema {report.Configuration.SchemaVersion}; исходная schema {report.Configuration.SourceSchemaVersion}\n"
            + $"Native: доступна {Fact(report.Native.IsAvailable)}; ABI совместим {Fact(report.Native.IsAbiCompatible)}; "
            + $"ABI {report.Native.AbiVersion?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалась"}; "
            + $"OpenCV {report.Native.OpencvVersion?.ToString() ?? "не наблюдалась"}\n"
            + $"MuMu: обнаружена {Fact(report.MuMu.IsInstallationDiscovered)}; версия {report.MuMu.Version ?? "не наблюдалась"}; "
            + $"control surface {report.MuMu.ControlSurfaceStatus}; configured instance {report.MuMu.ConfiguredInstance}; "
            + $"selected instance {report.MuMu.SelectedInstanceId ?? "не выбран"}; "
            + $"display name {report.MuMu.SelectedInstanceDisplayName ?? "не наблюдалось"}; "
            + $"состояние {report.MuMu.LifecycleState?.ToString() ?? "не наблюдалось"}; evidence {report.MuMu.Evidence ?? "не наблюдались"}\n"
            + $"Android: ADB {Fact(report.Android.IsAdbAvailable)}; endpoint {report.Android.Endpoint ?? "не разрешён"}; "
            + $"stage {report.Android.Stage}; transport {report.Android.TransportState?.ToString() ?? "не наблюдалось"}; "
            + $"shell {Fact(report.Android.IsShellAvailable)}; boot_completed "
            + $"{report.Android.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}; "
            + $"Android release {report.Android.AndroidRelease ?? "не наблюдалась"}; SDK {report.Android.SdkLevel?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдался"}; "
            + $"evidence {report.Android.Evidence ?? "не наблюдались"}\n"
            + $"Azur Lane Global/EN: package {report.AzurLane.Package}; состояние {report.AzurLane.State?.ToString() ?? "не наблюдалось"}; "
            + $"установлена {Fact(report.AzurLane.IsInstalled)}; процесс {Fact(report.AzurLane.IsProcessRunning)}; "
            + $"передний план {Fact(report.AzurLane.IsForeground)}; evidence {report.AzurLane.Evidence ?? "не наблюдались"}\n"
            + string.Join('\n', new[] { report.Native.Failure, report.MuMu.Failure, report.Android.Failure, report.AzurLane.Failure }
                .OfType<ApplicationFailure>().Select(FormatFailureDetails));
        return new OperationPresentation(true, warning ? summary + "\n" + nextStep : summary,
            warning ? OperatorUiMessageKind.Warning : OperatorUiMessageKind.Success, fullDetails);
    }

    private static string DescribeInstance(AzurPilotOperatorMuMuInstance instance)
        => $"{instance.Instance.Id} · {instance.Instance.DisplayName}: "
            + (instance.Observation is null ? "состояние не доказано" : DescribeMuMuState(instance.Observation.State))
            + (instance.Failure is null ? string.Empty : $" · причина: {instance.Failure.Message}");

    private static string DescribeRegion(ScreenshotRegionScanResult region)
    {
        PixelRegion bounds = region.PhysicalPixelBounds;
        return string.Create(CultureInfo.InvariantCulture,
            $"{region.RegionName}: кадр {region.FrameWidth}×{region.FrameHeight} {region.PixelFormat}; "
            + $"bounds=[{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}); "
            + $"pixels={region.PixelCount}; mean RGB=({region.MeanRed:F2}, {region.MeanGreen:F2}, {region.MeanBlue:F2}); "
            + $"std RGB=({region.StandardDeviationRed:F2}, {region.StandardDeviationGreen:F2}, {region.StandardDeviationBlue:F2})");
    }

    private static string FormatFailureDetails(ApplicationFailure failure)
        => $"Код: {failure.Code}; причина: {failure.Message}; повтор имеет смысл: {Fact(failure.IsRetryable)}"
            + (failure.Details is null ? string.Empty : "\n" + string.Join('\n', failure.Details.Take(16)
                .Select(pair => $"{pair.Key}: {OperatorUiSession.CleanText(pair.Value, 256)}")));

    private static string DescribeAction(OperatorUiAction action) => action switch
    {
        OperatorUiAction.MuMuStart or OperatorUiAction.GameStart => "запуск",
        OperatorUiAction.MuMuStop or OperatorUiAction.GameStop => "остановка",
        OperatorUiAction.MuMuRestart or OperatorUiAction.GameRestart => "перезапуск",
        OperatorUiAction.Diagnostics => "диагностика приложения",
        OperatorUiAction.Instances => "поиск экземпляров",
        OperatorUiAction.SelectInstance => "выбор экземпляра",
        OperatorUiAction.AndroidObserve => "наблюдение Android",
        OperatorUiAction.AndroidPrepare => "подготовка Android",
        OperatorUiAction.EnvironmentPrepare => "подготовка MuMu и Android",
        OperatorUiAction.GameObserve => "наблюдение Azur Lane",
        OperatorUiAction.Screenshot => "снимок экрана",
        OperatorUiAction.Cancel => "отмена",
        OperatorUiAction.Logs => "показ логов",
        OperatorUiAction.Details => "просмотр деталей",
        OperatorUiAction.Help => "помощь",
        OperatorUiAction.Exit => "завершение работы",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестное действие."),
    };

    private static string DescribeMuMuState(MuMuLifecycleState state) => state switch
    {
        MuMuLifecycleState.Running => "работает",
        MuMuLifecycleState.Stopped => "остановлен",
        MuMuLifecycleState.Unknown => "состояние неизвестно",
        _ => "состояние неизвестно",
    };

    private static string DescribeMuMuDiagnostics(AzurPilotMuMuDiagnostics diagnostics)
        => !diagnostics.IsInstallationDiscovered ? "установка не найдена"
            : diagnostics.LifecycleState is MuMuLifecycleState state ? DescribeMuMuState(state)
            : diagnostics.Failure is not null ? "состояние не удалось наблюдать"
            : "экземпляр не выбран";

    private static string DescribeAndroidDiagnostics(AzurPilotAndroidDiagnostics diagnostics)
    {
        if (diagnostics.TransportState is not AndroidTransportState state)
        {
            return diagnostics.Failure is not null ? "готовность не удалось наблюдать" : "транспорт не наблюдался";
        }

        if (state != AndroidTransportState.Device)
        {
            return "транспорт " + DescribeTransportState(state);
        }

        if (IsAndroidReady(diagnostics))
        {
            return "готов";
        }

        if (diagnostics.IsShellAvailable == false && diagnostics.BootCompleted == 0)
        {
            return "транспорт доступен; shell недоступен, загрузка выполняется";
        }

        if (diagnostics.IsShellAvailable == false)
        {
            return "транспорт доступен; shell недоступен";
        }

        if (diagnostics.BootCompleted == 0)
        {
            return "транспорт доступен; загрузка выполняется";
        }

        return "транспорт доступен; готовность не доказана";
    }

    private static bool IsAndroidReady(AzurPilotAndroidDiagnostics diagnostics)
        => diagnostics.TransportState == AndroidTransportState.Device
            && diagnostics.IsShellAvailable == true
            && diagnostics.BootCompleted == 1;

    private static string DescribeGameDiagnostics(AzurPilotAzurLaneDiagnostics diagnostics)
        => diagnostics.State is AzurLaneGameState state ? DescribeGameState(state) : "состояние неизвестно";

    private static string DescribeTransportState(AndroidTransportState state) => state switch
    {
        AndroidTransportState.Device => "доступен",
        AndroidTransportState.Absent => "не найден",
        AndroidTransportState.Offline => "offline",
        AndroidTransportState.Unknown => "не доказан",
        _ => "не доказан",
    };

    private static string DescribeGameState(AzurLaneGameState state) => state switch
    {
        AzurLaneGameState.NotInstalled => "не установлена",
        AzurLaneGameState.Stopped => "остановлена",
        AzurLaneGameState.Background => "в фоне",
        AzurLaneGameState.Foreground => "на переднем плане",
        AzurLaneGameState.Unknown => "состояние не доказано",
        _ => "состояние не доказано",
    };

    private static string DescribeAndroidObservation(
        MuMuInstanceId target,
        AndroidTransportState transport,
        bool? shellAvailable,
        int? bootCompleted)
    {
        string summary = $"Android · {target}: транспорт {DescribeTransportState(transport)}; "
            + $"shell {Fact(shellAvailable)}; загрузка {DescribeBoot(bootCompleted)}.";
        return transport is AndroidTransportState.Absent or AndroidTransportState.Offline
            ? summary + " Следующий шаг: /android-prepare после проверки состояния MuMu."
            : summary;
    }

    private static string DescribeBoot(int? bootCompleted) => bootCompleted switch
    {
        1 => "завершена",
        0 => "выполняется",
        _ => "не наблюдалась",
    };

    private static string Fact(bool value) => value ? "да" : "нет";

    private sealed record OperationPresentation(
        bool Success,
        string Summary,
        OperatorUiMessageKind Kind,
        string? Details = null,
        AzurPilotOperatorMuMuInventory? Inventory = null,
        bool Cancelled = false);
}
