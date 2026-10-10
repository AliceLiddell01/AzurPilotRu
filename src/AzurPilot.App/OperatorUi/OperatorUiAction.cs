namespace AzurPilot.App.OperatorUi;

/// <summary>Реально доступные действия операторской поверхности.</summary>
public enum OperatorUiAction
{
    /// <summary>Read-only диагностика.</summary>
    Diagnostics,
    /// <summary>Inventory MuMu.</summary>
    Instances,
    /// <summary>Выбор exact identity из inventory.</summary>
    SelectInstance,
    /// <summary>Запуск выбранного MuMu.</summary>
    MuMuStart,
    /// <summary>Остановка выбранного MuMu.</summary>
    MuMuStop,
    /// <summary>Перезапуск выбранного MuMu.</summary>
    MuMuRestart,
    /// <summary>Read-only Android observation.</summary>
    AndroidObserve,
    /// <summary>Явная подготовка Android transport.</summary>
    AndroidPrepare,
    /// <summary>Read-only Azur Lane observation.</summary>
    GameObserve,
    /// <summary>Запуск Azur Lane.</summary>
    GameStart,
    /// <summary>Остановка Azur Lane.</summary>
    GameStop,
    /// <summary>Перезапуск Azur Lane.</summary>
    GameRestart,
    /// <summary>Screenshot с FullFrame и Center scan.</summary>
    Screenshot,
    /// <summary>Запрос отмены текущей операции.</summary>
    Cancel,
    /// <summary>Переключение независимой панели логов.</summary>
    Logs,
    /// <summary>Помощь по доступным действиям.</summary>
    Help,
    /// <summary>Завершение с ожиданием текущей операции.</summary>
    Exit,
}

/// <summary>Команда палитры и её presentation-метаданные.</summary>
/// <param name="Action">Идентификатор действия.</param>
/// <param name="Command">Точное имя команды composer.</param>
/// <param name="Title">Человекочитаемое описание.</param>
public sealed record OperatorUiCommand(OperatorUiAction Action, string Command, string Title)
{
    /// <summary>Признак изменения состояния, требующего отдельного подтверждения.</summary>
    public bool RequiresConfirmation => Action is OperatorUiAction.MuMuStart or OperatorUiAction.MuMuStop
        or OperatorUiAction.MuMuRestart or OperatorUiAction.AndroidPrepare or OperatorUiAction.GameStart
        or OperatorUiAction.GameStop or OperatorUiAction.GameRestart;

    /// <summary>Признак действия над exact выбранным экземпляром.</summary>
    public bool RequiresInstance => RequiresConfirmation
        || Action is OperatorUiAction.AndroidObserve or OperatorUiAction.GameObserve or OperatorUiAction.Screenshot;

    /// <summary>Признак service operation, занимающей единый слот UI.</summary>
    public bool IsOperation => RequiresInstance || Action is OperatorUiAction.Diagnostics or OperatorUiAction.Instances;

    /// <summary>Все доступные команды; отсутствующие product capabilities не представлены.</summary>
    public static IReadOnlyList<OperatorUiCommand> All { get; } = Array.AsReadOnly<OperatorUiCommand>(
    [
        new(OperatorUiAction.Diagnostics, "/diagnostics", "Диагностика приложения"),
        new(OperatorUiAction.Instances, "/instances", "Обнаружить экземпляры MuMu"),
        new(OperatorUiAction.SelectInstance, "/select", "Выбрать конкретный экземпляр MuMu"),
        new(OperatorUiAction.AndroidObserve, "/android", "Наблюдать Android без подключения"),
        new(OperatorUiAction.GameObserve, "/game", "Наблюдать Azur Lane Global/EN"),
        new(OperatorUiAction.Screenshot, "/screenshot", "Screenshot: FullFrame и Center scan"),
        new(OperatorUiAction.MuMuStart, "/mumu-start", "Запустить MuMu"),
        new(OperatorUiAction.MuMuStop, "/mumu-stop", "Остановить MuMu"),
        new(OperatorUiAction.MuMuRestart, "/mumu-restart", "Перезапустить MuMu"),
        new(OperatorUiAction.AndroidPrepare, "/android-prepare", "Подготовить Android transport"),
        new(OperatorUiAction.GameStart, "/game-start", "Запустить Azur Lane"),
        new(OperatorUiAction.GameStop, "/game-stop", "Остановить Azur Lane"),
        new(OperatorUiAction.GameRestart, "/game-restart", "Перезапустить Azur Lane"),
        new(OperatorUiAction.Cancel, "/cancel", "Отменить текущую операцию"),
        new(OperatorUiAction.Logs, "/logs", "Показать или скрыть логи"),
        new(OperatorUiAction.Help, "/help", "Помощь"),
        new(OperatorUiAction.Exit, "/exit", "Завершить работу"),
    ]);

    /// <summary>Возвращает description существующего действия.</summary>
    /// <param name="action">Действие.</param>
    /// <returns>Единственная соответствующая команда.</returns>
    public static OperatorUiCommand For(OperatorUiAction action) => All.First(command => command.Action == action);
}
