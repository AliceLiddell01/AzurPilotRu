using System.Globalization;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.Diagnostics;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App.OperatorUi;

/// <summary>Координатор действий оператора и результатов сервисов, проверяемый без отрисовки.</summary>
/// <remarks>Session обновляется только вызывающей стороной; завершённый task применяется через CompleteOperation.</remarks>
public sealed class OperatorUiController : IDisposable
{
    private readonly AzurPilotOperatorService _service;
    private CancellationTokenSource? _cancellation;
    private Task<OperationPresentation>? _activeTask;
    private AzurPilotDiagnosticReport _lastDiagnostics;
    private string _operationText = "Операция: нет";
    private string? _progressText;
    private AzurPilotConfigurationSnapshot _savedConfiguration;
    private bool _disposed;

    /// <summary>Создаёт координатор с уже собранным снимком диагностики запуска.</summary>
    /// <param name="service">Сценарии приложения.</param>
    /// <param name="initialDiagnostics">Уже собранная диагностика.</param>
    /// <param name="mode">Режим интерфейса: объём доступных действий и подробность состояния.</param>
    /// <param name="logViewerFailure">Отказ открытия отдельной лог-консоли либо <see langword="null"/>.</param>
    public OperatorUiController(
        AzurPilotOperatorService service,
        AzurPilotDiagnosticReport initialDiagnostics,
        OperatorUiMode mode = OperatorUiMode.Normal,
        ApplicationFailure? logViewerFailure = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(initialDiagnostics);
        _service = service;
        _lastDiagnostics = initialDiagnostics;
        _savedConfiguration = service.Configuration;
        if (initialDiagnostics.MuMu.SelectedInstanceId is string selected && MuMuInstanceValue.IsValid(selected))
        {
            _ = Session.SelectInstance(MuMuInstanceId.FromIndex(selected[MuMuInstanceValue.ProviderPrefix.Length..]));
        }

        Session.AddMessage("AzurPilot", WelcomeText(mode));
        // В обычном режиме состояние среды — это не предупреждение об отказе: остановленный MuMu
        // описывается как рабочее состояние с доступным действием, а не как ряд failure-кодов.
        OperationPresentation diagnostic = mode == OperatorUiMode.Normal
            ? DescribeEnvironmentState(initialDiagnostics)
            : DescribeDiagnostics(initialDiagnostics);
        Session.AddMessage(
            mode == OperatorUiMode.Normal ? "Состояние среды" : "Диагностика запуска",
            diagnostic.Summary,
            diagnostic.Kind,
            diagnostic.Details);
        if (logViewerFailure is ApplicationFailure viewerFailure)
        {
            // Короткое сообщение вместо молчания: журнал не перенаправляется на экран TUI, но причина
            // отказа просмотра остаётся видимой и записанной в runtime log.
            Session.AddMessage(
                "Отдельная лог-консоль",
                viewerFailure.Message,
                OperatorUiMessageKind.Warning,
                $"Код: {viewerFailure.Code}\nПовтор имеет смысл: {Fact(viewerFailure.IsRetryable)}");
        }
    }

    private static string WelcomeText(OperatorUiMode mode) => mode == OperatorUiMode.Normal
        ? "Введите / для меню действий. Основное действие — «Запустить окружение»."
        : "Введите / для меню действий текущего режима. Технические команды доступны по категориям.";

    /// <summary>Чистое presentation-состояние с operation и confirmation gates.</summary>
    public OperatorUiSession Session { get; } = new();

    /// <summary>Последний фактически полученный inventory либо отсутствие observation.</summary>
    public AzurPilotOperatorMuMuInventory? Inventory { get; private set; }

    /// <summary>Краткий статус текущей либо последней операции.</summary>
    public string OperationText
    {
        get => Volatile.Read(ref _progressText) ?? _operationText;
        private set => _operationText = value;
    }

    /// <summary>Task завершён и результат готов к применению к Session.</summary>
    public bool IsOperationCompleted => _activeTask?.IsCompleted == true;

    /// <summary>Отказ последней завершённой операции; <see langword="null"/> при успехе или отмене.</summary>
    public ApplicationFailure? LastOperationFailure { get; private set; }

    /// <summary>Запускает сценарий сервиса только после проверки допуска.</summary>
    /// <param name="action">Service action.</param>
    /// <returns>Операция получила единственный execution slot.</returns>
    public bool TryStartOperation(OperatorUiAction action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Session.TryBeginOperation(action, out MuMuInstanceId? target))
        {
            Session.AddMessage("Действие недоступно", Session.IsOperationActive
                ? "Уже выполняется операция. /cancel — отмена; повтор доступен после завершения."
                : "Нужен явно выбранный экземпляр и, для изменения состояния, отдельное подтверждение.",
                OperatorUiMessageKind.Warning);
            return false;
        }

        _cancellation = new CancellationTokenSource();
        Volatile.Write(ref _progressText, null);
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
            // CompleteOperation сохраняет непредвиденный отказ, не оставляя task ненаблюдённым.
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
        MuMuInstanceId? resolvedTarget = null;
        try
        {
            OperationPresentation result = _activeTask.GetAwaiter().GetResult();
            Volatile.Write(ref _progressText, null);
            OperationText = result.Cancelled ? "Операция: отменена"
                : result.Success ? "Операция: завершена" : "Операция: отказ";
            LastOperationFailure = result.Failure;
            resolvedTarget = result.ResolvedTarget;
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
            LastOperationFailure = null;
            Session.AddMessage("Отмена", "Операция завершила обработку отмены.", OperatorUiMessageKind.Warning);
        }
        catch (Exception exception)
        {
            OperationText = "Операция: ошибка";
            LastOperationFailure = null;
            Session.AddMessage("Ошибка", "Неожиданная ошибка операции. Проверьте технические детали.",
                OperatorUiMessageKind.Error,
                $"Тип: {exception.GetType().FullName}\nСообщение: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _progressText, null);
            Session.CompleteOperation();
            // Доказанная identity применяется к сессии только после освобождения operation slot:
            // до этого момента смена target запрещена контрактом сессии.
            if (resolvedTarget is MuMuInstanceId target)
            {
                _ = Session.SelectInstance(target);
            }

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
                return DescribeDiagnostics(Remember(_service.RefreshDiagnostics()));
            case OperatorUiAction.Status:
                return DescribeEnvironmentState(Remember(_service.RefreshDiagnostics()));
            case OperatorUiAction.Instances:
                ApplicationResult<AzurPilotOperatorMuMuInventory> inventory = _service.DiscoverMuMuInstances(cancellationToken: cancellationToken);
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
                        $"Действие: {action}\nИсходное состояние: {DescribeMuMuState(outcome.InitialState)}\n"
                        + $"Итоговое состояние: {DescribeMuMuState(outcome.FinalState)}\nНаблюдения: {outcome.Evidence}"
                    ));
            case OperatorUiAction.AndroidObserve:
                return Present(_service.ObserveAndroid(target!.Value), facts =>
                    (
                        DescribeAndroidObservation(target.Value, facts.Transport.State, facts.Boot?.ShellAvailable,
                            facts.Boot?.BootCompleted),
                        $"Адрес подключения: {facts.Endpoint}\nТранспорт: {facts.Transport.State}\n"
                        + $"Shell: {Fact(facts.Boot?.ShellAvailable)}\n"
                        + $"boot_completed: {facts.Boot?.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                        + $"Версия Android: {facts.Boot?.AndroidRelease ?? "не наблюдалась"}\nНаблюдения: {facts.Evidence}"
                    ));
            case OperatorUiAction.AndroidPrepare:
                return Present(await _service.PrepareAndroidAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    outcome =>
                    (
                        $"Android готов для {target}: transport, shell и загрузка Android проверены.",
                        $"Адрес подключения: {outcome.Endpoint}\nТранспорт: {outcome.InitialTransportName} → {outcome.FinalTransportName}\n"
                        + $"Попытка connect: {Fact(outcome.ConnectAttempted)}\n"
                        + $"Shell: {Fact(outcome.Boot.ShellAvailable)}\n"
                        + $"boot_completed: {outcome.Boot.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                        + $"Наблюдения: {outcome.Evidence}"
                    ));
            case OperatorUiAction.EnvironmentPrepare:
                return DescribeEnvironmentPreparation(await _service.PrepareSelectedAndroidAsync(
                    target!.Value, cancellationToken, PreparationProgress()).ConfigureAwait(false));
            case OperatorUiAction.ResolveTarget:
                return ResolveConfiguredTarget();
            case OperatorUiAction.ReopenLogViewer:
                return Present(await _service.ReopenLogViewerAsync(cancellationToken).ConfigureAwait(false),
                    _ => ("Отдельная лог-консоль подключена.", "Потребитель журнала хоста приложения без управления; MuMu не изменялся."));
            case OperatorUiAction.GameObserve:
                return Present(_service.ObserveAzurLane(target!.Value), observation =>
                    (
                        $"Azur Lane Global/EN · {target}: {DescribeGameState(observation.State)}. "
                        + $"Установлена: {Fact(observation.Facts.Installed)}; процесс: {Fact(observation.Facts.ProcessRunning)}; "
                        + $"на переднем плане: {Fact(observation.Facts.Foreground)}.",
                        $"Состояние: {observation.State}\n"
                        + $"Установлена: {Fact(observation.Facts.Installed)}\nПроцесс: {Fact(observation.Facts.ProcessRunning)}\n"
                        + $"На переднем плане: {Fact(observation.Facts.Foreground)}\n"
                        + $"Наблюдения: {observation.Evidence}"
                    ));
            case OperatorUiAction.GameStart:
            case OperatorUiAction.GameStop:
            case OperatorUiAction.GameRestart:
                ApplicationResult<AzurLaneGameLifecycleOutcome> game;
                if (action == OperatorUiAction.GameStart)
                {
                    game = await _service.StartAzurLaneAsync(target!.Value, cancellationToken, PreparationProgress(),
                        () => Volatile.Write(ref _progressText, "Запуск игры · F4 отмена"))
                        .ConfigureAwait(false);
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
                        $"Пакет: {outcome.Package}\nДействие: {action}\nИсходное состояние: {outcome.InitialState}\n"
                        + $"Итоговое состояние: {outcome.FinalState}\nНаблюдения: {outcome.Evidence}"
                    ));
            case OperatorUiAction.Screenshot:
                return Present(await _service.CaptureAndScanAsync(target!.Value, cancellationToken).ConfigureAwait(false),
                    analysis =>
                    (
                        $"Снимок MuMu {analysis.Capture.InstanceId} получен; FullFrame и Center проверены.",
                        $"Адрес подключения: {analysis.Capture.Endpoint}\nPNG: {analysis.Capture.EncodedPngByteCount} байт\n"
                        + $"Время захвата UTC: {analysis.Capture.CapturedAtUtc:O}\n"
                        + string.Join('\n', analysis.Regions.Select(DescribeRegion))
                    ));
            case OperatorUiAction.SelectInstance:
            case OperatorUiAction.Cancel:
            case OperatorUiAction.Settings:
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
            string summary = $"Окружение готово. Android для {outcome.InstanceId} готов: transport, shell и загрузка проверены. "
                + $"{muMuSummary} Azur Lane не запускалась.";
            string details = $"Экземпляр: {outcome.InstanceId}\nЭтап: {outcome.Stage}\n"
                + $"Исходное состояние MuMu: {outcome.InitialMuMuState}\n{muMuSummary}\n"
                + $"Адрес подключения: {readiness.Endpoint}\nТранспорт: {readiness.InitialTransportName} → {readiness.FinalTransportName}\n"
                + $"Попытка connect: {Fact(readiness.ConnectAttempted)}\n"
                + $"Shell: {Fact(readiness.Boot.ShellAvailable)}\n"
                + $"boot_completed: {readiness.Boot.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}\n"
                + $"Наблюдения: {readiness.Evidence}\nAzur Lane: запуск не выполнялся.";
            return new OperationPresentation(true, summary, OperatorUiMessageKind.Success, details);
        }

        ApplicationFailure? failure = outcome.Failure;
        string reason = failure?.Code == ApplicationFailure.OperationCancelled
            ? "Подготовка отменена пользователем."
            : failure?.Message ?? "Подготовка не подтвердила готовность Android.";
        string partialState = outcome.PostflightMuMuState switch
        {
            MuMuLifecycleState.Running => "Новое наблюдение: MuMu продолжает работать. Для остановки выберите «Остановить окружение».",
            MuMuLifecycleState.Stopped => "Новое наблюдение: MuMu остановлен.",
            MuMuLifecycleState.Unknown => "Новое наблюдение: состояние MuMu не доказано, он может остаться запущенным. Автоматической остановки нет.",
            _ => outcome.MuMuMayRemainRunning
                ? "MuMu может остаться запущенным; новое наблюдение не доказало его состояние. Автоматической остановки нет."
                : "Запуск MuMu не подтверждён.",
        };
        string failedSummary = $"Подготовка {outcome.InstanceId} остановлена на этапе "
            + $"{DescribePreparationStage(outcome.Stage)}: {reason} {partialState} Azur Lane не запускалась.";
        string failedDetails = $"Экземпляр: {outcome.InstanceId}\nЭтап: {outcome.Stage}\n"
            + $"Исходное состояние MuMu: {outcome.InitialMuMuState?.ToString() ?? "не наблюдалось"}\n"
            + $"Запрос запуска MuMu: {Fact(outcome.MuMuStartRequested)}\n"
            + $"Итог MuMu: {outcome.MuMuStartOutcome?.FinalState.ToString() ?? "не доказан"}\n"
            + $"MuMu может остаться запущенным: {Fact(outcome.MuMuMayRemainRunning)}\n"
            + (failure is null ? "Код отказа: не предоставлен." : FormatFailureDetails(failure))
            + (outcome.AndroidPreparation is null ? string.Empty
                : $"\nАдрес подключения: {outcome.AndroidPreparation.Endpoint}\n"
                    + $"Транспорт: {outcome.AndroidPreparation.InitialTransportName} → "
                    + $"{outcome.AndroidPreparation.FinalTransportName}\n"
                    + $"Наблюдения: {outcome.AndroidPreparation.Evidence}")
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
            Cancelled: failure?.Code == ApplicationFailure.OperationCancelled,
            Failure: failure);
    }

    private static string DescribePreparationStage(AzurPilotOperatorAndroidPreparationStage stage) => stage switch
    {
        AzurPilotOperatorAndroidPreparationStage.ResolveMuMu => "разрешения выбранного MuMu",
        AzurPilotOperatorAndroidPreparationStage.ObserveMuMu => "наблюдения MuMu",
        AzurPilotOperatorAndroidPreparationStage.StartMuMu => "запуска MuMu",
        AzurPilotOperatorAndroidPreparationStage.PrepareAndroid => "подготовки Android",
        AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot => "проверки загрузки Android",
        AzurPilotOperatorAndroidPreparationStage.Completed => "проверки готовности",
        _ => "неизвестном этапе",
    };

    private SynchronousProgress<AzurPilotOperatorAndroidPreparationStage> PreparationProgress() =>
        new SynchronousProgress<AzurPilotOperatorAndroidPreparationStage>(stage =>
        {
            string text = stage switch
            {
                AzurPilotOperatorAndroidPreparationStage.ResolveMuMu => "Проверка целевого экземпляра",
                AzurPilotOperatorAndroidPreparationStage.ObserveMuMu => "Проверка состояния MuMu",
                AzurPilotOperatorAndroidPreparationStage.StartMuMu => "Запуск MuMu",
                AzurPilotOperatorAndroidPreparationStage.PrepareAndroid => "Подключение Android",
                AzurPilotOperatorAndroidPreparationStage.CheckAndroidBoot => "Проверка загрузки Android",
                AzurPilotOperatorAndroidPreparationStage.Completed => "Окружение готово",
                _ => "Подготовка окружения",
            };
            Volatile.Write(ref _progressText, text + " · F4 отмена");
        });

    /// <summary>Настройки, записанные для следующего запуска; текущий DI snapshot остаётся прежним.</summary>
    internal AzurPilotConfiguration SavedConfiguration => _savedConfiguration.Configuration;

    /// <summary>Явно сохраняет настройку и сообщает фактический результат записи.</summary>
    internal bool SaveSettings(AzurPilotConfiguration configuration)
    {
        if (Session.IsOperationActive)
        {
            Session.AddMessage("Настройки", "Дождитесь завершения операции.", OperatorUiMessageKind.Warning);
            return false;
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> saved = _service.SaveConfiguration(_savedConfiguration, configuration,
            out string? backupPath);
        if (saved.IsFailure)
        {
            Session.AddMessage("Настройки не сохранены", saved.FailureInfo!.Message, OperatorUiMessageKind.Error);
            return false;
        }

        _savedConfiguration = saved.Value!;
        Session.AddMessage("Настройки сохранены", "Выбор будет восстановлен при следующем запуске. "
            + "Режим и лог-консоль применяются после перезапуска."
            + (backupPath is null ? string.Empty : $"\nРезервная копия: {backupPath}"), OperatorUiMessageKind.Success);
        return true;
    }

    private static OperationPresentation Failure(ApplicationFailure failure)
    {
        bool cancelled = failure.Code == ApplicationFailure.OperationCancelled;
        string summary = cancelled
            ? $"Операция отменена: {failure.Message}\n"
                + "Следующий шаг: проверьте завершённые этапы через /diagnostics и откройте /details."
            : $"Причина отказа: {failure.Message}\n"
                + (failure.IsRetryable
                    ? "Следующий шаг: устраните указанную причину и повторите действие."
                    : "Подробности доступны при запуске с --mode diagnostic: /diagnostics и /details.");
        return new OperationPresentation(
            false,
            summary,
            cancelled ? OperatorUiMessageKind.Warning : OperatorUiMessageKind.Error,
            $"Код: {failure.Code}\nПовтор имеет смысл: {Fact(failure.IsRetryable)}"
            + (failure.Details is null ? string.Empty : "\n" + string.Join('\n', failure.Details.Take(16)
                .Select(pair => $"{pair.Key}: {OperatorUiSession.CleanText(pair.Value, 256)}"))),
            Cancelled: cancelled,
            Failure: failure);
    }

    /// <summary>Строит семантику выбора целевого экземпляра из действующей конфигурации.</summary>
    /// <returns>Семантика выбора либо отказ недопустимого значения конфигурации.</returns>
    private ApplicationResult<MuMuInstanceSelection> ConfiguredSelection()
    {
        string configured = _service.Configuration.Configuration.MuMu.Instance;
        if (MuMuInstanceValue.IsAuto(configured))
        {
            return ApplicationResult<MuMuInstanceSelection>.Success(MuMuInstanceSelection.Auto());
        }

        if (MuMuInstanceValue.IsValid(configured))
        {
            return ApplicationResult<MuMuInstanceSelection>.Success(MuMuInstanceSelection.Explicit(
                MuMuInstanceId.FromIndex(configured[MuMuInstanceValue.ProviderPrefix.Length..])));
        }

        return ApplicationResult<MuMuInstanceSelection>.Failure(new ApplicationFailure
        {
            Code = ApplicationFailure.ConfigurationInvalid,
            Message = $"Значение mumu.instance «{configured}» не поддерживается схемой конфигурации.",
            IsRetryable = false,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mumu_instance"] = configured,
            },
        });
    }

    /// <summary>
    /// Разрешает точную identity целевого экземпляра без mutation: шаг перед единым подтверждением.
    /// </summary>
    /// <returns>Представление доказанной identity либо объяснение, почему старт не разрешён.</returns>
    private OperationPresentation ResolveConfiguredTarget()
    {
        ApplicationResult<MuMuInstanceSelection> selection = ConfiguredSelection();
        if (selection.IsFailure)
        {
            return Failure(selection.FailureInfo!);
        }

        MuMuInstanceSelection value = selection.Value!;
        ApplicationResult<MuMuInstanceId> resolved = _service.ResolveTarget(value);
        if (resolved.IsFailure)
        {
            return DescribeTargetResolutionFailure(resolved.FailureInfo!, value);
        }

        MuMuInstanceId target = resolved.Value!;
        string how = value.IsAutomatic
            ? "автоматически: обнаружен ровно один экземпляр"
            : "по сохранённому mumu.instance";
        return new OperationPresentation(
            true,
            $"Целевой экземпляр {target} определён {how}.",
            OperatorUiMessageKind.Information,
            $"Экземпляр: {target}\nСпособ выбора: {how}\n"
            + $"Значение mumu.instance: {_service.Configuration.Configuration.MuMu.Instance}",
            ResolvedTarget: target,
            Failure: null);
    }

    /// <summary>Объясняет оператору, почему старт не разрешён, и что делать дальше.</summary>
    /// <param name="failure">Отказ разрешения target.</param>
    /// <param name="selection">Использованная семантика выбора.</param>
    /// <returns>Представление отказа с пользовательским следующим шагом.</returns>
    private static OperationPresentation DescribeTargetResolutionFailure(
        ApplicationFailure failure,
        MuMuInstanceSelection selection)
    {
        string nextStep = failure.Code switch
        {
            ApplicationFailure.MuMuInstanceAmbiguous =>
                "Обнаружено несколько экземпляров MuMu, поэтому автоматический выбор не выполняется. "
                + "Выберите точный ID в списке и сохраните его в mumu.instance, чтобы следующий запуск "
                + "обошёлся без выбора.",
            ApplicationFailure.MuMuInstanceNotFound =>
                "Ни один экземпляр MuMu не обнаружен. Проверьте, что нужный экземпляр создан в MuMuPlayer.",
            ApplicationFailure.MuMuInstallationNotFound or ApplicationFailure.MuMuInstallationAmbiguous =>
                "Установка MuMuPlayer не разрешена однозначно. Проверьте установку и повторите запуск.",
            _ => "Проверьте состояние MuMu и повторите запуск окружения.",
        };
        string how = selection.IsAutomatic
            ? "автоматический (mumu.instance = auto)"
            : "явный (mumu.instance)";
        return new OperationPresentation(
            false,
            $"Запуск окружения не выполнен: {failure.Message}\n{nextStep}",
            OperatorUiMessageKind.Warning,
            $"Код: {failure.Code}\nСпособ выбора: {how}\n"
            + (failure.Details is null ? string.Empty : string.Join('\n', failure.Details.Take(16)
                .Select(pair => $"{pair.Key}: {OperatorUiSession.CleanText(pair.Value, 256)}"))),
            Failure: failure);
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
            ? "Следующий шаг: запустите окружение — точный экземпляр разрешается из mumu.instance."
            : !androidReady
                ? "Следующий шаг: проверьте /android; подготовка доступна действием «Запустить окружение»."
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
            + $"интерфейс управления {report.MuMu.ControlSurfaceStatus}; настроенный экземпляр {report.MuMu.ConfiguredInstance}; "
            + $"выбранный экземпляр {report.MuMu.SelectedInstanceId ?? "не выбран"}; "
            + $"отображаемое имя {report.MuMu.SelectedInstanceDisplayName ?? "не наблюдалось"}; "
            + $"состояние {(report.MuMu.LifecycleState is MuMuLifecycleState muMuState ? DescribeMuMuState(muMuState) : "не наблюдалось")}; наблюдения {report.MuMu.Evidence ?? "не наблюдались"}\n"
            + $"Android: ADB {Fact(report.Android.IsAdbAvailable)}; endpoint {report.Android.Endpoint ?? "не разрешён"}; "
            + $"этап {report.Android.Stage}; transport {(report.Android.TransportState is AndroidTransportState androidTransport ? DescribeTransportState(androidTransport) : "не наблюдалось")}; "
            + $"shell {Fact(report.Android.IsShellAvailable)}; boot_completed "
            + $"{report.Android.BootCompleted?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдалось"}; "
            + $"версия Android {report.Android.AndroidRelease ?? "не наблюдалась"}; SDK {report.Android.SdkLevel?.ToString(CultureInfo.InvariantCulture) ?? "не наблюдался"}; "
            + $"наблюдения {report.Android.Evidence ?? "не наблюдались"}\n"
            + $"Azur Lane Global/EN: пакет {report.AzurLane.Package}; состояние {(report.AzurLane.State is AzurLaneGameState gameState ? DescribeGameState(gameState) : "не наблюдалось")}; "
            + $"установлена {Fact(report.AzurLane.IsInstalled)}; процесс {Fact(report.AzurLane.IsProcessRunning)}; "
            + $"передний план {Fact(report.AzurLane.IsForeground)}; наблюдения {report.AzurLane.Evidence ?? "не наблюдались"}\n"
            + string.Join('\n', new[] { report.Native.Failure, report.MuMu.Failure, report.Android.Failure, report.AzurLane.Failure }
                .OfType<ApplicationFailure>().Select(FormatFailureDetails));
        return new OperationPresentation(true, warning ? summary + "\n" + nextStep : summary,
            warning ? OperatorUiMessageKind.Warning : OperatorUiMessageKind.Success, fullDetails);
    }

    /// <summary>Запоминает последний наблюдённый отчёт для экрана настроек.</summary>
    /// <param name="report">Отчёт текущего наблюдения.</param>
    /// <returns>Тот же отчёт.</returns>
    private AzurPilotDiagnosticReport Remember(AzurPilotDiagnosticReport report)
    {
        _lastDiagnostics = report;
        return report;
    }

    /// <summary>
    /// Краткое состояние среды для обычного режима: готовность и возможное действие пользователя.
    /// </summary>
    /// <remarks>
    /// Нормальное состояние <c>MuMu = Stopped</c> — это не отказ: недоступный до запуска ADB endpoint
    /// относится к диагностическим подробностям, а не к пользовательскому экрану. Технические
    /// failure-коды и correlation identifier здесь не показываются.
    /// </remarks>
    /// <param name="report">Отчёт текущего наблюдения.</param>
    /// <returns>Краткое представление состояния.</returns>
    private static OperationPresentation DescribeEnvironmentState(AzurPilotDiagnosticReport report)
    {
        string summary = $"MuMu — {DescribeMuMuDiagnostics(report.MuMu)}; "
            + $"Android — {DescribeAndroidDiagnostics(report.Android)}; "
            + $"Azur Lane Global/EN — {DescribeGameDiagnostics(report.AzurLane)}.";
        if (!report.MuMu.IsInstallationDiscovered)
        {
            return new OperationPresentation(true,
                summary + "\nУстановка MuMuPlayer не обнаружена: запуск окружения недоступен.",
                OperatorUiMessageKind.Warning);
        }

        if (report.MuMu.LifecycleState == MuMuLifecycleState.Stopped)
        {
            return new OperationPresentation(true,
                "MuMu остановлен. Можно запустить окружение.",
                OperatorUiMessageKind.Information);
        }

        if (report.MuMu.LifecycleState == MuMuLifecycleState.Running)
        {
            return IsAndroidReady(report.Android)
                ? new OperationPresentation(true,
                    summary + "\nОкружение готово: можно запустить игру.",
                    OperatorUiMessageKind.Success)
                : new OperationPresentation(true,
                    summary + "\nMuMu работает; Android ещё не готов. Запустите окружение для подготовки Android.",
                    OperatorUiMessageKind.Information);
        }

        return new OperationPresentation(true,
            summary + "\nТочное состояние MuMu не доказано. Запустите окружение, чтобы получить его.",
            OperatorUiMessageKind.Information);
    }

    /// <summary>Описывает действующую эффективную конфигурацию и реально доступную настройку.</summary>
    /// <param name="mode">Режим интерфейса, в котором открыт экран настроек.</param>
    /// <returns>Сообщение с реальными значениями конфигурации.</returns>
    internal OperatorUiMessage DescribeSettings(OperatorUiMode mode)
    {
        AzurPilotConfigurationDiagnostics configuration = _lastDiagnostics.Configuration;
        string source = configuration.Source == AzurPilotConfigurationSource.BuiltInDefaults
            ? "встроенные значения по умолчанию (файла нет)"
            : configuration.FilePath ?? "файл";
        string instance = string.IsNullOrWhiteSpace(_lastDiagnostics.MuMu.ConfiguredInstance)
            ? "не задан"
            : _lastDiagnostics.MuMu.ConfiguredInstance;
        string text = "Действующая конфигурация применяется один раз при запуске; hot reload нет.\n"
            + $"Источник: {source}\n"
            + $"Схема: эффективная v{configuration.SchemaVersion}, объявленная v{configuration.SourceSchemaVersion}\n"
            + $"Минимальный уровень журнала: {OperatorUiLogPresentation.LevelLabel(configuration.MinimumLevel)}\n"
            + $"Режим интерфейса: {OperatorUiModePresentation.Label(mode)} "
            + $"(запуск: --mode {OperatorUiModePresentation.Argument(mode)})\n"
            + $"Экземпляр MuMu: {instance}\n"
            + $"Расположение конфигурации: {AzurPilotConfigurationPath.GetDefaultRuntimePath()}\n"
            + $"Следующий запуск: {OperatorUiModePresentation.Argument(SavedConfiguration.Ui.Mode)}, "
            + $"экземпляр {SavedConfiguration.MuMu.Instance}, "
            + $"отдельная лог-консоль: {(SavedConfiguration.Logging.Viewer.Enabled ? "включена" : "выключена")}.";
        return new OperatorUiMessage("Настройки", text);
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
        OperatorUiAction.Status => "состояние среды",
        OperatorUiAction.Instances => "поиск экземпляров",
        OperatorUiAction.SelectInstance => "выбор экземпляра",
        OperatorUiAction.AndroidObserve => "наблюдение Android",
        OperatorUiAction.AndroidPrepare => "подготовка Android",
        OperatorUiAction.EnvironmentPrepare => "подготовка MuMu и Android",
        OperatorUiAction.ResolveTarget => "определение целевого экземпляра",
        OperatorUiAction.GameObserve => "наблюдение Azur Lane",
        OperatorUiAction.Screenshot => "снимок экрана",
        OperatorUiAction.Cancel => "отмена",
        OperatorUiAction.Settings => "настройки",
        OperatorUiAction.ReopenLogViewer => "открытие отдельной лог-консоли",
        OperatorUiAction.Details => "просмотр деталей",
        OperatorUiAction.Help => "помощь",
        OperatorUiAction.Exit => "завершение работы",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Неизвестное действие."),
    };

    internal static string DescribeMuMuState(MuMuLifecycleState state) => state switch
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
        bool Cancelled = false,
        MuMuInstanceId? ResolvedTarget = null,
        ApplicationFailure? Failure = null);
}
