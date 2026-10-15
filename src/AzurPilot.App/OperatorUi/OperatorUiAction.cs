using AzurPilot.Core.Configuration;

namespace AzurPilot.App.OperatorUi;

/// <summary>Реально доступные действия операторской поверхности.</summary>
/// <remarks>
/// Перечисление описывает только существующие production use cases. Отсутствующие capability (bot
/// backend, задачи бота, настройки нереализованных подсистем) здесь не представлены.
/// </remarks>
public enum OperatorUiAction
{
    /// <summary>Краткое read-only состояние среды.</summary>
    Status,
    /// <summary>Read-only диагностика приложения с полными техническими деталями.</summary>
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
    /// <summary>Запуск целевого MuMu и подготовка Android readiness без запуска игры.</summary>
    EnvironmentPrepare,
    /// <summary>
    /// Read-only разрешение точной identity целевого экземпляра по конфигурации.
    /// </summary>
    /// <remarks>
    /// Внутренний шаг пользовательского действия «Запустить окружение»: он не является отдельным
    /// пунктом меню, потому что оператор не должен различать разрешение target и саму подготовку.
    /// </remarks>
    ResolveTarget,
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
    /// <summary>Действующая эффективная конфигурация и реализуемая настройка.</summary>
    Settings,
    /// <summary>Явное повторное открытие отдельной read-only лог-консоли.</summary>
    ReopenLogViewer,
    /// <summary>Запрос отмены текущей операции.</summary>
    Cancel,
    /// <summary>Выбор и чтение технических деталей результата.</summary>
    Details,
    /// <summary>Помощь по доступным действиям текущего режима.</summary>
    Help,
    /// <summary>Завершение с ожиданием текущей операции.</summary>
    Exit,
}

/// <summary>Предметная категория действия: второй уровень палитры.</summary>
public enum OperatorUiCategory
{
    /// <summary>MuMu: обнаружение, выбор и запуск экземпляра.</summary>
    Environment,
    /// <summary>Android transport и readiness.</summary>
    Android,
    /// <summary>Lifecycle Azur Lane Global/EN.</summary>
    Game,
    /// <summary>Технические сведения, screenshot и диагностические результаты.</summary>
    Diagnostics,
    /// <summary>Состояние приложения, настройки, помощь и выход.</summary>
    Session,
}

/// <summary>Описание категории палитры.</summary>
/// <param name="Category">Идентификатор категории.</param>
/// <param name="Title">Человекочитаемое название.</param>
/// <param name="MinimumMode">Минимальный режим, в котором категория содержит доступные действия.</param>
public sealed record OperatorUiCategoryDefinition(
    OperatorUiCategory Category,
    string Title,
    OperatorUiMode MinimumMode);

/// <summary>Команда палитры и её presentation-метаданные.</summary>
/// <param name="Action">Идентификатор действия.</param>
/// <param name="Command">Точное имя команды composer.</param>
/// <param name="Title">Человекочитаемое название для пользователя.</param>
/// <param name="MinimumMode">Минимальный режим, в котором действие доступно.</param>
/// <param name="Category">Предметная категория действия.</param>
/// <param name="ShowInPalette">
/// Показывать ли действие в палитре. Клавиатурные действия (например отмена) остаются доступными при
/// прямом вводе, но не занимают строку меню.
/// </param>
/// <param name="TechnicalAlias">Совместимое техническое имя, доступное только в manual/diagnostic.</param>
/// <param name="InternalOnly">Внутренний шаг orchestration, который не принимается из composer.</param>
public sealed record OperatorUiCommand(
    OperatorUiAction Action,
    string Command,
    string Title,
    OperatorUiMode MinimumMode,
    OperatorUiCategory Category,
    bool ShowInPalette = true,
    string? TechnicalAlias = null,
    bool InternalOnly = false)
{
    /// <summary>Признак изменения состояния, требующего отдельного подтверждения.</summary>
    public bool RequiresConfirmation => Action is OperatorUiAction.MuMuStart or OperatorUiAction.MuMuStop
        or OperatorUiAction.MuMuRestart or OperatorUiAction.AndroidPrepare or OperatorUiAction.EnvironmentPrepare
        or OperatorUiAction.GameStart
        or OperatorUiAction.GameStop or OperatorUiAction.GameRestart;

    /// <summary>Признак действия над exact выбранным экземпляром.</summary>
    public bool RequiresInstance => RequiresConfirmation
        || Action is OperatorUiAction.AndroidObserve or OperatorUiAction.GameObserve or OperatorUiAction.Screenshot;

    /// <summary>Признак service operation, занимающей единый слот UI.</summary>
    public bool IsOperation => RequiresInstance
        || Action is OperatorUiAction.Status or OperatorUiAction.Diagnostics or OperatorUiAction.Instances
            or OperatorUiAction.ResolveTarget or OperatorUiAction.ReopenLogViewer;

    /// <summary>Действие доступно в указанном режиме.</summary>
    /// <param name="mode">Текущий режим интерфейса.</param>
    /// <returns>Разрешено ли действие.</returns>
    public bool IsAvailableIn(OperatorUiMode mode) => mode >= MinimumMode;

    /// <summary>Минимальный режим для точного имени, включая прежнее техническое имя.</summary>
    public OperatorUiMode RequiredModeFor(string input) => string.Equals(input, TechnicalAlias, StringComparison.OrdinalIgnoreCase)
        ? OperatorUiMode.Manual : MinimumMode;

    /// <summary>Предикат доступности при занятом единственном слоте операции.</summary>
    public bool CanExecute(bool operationActive) => !operationActive
        || Action is OperatorUiAction.Cancel or OperatorUiAction.Help or OperatorUiAction.Exit;

    /// <summary>
    /// Все команды; отсутствующие product capabilities не представлены.
    /// </summary>
    /// <remarks>
    /// Каждый пункт несёт режим доступности, предметную категорию и human-readable название. Единый
    /// каталог остаётся единственным владельцем dispatch-имён: второй список строк не заводится.
    /// </remarks>
    public static IReadOnlyList<OperatorUiCommand> All { get; } = Array.AsReadOnly<OperatorUiCommand>(
    [
        // Пользовательские действия основного режима. Именно они образуют первый уровень палитры.
        new(OperatorUiAction.EnvironmentPrepare, "/environment-start", "Запустить окружение",
            OperatorUiMode.Normal, OperatorUiCategory.Environment, TechnicalAlias: "/environment-prepare"),
        new(OperatorUiAction.MuMuStop, "/environment-stop", "Остановить окружение",
            OperatorUiMode.Normal, OperatorUiCategory.Environment, TechnicalAlias: "/mumu-stop"),
        new(OperatorUiAction.GameStart, "/game-start", "Запустить игру",
            OperatorUiMode.Normal, OperatorUiCategory.Game),
        new(OperatorUiAction.GameStop, "/game-stop", "Остановить игру",
            OperatorUiMode.Normal, OperatorUiCategory.Game),
        new(OperatorUiAction.Status, "/status", "Состояние",
            OperatorUiMode.Normal, OperatorUiCategory.Session),
        new(OperatorUiAction.Settings, "/settings", "Настройки",
            OperatorUiMode.Normal, OperatorUiCategory.Session),
        new(OperatorUiAction.Help, "/help", "Помощь",
            OperatorUiMode.Normal, OperatorUiCategory.Session),
        new(OperatorUiAction.Exit, "/exit", "Выход",
            OperatorUiMode.Normal, OperatorUiCategory.Session),
        // Отмена доступна всегда, но её место — клавиша F4, а не строка меню.
        new(OperatorUiAction.Cancel, "/cancel", "Отменить операцию",
            OperatorUiMode.Normal, OperatorUiCategory.Session, ShowInPalette: false),
        // Разрешение target — внутренний шаг «Запустить окружение», а не отдельное действие оператора.
        new(OperatorUiAction.ResolveTarget, "/resolve-target", "Определить целевой экземпляр",
            OperatorUiMode.Normal, OperatorUiCategory.Environment, ShowInPalette: false, InternalOnly: true),
        new(OperatorUiAction.ReopenLogViewer, "/viewer-reopen", "Открыть отдельную лог-консоль снова",
            OperatorUiMode.Normal, OperatorUiCategory.Session, ShowInPalette: false),

        // Ручное управление: только manual и выше, скрыто за категориями.
        new(OperatorUiAction.Instances, "/instances", "Обнаружить экземпляры MuMu",
            OperatorUiMode.Manual, OperatorUiCategory.Environment),
        new(OperatorUiAction.SelectInstance, "/select", "Выбрать точный экземпляр MuMu",
            OperatorUiMode.Manual, OperatorUiCategory.Environment),
        new(OperatorUiAction.MuMuStart, "/mumu-start", "Запустить MuMu",
            OperatorUiMode.Manual, OperatorUiCategory.Environment),
        new(OperatorUiAction.MuMuRestart, "/mumu-restart", "Перезапустить MuMu",
            OperatorUiMode.Manual, OperatorUiCategory.Environment),
        new(OperatorUiAction.AndroidObserve, "/android", "Наблюдать Android без подключения",
            OperatorUiMode.Manual, OperatorUiCategory.Android),
        new(OperatorUiAction.AndroidPrepare, "/android-prepare", "Подготовить Android transport",
            OperatorUiMode.Manual, OperatorUiCategory.Android),
        new(OperatorUiAction.GameObserve, "/game", "Наблюдать Azur Lane Global/EN",
            OperatorUiMode.Manual, OperatorUiCategory.Game),
        new(OperatorUiAction.GameRestart, "/game-restart", "Перезапустить Azur Lane",
            OperatorUiMode.Manual, OperatorUiCategory.Game),

        // Полный технический инструментарий: только diagnostic.
        new(OperatorUiAction.Diagnostics, "/diagnostics", "Диагностика приложения",
            OperatorUiMode.Diagnostic, OperatorUiCategory.Diagnostics),
        new(OperatorUiAction.Screenshot, "/screenshot", "Снимок: FullFrame и Center scan",
            OperatorUiMode.Diagnostic, OperatorUiCategory.Diagnostics),
        new(OperatorUiAction.Details, "/details", "Технические детали результатов и журнала",
            OperatorUiMode.Diagnostic, OperatorUiCategory.Diagnostics),
    ]);

    /// <summary>Категории второго уровня палитры.</summary>
    public static IReadOnlyList<OperatorUiCategoryDefinition> Categories { get; } =
        Array.AsReadOnly<OperatorUiCategoryDefinition>(
        [
            new(OperatorUiCategory.Environment, "MuMu", OperatorUiMode.Manual),
            new(OperatorUiCategory.Android, "Android", OperatorUiMode.Manual),
            new(OperatorUiCategory.Game, "Игра", OperatorUiMode.Manual),
            new(OperatorUiCategory.Diagnostics, "Диагностика", OperatorUiMode.Diagnostic),
        ]);

    /// <summary>Возвращает описание существующего действия.</summary>
    /// <param name="action">Действие.</param>
    /// <returns>Единственная соответствующая команда.</returns>
    public static OperatorUiCommand For(OperatorUiAction action) => All.First(command => command.Action == action);

    /// <summary>Ищет команду по точному тексту, введённому оператором.</summary>
    /// <param name="input">Введённая строка.</param>
    /// <returns>Команда либо <see langword="null"/>, если такой команды нет.</returns>
    public static OperatorUiCommand? Match(string input) => All.FirstOrDefault(command =>
        !command.InternalOnly && (string.Equals(command.Command, input, StringComparison.OrdinalIgnoreCase)
        || string.Equals(command.TechnicalAlias, input, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Действия первого уровня, доступные в режиме.</summary>
    /// <param name="mode">Текущий режим.</param>
    /// <returns>Команды основного уровня в порядке каталога.</returns>
    /// <remarks>
    /// Первый уровень намеренно короткий: он несёт пользовательские операции основного режима, а
    /// технические команды доступны только через свою категорию.
    /// </remarks>
    public static IEnumerable<OperatorUiCommand> PrimaryFor(OperatorUiMode mode) => All.Where(command =>
        command.ShowInPalette && command.MinimumMode == OperatorUiMode.Normal && command.IsAvailableIn(mode));

    /// <summary>Категории второго уровня, содержащие доступные в режиме действия.</summary>
    /// <param name="mode">Текущий режим.</param>
    /// <returns>Категории, у которых есть хотя бы одно доступное действие выше основного уровня.</returns>
    public static IEnumerable<OperatorUiCategoryDefinition> CategoriesFor(OperatorUiMode mode) =>
        Categories.Where(category => category.MinimumMode <= mode && All.Any(command =>
            command.Category == category.Category && command.MinimumMode > OperatorUiMode.Normal
            && command.IsAvailableIn(mode)));

    /// <summary>Действия указанной категории, доступные в режиме.</summary>
    /// <param name="category">Категория.</param>
    /// <param name="mode">Текущий режим.</param>
    /// <returns>Команды категории в порядке каталога.</returns>
    public static IEnumerable<OperatorUiCommand> InCategory(OperatorUiCategory category, OperatorUiMode mode) =>
        All.Where(command => command.Category == category && command.MinimumMode > OperatorUiMode.Normal
            && command.IsAvailableIn(mode));
}
