using System.Globalization;
using AzurPilot.App.OperatorServices;
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
        Session.AddMessage("Диагностика запуска", DescribeDiagnostics(initialDiagnostics));
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
                : "Нужен explicit target и, для изменения состояния, отдельное подтверждение.");
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
            OperationText = result.Success ? "Операция: завершена" : "Операция: отказ";
            Session.AddMessage(result.Success ? "Результат" : "Отказ", result.Text);
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
            Session.AddMessage("Отмена", "Операция завершила обработку cancellation.");
        }
        catch (Exception exception)
        {
            OperationText = "Операция: ошибка";
            Session.AddMessage("Ошибка", $"Неожиданная ошибка операции: {exception.GetType().Name}.");
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
                return new OperationPresentation(true, DescribeDiagnostics(_service.RefreshDiagnostics()));
            case OperatorUiAction.Instances:
                ApplicationResult<AzurPilotOperatorMuMuInventory> inventory = _service.DiscoverMuMuInstances();
                return inventory.IsFailure ? Failure(inventory.FailureInfo!) : new OperationPresentation(true,
                    $"MuMu {inventory.Value!.InstallationVersion}; экземпляров: {inventory.Value.Instances.Count}.\n"
                    + string.Join('\n', inventory.Value.Instances.Select(instance =>
                        $"{instance.Instance.Id} · {instance.Instance.DisplayName} · "
                        + (instance.Observation?.State.ToString() ?? "не наблюдалось")
                        + (instance.Failure is null ? string.Empty : " · " + instance.Failure.Code))), inventory.Value);
            case OperatorUiAction.MuMuStart:
            case OperatorUiAction.MuMuStop:
            case OperatorUiAction.MuMuRestart:
                MuMuLifecycleOperation operation = action == OperatorUiAction.MuMuStart
                    ? MuMuLifecycleOperation.Start
                    : action == OperatorUiAction.MuMuStop
                        ? MuMuLifecycleOperation.Stop
                        : MuMuLifecycleOperation.Restart;
                return Present(await _service.RunMuMuLifecycleAsync(target!.Value, operation, cancellationToken)
                    .ConfigureAwait(false), outcome => $"{target}: {outcome.InitialState} → {outcome.FinalState}\n{outcome.Evidence}");
            case OperatorUiAction.AndroidObserve:
                return Present(_service.ObserveAndroid(target!.Value), facts =>
                    $"{target} · {facts.Endpoint}\nTransport: {facts.Transport.State}; "
                    + $"boot_completed: {facts.Boot?.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n{facts.Evidence}");
            case OperatorUiAction.AndroidPrepare:
                return Present(await _service.PrepareAndroidAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    outcome => $"{target} · {outcome.Endpoint}\n{outcome.InitialTransportName} → {outcome.FinalTransportName}"
                    + $"; connect: {Fact(outcome.ConnectAttempted)}\n{outcome.Evidence}");
            case OperatorUiAction.GameObserve:
                return Present(_service.ObserveAzurLane(target!.Value), observation =>
                    $"{target} · Azur Lane Global/EN: {observation.State}\n"
                    + $"Installed: {Fact(observation.Facts.Installed)}; process: {Fact(observation.Facts.ProcessRunning)}; "
                    + $"foreground: {Fact(observation.Facts.Foreground)}\n{observation.Evidence}");
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

                return Present(game, outcome => $"{target} · {outcome.Package}\n{outcome.InitialState} → {outcome.FinalState}\n{outcome.Evidence}");
            case OperatorUiAction.Screenshot:
                return Present(await _service.CaptureAndScanAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    analysis => $"{analysis.Capture.InstanceId} · {analysis.Capture.Endpoint}\n"
                    + $"PNG: {analysis.Capture.EncodedPngByteCount} bytes; capture: {analysis.Capture.CapturedAtUtc:O}\n"
                    + string.Join('\n', analysis.Regions.Select(region =>
                    {
                        PixelRegion bounds = region.PhysicalPixelBounds;
                        return string.Create(CultureInfo.InvariantCulture,
                            $"{region.RegionName}: кадр {region.FrameWidth}×{region.FrameHeight} {region.PixelFormat}; "
                            + $"bounds=[{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}); "
                            + $"pixels={region.PixelCount}; mean RGB=({region.MeanRed:F2}, {region.MeanGreen:F2}, {region.MeanBlue:F2}); "
                            + $"std RGB=({region.StandardDeviationRed:F2}, {region.StandardDeviationGreen:F2}, {region.StandardDeviationBlue:F2})");
                    })));
            case OperatorUiAction.SelectInstance:
            case OperatorUiAction.Cancel:
            case OperatorUiAction.Logs:
            case OperatorUiAction.Help:
            case OperatorUiAction.Exit:
                throw new InvalidOperationException("Действие не является service operation.");
            default:
                throw new InvalidOperationException("Неизвестное действие операторской поверхности.");
        }
    }

    private static OperationPresentation Present<T>(ApplicationResult<T> result, Func<T, string> describe)
        where T : notnull
        => result.IsFailure ? Failure(result.FailureInfo!) : new OperationPresentation(true, describe(result.Value!));

    private static OperationPresentation Failure(ApplicationFailure failure)
        => new(false, $"{failure.Code}: {failure.Message}\nПовтор имеет смысл: {Fact(failure.IsRetryable)}"
            + (failure.Details is null ? string.Empty : "\n" + string.Join('\n', failure.Details.Take(16)
                .Select(pair => $"{pair.Key}: {OperatorUiSession.CleanText(pair.Value, 256)}"))));

    private static string Fact(bool? value) => value.HasValue ? (value.Value ? "да" : "нет") : "не наблюдалось";

    internal static string DescribeDiagnostics(AzurPilotDiagnosticReport report)
        => $"{report.Application.AssemblyName} {report.Application.AssemblyVersion} · {report.Application.RuntimeIdentifier}\n"
            + $"Configuration: {report.Configuration.ValidationStatus}; schema {report.Configuration.SchemaVersion}\n"
            + $"Native: available {Fact(report.Native.IsAvailable)}; ABI compatible {Fact(report.Native.IsAbiCompatible)}"
            + $"; OpenCV {report.Native.OpencvVersion}\n"
            + $"MuMu: discovered {Fact(report.MuMu.IsInstallationDiscovered)}; version {report.MuMu.Version ?? "не наблюдалась"}"
            + $"; state {report.MuMu.LifecycleState?.ToString() ?? "не наблюдалось"}\n"
            + $"Android: {report.Android.Stage}; transport {report.Android.TransportState?.ToString() ?? "не наблюдалось"}\n"
            + $"Azur Lane Global/EN: {report.AzurLane.State?.ToString() ?? "не наблюдалось"}"
            + $"; installed {Fact(report.AzurLane.IsInstalled)}; foreground {Fact(report.AzurLane.IsForeground)}\n"
            + string.Join('\n', new[] { report.Native.Failure, report.MuMu.Failure, report.Android.Failure, report.AzurLane.Failure }
                .OfType<ApplicationFailure>().Select(failure => failure.Code + ": " + failure.Message));

    private sealed record OperationPresentation(bool Success, string Text, AzurPilotOperatorMuMuInventory? Inventory = null);
}
