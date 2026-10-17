using System.Drawing;
using System.Text;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AzurPilot.App.OperatorUi;

/// <summary>Интерактивная операторская поверхность поверх существующих сценариев приложения.</summary>
/// <remarks>
/// <para>
/// UI владеет только presentation-состоянием. Task исполняется вне потока UI, а все изменения представлений
/// применяются одним обратным вызовом таймера. Выход отменяет и дожидается текущей операции до освобождения терминала.
/// </para>
/// <para>
/// Встроенной области журнала нет ни в одном режиме: технический поток принадлежит отдельной консоли
/// (<c>logging.viewer.enabled</c>), а на экране остаются краткие результаты и явно запрошенные детали.
/// Поэтому геометрия главного экрана не резервирует высоту под лог.
/// </para>
/// </remarks>
public sealed class AzurPilotOperatorUi : IDisposable
{
    private const int MinimumWidth = 60;
    private const int MinimumHeight = 18;
    private const int MaximumRenderedRows = 2048;
    /// <summary>Заголовок записи, которой UI отмечает введённую оператором команду.</summary>
    private const string OperatorEchoTitle = "Оператор";
    /// <summary>Символ приглашения ввода: отделяет ввод от истории и не встречается в тексте сообщений.</summary>
    private const string ComposerPromptGlyph = "\u276f";
    /// <summary>Нейтральная схема: и текст, и фон наследуются от темы терминала.</summary>
    private static readonly Scheme NeutralScheme = new(new Terminal.Gui.Drawing.Attribute("None", "None", TextStyle.None));
    private Scheme _primaryScheme = NeutralScheme;
    private Scheme _mutedScheme = NeutralScheme;
    private Scheme _successScheme = NeutralScheme;
    private Scheme _warningScheme = NeutralScheme;
    private Scheme _errorScheme = NeutralScheme;
    private Scheme _selectionScheme = NeutralScheme;
    private Scheme _accentScheme = NeutralScheme;
    private Scheme _disabledScheme = NeutralScheme;
    private bool? _darkBackground;
    private readonly OperatorUiController _controller;
    private readonly IOperatorUiLogSource _logSource;
    private readonly OperatorUiSession _session;
    private readonly OperatorUiMode _mode;
    private readonly List<PaletteChoice> _choices = [];
    private IApplication? _application;
    private Runnable? _window;
    private View? _historyView;
    private View? _paletteView;
    private TextField? _composer;
    private Label? _composerPrompt;
    private Label? _composerRule;
    private Label? _header;
    private Label? _operation;
    private Label? _historyHeading;
    private Label? _footer;
    private Label? _sizeNotice;
    private IReadOnlyList<AzurPilotOperatorMuMuInstance> _instances = [];
    private OperatorUiMessage? _detailsMessage;
    private AzurPilotOperatorLogEntry? _detailsLogEntry;
    private OperatorUiMessage? _settingsMessage;
    private PaletteMode _paletteMode;
    private OperatorUiCategory? _openCategory;
    /// <summary>Действие, подтверждение которого ожидается после разрешения target.</summary>
    private OperatorUiAction? _pendingConfirmation;
    /// <summary>Ожидается завершение read-only разрешения точной identity целевого экземпляра.</summary>
    private bool _awaitingTargetResolution;
    private bool _saveChosenTarget;
    private int _choiceIndex;
    private int _messageRows;
    private int _messageWrapWidth;
    private Size _lastSize;
    private bool _messagesDirty = true;
    private bool _followMessages = true;
    private bool _exitRequested;
    private bool _suppressCompletion;
    private bool _hasRun;

    /// <summary>
    /// Строит спокойную палитру после Init(), когда доступна реальная пара цветов темы терминала.
    /// </summary>
    /// <remarks>
    /// Фон везде наследуется от терминала (Color.None), поэтому сплошных плашек и инверсных полос нет,
    /// а явные цвета выбираются по полярности фона и удерживают контраст не ниже 4.5:1. Роли несут
    /// семантику: нейтральный основной текст, приглушённое второстепенное, зелёный успех, жёлтый
    /// внимание, красный отказ, акцент для выбора и активного ввода, приглушённый цвет недоступного действия.
    /// </remarks>
    /// <param name="darkBackground">Фон терминала тёмный.</param>
    private void BuildPalette(bool darkBackground)
    {
        _darkBackground = darkBackground;
        string muted = darkBackground ? "#9A9A9A" : "#5F6368";
        string accent = darkBackground ? "#4E9ACF" : "#2E6DA4";
        string success = darkBackground ? "#7FB069" : "#217A3C";
        string warning = darkBackground ? "#C19C00" : "#8A6100";
        string error = darkBackground ? "#E06C75" : "#B3261E";
        string disabled = darkBackground ? "#6E6E6E" : "#8C8C8C";
        _primaryScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute("None", "None", TextStyle.None));
        _mutedScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(muted, "None", TextStyle.None));
        _successScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(success, "None", TextStyle.None));
        _warningScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(warning, "None", TextStyle.None));
        _errorScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(error, "None", TextStyle.None));
        _selectionScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(accent, "None", TextStyle.Bold));
        _accentScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(accent, "None", TextStyle.None));
        _disabledScheme = CalmScheme(new Terminal.Gui.Drawing.Attribute(disabled, "None", TextStyle.None));
    }

    /// <summary>
    /// Задаёт все роли схемы явно: иначе Terminal.Gui выводит Focus/Active/Highlight из темы и
    /// получает полную инверсию Normal, то есть те самые сплошные полосы.
    /// </summary>
    /// <param name="normal">Основной атрибут роли Normal.</param>
    /// <returns>Схема без выведенных ролей.</returns>
    private static Scheme CalmScheme(Terminal.Gui.Drawing.Attribute normal) => new(normal)
    {
        HotNormal = normal,
        Focus = normal,
        HotFocus = normal,
        Active = normal,
        HotActive = normal,
        Highlight = normal,
        Editable = normal,
        ReadOnly = normal,
        Disabled = normal,
    };

    /// <summary>Использует уже собранный снимок при запуске и общий источник логов хоста приложения.</summary>
    /// <param name="service">Владелец сценариев приложения.</param>
    /// <param name="initialDiagnostics">Диагностика текущего запуска без повторной инициализации.</param>
    /// <param name="logSource">Потокобезопасный ограниченный источник логов для явно запрошенных деталей.</param>
    /// <param name="mode">Режим интерфейса: объём доступных действий.</param>
    /// <param name="logViewerFailure">
    /// Отказ открытия отдельной лог-консоли либо <see langword="null"/>. Отказ просмотра не является
    /// отказом запуска: он показывается коротким сообщением.
    /// </param>
    public AzurPilotOperatorUi(
        AzurPilotOperatorService service,
        AzurPilotDiagnosticReport initialDiagnostics,
        IOperatorUiLogSource logSource,
        OperatorUiMode mode = OperatorUiMode.Normal,
        ApplicationFailure? logViewerFailure = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(initialDiagnostics);
        ArgumentNullException.ThrowIfNull(logSource);
        _mode = mode;
        _controller = new OperatorUiController(service, initialDiagnostics, mode, logViewerFailure);
        _session = _controller.Session;
        _logSource = logSource;
    }

    /// <summary>Текущий режим интерфейса.</summary>
    internal OperatorUiMode Mode => _mode;

    /// <summary>Запускает одну интерактивную сессию на вызывающем потоке.</summary>
    /// <param name="cancellationToken">Запрос управляемого выхода от хоста приложения.</param>
    /// <exception cref="InvalidOperationException">Повторный запуск либо redirected stdin/stdout.</exception>
    public void Run(CancellationToken cancellationToken = default)
    {
        if (_hasRun)
        {
            throw new InvalidOperationException("Операторская сессия уже запускалась.");
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            throw new InvalidOperationException("Интерактивному режиму нужен терминал с неперенаправленными stdin/stdout.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _hasRun = true;
        using WindowsConsoleOutputMode outputMode = WindowsConsoleOutputMode.EnableVirtualTerminalProcessing();
        using IApplication application = Application.Create();
        _application = application;
        _ = application.Init();
        Terminal.Gui.Drawing.Color terminalBackground =
            application.Driver!.DefaultAttribute?.Background ?? Terminal.Gui.Drawing.Color.None;
        if (terminalBackground.A == 0 && outputMode.BackgroundRgb is int backgroundRgb)
        {
            terminalBackground = new Terminal.Gui.Drawing.Color((byte)(backgroundRgb >> 16),
                (byte)(backgroundRgb >> 8), (byte)backgroundRgb);
        }
        // Неразрешённый фон темы (Color.None) считаем тёмным: это исторический вид поверхности.
        BuildPalette(terminalBackground.A == 0 || terminalBackground.IsDarkColor());
        // Корневая поверхность без декоративной границы: содержимое занимает всю доступную площадь
        // терминала, а второго названия приложения внутри TUI нет.
        using Runnable window = new()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        InitializeViews(window);
        application.Keyboard.KeyDown += (_, key) => HandleKey(key);
        application.Mouse.MouseEvent += (_, mouse) => HandleMouse(mouse);
        window.Initialized += (_, _) => { RefreshLayout(); _ = _composer!.SetFocus(); };
        _ = application.AddTimeout(TimeSpan.FromMilliseconds(100), () => Tick(cancellationToken));
        try
        {
            _ = application.Run(window);
        }
        finally
        {
            _controller.Dispose();
            _application = null;
            _window = null;
        }
    }

    /// <summary>Создаёт те же элементы и подписки ввода без запуска терминального цикла.</summary>
    internal void InitializeViews(Runnable window)
    {
        _window = window;
        _header = new Label { Height = 1 };
        _operation = new Label { Height = 1 };
        _historyHeading = new Label { Height = 1 };
        _historyView = new View { CanFocus = false };
        _composerRule = new Label { Height = 1 };
        _composerPrompt = new Label { Height = 1, Width = 2, Text = ComposerPromptGlyph };
        _composer = new TextField { Height = 1 };
        _footer = new Label { Height = 1 };
        _paletteView = new View { CanFocus = false, Visible = false };
        _sizeNotice = new Label { Visible = false };
        window.Add(_header, _operation, _historyHeading, _historyView, _composerRule, _composerPrompt,
            _composer, _footer, _paletteView, _sizeNotice);
        // Все роли заданы явно, иначе производные роли дают непрозрачные полосы на строке ввода.
        foreach (View calmView in new View[] { _header, _operation, _historyHeading, _composer, _footer })
        {
            _ = calmView.SetScheme(_primaryScheme);
        }

        _ = _composerRule.SetScheme(_mutedScheme);
        _ = _historyHeading.SetScheme(_mutedScheme);
        _composer.TextChanged += (_, _) => UpdateCommandPalette();
    }

    internal string ComposerText
    {
        get => _composer!.Text;
        set => _composer!.Text = value;
    }

    internal PaletteMode CurrentPalette => _paletteMode;
    internal OperatorUiSession Session => _session;

    /// <summary>Освобождает coordinator после управляемого завершения операции.</summary>
    public void Dispose()
    {
        _controller.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool Tick(CancellationToken cancellationToken)
    {
        ApplyTerminalBackground(_application?.Driver?.DefaultAttribute?.Background ?? Terminal.Gui.Drawing.Color.None);
        if (cancellationToken.IsCancellationRequested && !_exitRequested)
        {
            RequestExit();
        }

        if (_controller.IsOperationCompleted)
        {
            CompleteOperation();
            if (_paletteMode != PaletteMode.Hidden)
            {
                RenderPalette();
            }
        }

        if (_exitRequested && !_session.IsOperationActive)
        {
            _application!.RequestStop();
            return false;
        }

        RefreshLayout();
        _messagesDirty |= _messageWrapWidth != _historyView!.Viewport.Width;
        if (_messagesDirty)
        {
            RenderMessages();
        }

        _header!.Text = BuildHeaderText();
        _operation!.Text = _controller.OperationText;
        RefreshComposerFocus();
        return true;
    }

    // OSC 10/11 завершается асинхронно уже после Init. Палитра обновляется только в цикле событий UI.
    internal void ApplyTerminalBackground(Terminal.Gui.Drawing.Color background)
    {
        if (background.A == 0 || _window is null)
        {
            return;
        }

        bool dark = background.IsDarkColor();
        if (_darkBackground == dark)
        {
            return;
        }

        BuildPalette(dark);
        _ = _historyHeading!.SetScheme(_mutedScheme);
        _messagesDirty = true;
        if (_paletteMode != PaletteMode.Hidden)
        {
            RenderPalette();
        }

        RefreshComposerFocus();
    }

    /// <summary>Строка состояния: выбранный target, режим и подсказка вызова палитры.</summary>
    private string BuildHeaderText()
    {
        string target = _session.SelectedInstance?.ToString() ?? "не выбран";
        return $"Экземпляр: {target}  |  Режим: {OperatorUiModePresentation.Label(_mode)}  |  / — действия";
    }

    /// <summary>Отдельная контрастная область ввода: приглашение ввода и линия-разделитель отражают фокус.</summary>
    private void RefreshComposerFocus()
    {
        bool focused = _composer!.HasFocus && _composer.Enabled;
        // Отключённое поле ввода (открыто подтверждение) отличается и от активного, и от неактивного.
        Scheme scheme = !_composer.Enabled ? _disabledScheme : focused ? _accentScheme : _mutedScheme;
        _ = _composerPrompt!.SetScheme(scheme);
        _ = _composerRule!.SetScheme(scheme);
    }

    private void RefreshLayout()
    {
        Size size = _window!.Viewport.Size;
        if (size == _lastSize)
        {
            return;
        }

        _lastSize = size;
        // Корень без рамки: доступная площадь совпадает с фактическими терминальными ячейками.
        bool small = size.Width < MinimumWidth || size.Height < MinimumHeight;
        _sizeNotice!.Visible = small;
        _sizeNotice.Text = $"Увеличьте терминал минимум до {MinimumWidth}×{MinimumHeight}. Ctrl+C — выход.";
        _sizeNotice.Width = Math.Max(1, size.Width);
        _sizeNotice.Height = Math.Max(1, size.Height);
        foreach (View view in new View[]
        {
            _header!, _operation!, _historyHeading!, _historyView!, _composerRule!, _composerPrompt!, _composer!, _footer!,
        })
        {
            view.Visible = !small;
        }

        _paletteView!.Visible = !small && _paletteMode != PaletteMode.Hidden;
        if (small)
        {
            return;
        }

        _header!.X = 0; _header.Y = 0; _header.Width = size.Width;
        _operation!.X = 0; _operation.Y = 1; _operation.Width = size.Width;
        // Закреплённый заголовок панели: он не участвует в прокрутке и остаётся видимым при длинной истории.
        _historyHeading!.X = 0; _historyHeading.Y = 2; _historyHeading.Width = size.Width;
        _historyHeading.Text = BuildHistoryHeading(size.Width);
        // Встроенной области журнала нет: история занимает всю высоту до области ввода.
        int composerRuleY = Math.Max(3, size.Height - 3);
        _historyView!.X = 0; _historyView.Y = 3;
        _historyView.Width = size.Width;
        _historyView.Height = Math.Max(1, composerRuleY - 3);
        _composerRule!.X = 0; _composerRule.Y = composerRuleY; _composerRule.Width = size.Width;
        _composerRule.Text = new string('\u2500', Math.Max(1, size.Width));
        _composerPrompt!.X = 0; _composerPrompt.Y = composerRuleY + 1;
        _composer!.X = 2; _composer.Y = composerRuleY + 1; _composer.Width = Math.Max(1, size.Width - 2);
        _footer!.X = 0; _footer.Y = size.Height - 1; _footer.Width = size.Width;
        _footer.Text = BuildFooterText(size.Width);
        _messagesDirty = true;
        RenderPalette();
    }

    private string BuildHistoryHeading(int width)
    {
        string hint = _mode == OperatorUiMode.Diagnostic
            ? " · /details — технические данные"
            : string.Empty;
        string heading = "\u2500\u2500 Сообщения и результаты" + hint + " ";
        int used = heading.EnumerateRunes().Sum(static rune => Math.Max(0, rune.GetColumns()));
        int tail = Math.Max(0, width - used);
        return heading + new string('\u2500', tail);
    }

    internal static string BuildFooterText(int width) => width switch
    {
        >= 94 => "↑↓ история · PgUp/PgDn · Ctrl+End конец · F4 отмена · Ctrl+C выход · F2 действия",
        >= 70 => "PgUp/PgDn · F4 отмена · Ctrl+C выход · F2 действия",
        _ => "F4 отмена · Ctrl+C выход · F2 действия",
    };

    internal void HandleKey(Key key)
    {
        if (key == Key.C.WithCtrl || key == Key.Q.WithCtrl)
        {
            if (_session.IsOperationActive && !_exitRequested)
            {
                CancelOperation();
            }
            else
            {
                RequestExit();
            }

            key.Handled = true;
            return;
        }

        if (key == Key.F4)
        {
            CancelOperation(); key.Handled = true; return;
        }

        if (_exitRequested || _sizeNotice!.Visible)
        {
            key.Handled = true; return;
        }

        if (key == Key.Esc || key == Key.F2)
        {
            ComposerKeyTransition transition = ApplyComposerKey(
                key,
                _composer!.Text,
                _paletteMode,
                _detailsMessage is not null || _detailsLogEntry is not null || _settingsMessage is not null,
                _session);
            if (transition.Effect == ComposerKeyEffect.ClearDetails)
            {
                _detailsMessage = null;
                _detailsLogEntry = null;
                _settingsMessage = null;
                _messagesDirty = true;
                _ = _composer.SetFocus();
            }
            else if (transition.Effect == ComposerKeyEffect.BackToCommands)
            {
                ShowCommandLevel();
            }
            else if (transition.Effect == ComposerKeyEffect.OpenPalette)
            {
                _session.CancelConfirmation();
                HidePalette();
                if (transition.ComposerText is string opened)
                {
                    SetComposerText(opened);
                }

                OpenCommands();
            }
            else
            {
                ClosePalette(restoreDraft: false);
                if (transition.ComposerText is string restored)
                {
                    SetComposerText(restored);
                }
            }

            key.Handled = true; return;
        }

        if (key == Key.PageUp || key == Key.PageDown || key == Key.End.WithCtrl)
        {
            ScrollMessages(key); key.Handled = true; return;
        }

        if (_paletteMode != PaletteMode.Hidden)
        {
            if (key == Key.CursorUp || key == Key.CursorDown)
            {
                _choiceIndex = Math.Clamp(_choiceIndex + (key == Key.CursorUp ? -1 : 1), 0,
                    Math.Max(0, _choices.Count - 1));
                RenderPalette(); key.Handled = true; return;
            }

            // Home/End перемещают по меню целиком: узкая палитра не должна делать последние пункты
            // достижимыми только многократным нажатием стрелки.
            if (key == Key.Home || key == Key.End)
            {
                _choiceIndex = key == Key.Home ? 0 : Math.Max(0, _choices.Count - 1);
                RenderPalette(); key.Handled = true; return;
            }

            if (key == Key.Tab || key == Key.Enter)
            {
                AcceptChoice(key == Key.Tab); key.Handled = true; return;
            }

            // В подтверждении текст и paste не должны неявно выбирать положительный ответ.
            if (_paletteMode is PaletteMode.Confirmation or PaletteMode.Instances or PaletteMode.Settings)
            {
                key.Handled = true; return;
            }
        }

        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _suppressCompletion = true;
            _composer!.Text = _session.NavigateHistory(key == Key.CursorUp ? -1 : 1, _composer.Text);
            _composer.InsertionPoint = _composer.Text.Length;
            _suppressCompletion = false;
            key.Handled = true; return;
        }

        if (key == Key.Enter)
        {
            SubmitComposer(); key.Handled = true;
        }
        else if (key == Key.Tab)
        {
            ComposerKeyTransition transition = ApplyComposerKey(
                Key.Tab, _composer!.Text, _paletteMode, detailsVisible: false, _session);
            if (transition.ComposerText is string completed)
            {
                SetComposerText(completed);
            }

            OpenCommands(); key.Handled = true;
        }
    }

    private void UpdateCommandPalette()
    {
        if (_suppressCompletion || _paletteMode is PaletteMode.Confirmation or PaletteMode.Instances or PaletteMode.Settings)
        {
            return;
        }

        if (_composer!.Text.Length > 512)
        {
            _suppressCompletion = true;
            _composer.Text = OperatorUiSession.CleanText(_composer.Text, 512);
            _suppressCompletion = false;
        }

        if (_composer.Text.StartsWith('/'))
        {
            _ = OpenPalette(_session, _paletteMode, ComposerPaletteSource.Trigger, _composer.Text);
            OpenCommands();
        }
        else if (_paletteMode is PaletteMode.Commands or PaletteMode.Category)
        {
            // Строка перестала быть запросом палитры: закрываем без восстановления черновика.
            ClosePalette(restoreDraft: false);
        }
    }

    /// <summary>Открывает первый уровень палитры: основные действия и категории режима.</summary>
    private void OpenCommands()
    {
        bool wasShowingDetails = _detailsMessage is not null || _detailsLogEntry is not null
            || _settingsMessage is not null;
        _detailsMessage = null;
        _detailsLogEntry = null;
        _settingsMessage = null;
        _messagesDirty |= wasShowingDetails;
        _session.CancelConfirmation();
        _composer!.Enabled = true;
        _paletteMode = PaletteMode.Commands;
        _openCategory = null;
        _choices.Clear();
        string filter = _composer!.Text.Trim();
        foreach (OperatorUiCommand command in OperatorUiCommand.PrimaryFor(_mode))
        {
            if (MatchesFilter(command, filter))
            {
                _choices.Add(new PaletteChoice(ChoiceTitle(command), command.Action));
            }
        }

        foreach (OperatorUiCategoryDefinition category in OperatorUiCommand.CategoriesFor(_mode))
        {
            if (filter.Length <= 1 || OperatorUiCommand.InCategory(category.Category, _mode)
                .Any(command => MatchesFilter(command, filter)))
            {
                _choices.Add(new PaletteChoice(category.Title + " \u25b8", Category: category.Category));
            }
        }

        _choiceIndex = 0;
        RenderPalette();
    }

    /// <summary>Открывает второй уровень палитры: действия выбранной категории.</summary>
    private void OpenCategory(OperatorUiCategory category)
    {
        _paletteMode = PaletteMode.Category;
        _openCategory = category;
        _composer!.Enabled = true;
        _choices.Clear();
        foreach (OperatorUiCommand command in OperatorUiCommand.InCategory(category, _mode))
        {
            _choices.Add(new PaletteChoice(ChoiceTitle(command), command.Action));
        }

        _choiceIndex = 0;
        RenderPalette();
    }

    /// <summary>Возвращает палитру на первый уровень без потери черновика.</summary>
    private void ShowCommandLevel()
    {
        OpenCommands();
        _ = _composer!.SetFocus();
    }

    private static bool MatchesFilter(OperatorUiCommand command, string filter) =>
        filter.Length <= 1
        || command.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || command.Title.Contains(filter.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private static string ChoiceTitle(OperatorUiCommand command) =>
        command.Title + (command.RequiresConfirmation ? " [подтверждение]" : string.Empty);

    private void RenderPalette()
    {
        if (_paletteView is null)
        {
            return;
        }

        _paletteView.Visible = _paletteMode != PaletteMode.Hidden && !_sizeNotice!.Visible;
        if (!_paletteView.Visible)
        {
            return;
        }

        RemoveChildren(_paletteView);
        string[] confirmationLines = _paletteMode == PaletteMode.Confirmation
            ? [.. ConfirmationPlan(_session.PendingConfirmation)
                .SelectMany(line => Wrap(line, Math.Max(1, _lastSize.Width - 2)))]
            : [];
        int desiredRows = 1 + confirmationLines.Length + _choices.Count;
        int visibleBudget = Math.Max(4, _lastSize.Height - 6);
        int rows = _paletteMode == PaletteMode.Confirmation
            ? Math.Min(Math.Min(visibleBudget, 12), desiredRows)
            : Math.Min(Math.Min(visibleBudget, _choices.Count + 2), visibleBudget);
        // Палитра заканчивается строкой выше линии ввода: область ввода не перекрывается меню.
        _paletteView.X = 0; _paletteView.Y = Math.Max(2, _lastSize.Height - 3 - rows);
        _paletteView.Width = Math.Max(1, _lastSize.Width); _paletteView.Height = rows;
        _ = AddLine(_paletteView, 0, PaletteTitle()).SetScheme(_mutedScheme);
        int planRows = Math.Min(confirmationLines.Length, Math.Max(0, rows - 1 - _choices.Count));
        for (int index = 0; index < planRows; index++)
        {
            _ = AddLine(_paletteView, index + 1, confirmationLines[index]);
        }

        int availableChoiceRows = Math.Max(0, rows - 1 - planRows);
        int first = _paletteMode == PaletteMode.Confirmation
            ? Math.Max(0, _choiceIndex - Math.Max(0, availableChoiceRows - 1))
            : Math.Max(0, _choiceIndex - (rows - 3));
        for (int index = first; index < Math.Min(_choices.Count, first + availableChoiceRows); index++)
        {
            bool disabled = _choices[index].Action is OperatorUiAction action
                && !OperatorUiCommand.For(action).CanExecute(_session.IsOperationActive);
            Label line = AddLine(_paletteView, index - first + 1,
                (index == _choiceIndex ? "> " : "  ") + _choices[index].Title
                + (disabled ? " · занято" : string.Empty));
            line.Y = planRows + index - first + 1;
            if (disabled)
            {
                _ = line.SetScheme(_disabledScheme);
            }
            else if (index == _choiceIndex)
            {
                _ = line.SetScheme(_selectionScheme);
            }
        }

        if (_choices.Count == 0)
        {
            _ = AddLine(_paletteView, 1, "Нет подходящих действий. Esc — закрыть.");
        }
    }

    private string PaletteTitle() => _paletteMode switch
    {
        PaletteMode.Hidden => string.Empty,
        PaletteMode.Commands => "Действия · ↑↓ выбор · Enter открыть · Esc закрыть",
        PaletteMode.Category => OperatorUiCommand.Categories.First(category =>
            category.Category == _openCategory).Title + " · ↑↓ выбор · Enter выполнить · Esc назад",
        PaletteMode.Instances => "Выберите точный MuMu ID · ↑↓ Enter · Esc отмена",
        PaletteMode.Settings => "Настройки следующего запуска · Enter сохранить · Esc закрыть",
        PaletteMode.Details => "Выберите результат или запись журнала · Esc закрыть",
        PaletteMode.Confirmation => "Подтверждение · по умолчанию отмена",
        _ => string.Empty,
    };

    private IReadOnlyList<string> ConfirmationPlan(OperatorUiConfirmation? confirmation)
    {
        if (_mode == OperatorUiMode.Normal && confirmation is OperatorUiConfirmation normal)
        {
            if (normal.Action == OperatorUiAction.EnvironmentPrepare)
            {
                return
                [
                    $"Запустить MuMu {normal.Target}, если он остановлен, и подготовить Android.",
                    "Игра запускаться не будет. Работающий MuMu не перезапускается.",
                    "При отмене запущенный MuMu автоматически не останавливается.",
                ];
            }

            if (normal.Action == OperatorUiAction.MuMuStop)
            {
                return
                [
                    $"Остановить MuMu {normal.Target} вместе с Android и запущенной игрой.",
                    "Несохранённые данные внутри эмулятора могут быть потеряны.",
                ];
            }

            if (normal.Action == OperatorUiAction.GameStart)
            {
                return
                [
                    $"Запустить Azur Lane Global/EN на {normal.Target}.",
                    "При необходимости MuMu и Android будут подготовлены автоматически.",
                ];
            }

            return [$"{OperatorUiCommand.For(normal.Action).Title} · {normal.Target}."];
        }

        if (confirmation is not { Action: OperatorUiAction.EnvironmentPrepare } pending)
        {
            return [];
        }

        return
        [
            $"Экземпляр {pending.Target}: повторно наблюдать точное состояние MuMu.",
            "Запуск — только из доказанного Stopped.",
            "Running не перезапускается.",
            "Неизвестное состояние или отказ предварительной проверки — завершить без запуска.",
            $"Endpoint разрешается только для {pending.Target}.",
            "Подготовить транспорт; проверить shell и завершение загрузки.",
            "Без запуска Azur Lane; при сбое/отмене MuMu может остаться запущенным.",
        ];
    }

    private void AcceptChoice(bool completeOnly)
    {
        if (!completeOnly && _paletteMode == PaletteMode.Commands && IsDirectCommand(_composer!.Text))
        {
            // Точное имя в поле ввода имеет приоритет над строкой категории. Иначе Enter после
            // /screenshot или /android открывал первую категорию вместо проверки доступа и маршрутизации.
            HidePalette();
            SubmitComposer();
            return;
        }

        if (_paletteMode == PaletteMode.Settings && _choices.Count != 0)
        {
            if (!completeOnly)
            {
                ApplySetting(_choices[_choiceIndex].Setting);
            }

            return;
        }

        if (_choices.Count == 0)
        {
            // Пустой список — не тупик: введённый текст всё равно доходит до правила доступа, поэтому
            // техническая команда в обычном режиме получает объяснение, а не молчание.
            if (_paletteMode == PaletteMode.Commands && !completeOnly)
            {
                HidePalette();
                SubmitComposer();
            }

            return;
        }

        PaletteChoice choice = _choices[_choiceIndex];
        if (_paletteMode == PaletteMode.Confirmation)
        {
            if (!completeOnly && choice.Confirm && _session.ConfirmMutation())
            {
                OperatorUiAction action = choice.Action!.Value;
                if (choice.SaveTarget && _session.SelectedInstance is MuMuInstanceId target
                    && !_controller.SaveSettings(_controller.SavedConfiguration with
                    {
                        MuMu = new MuMuConfiguration { Instance = target.ToString() },
                    }))
                {
                    ClosePalette();
                    return;
                }

                HidePalette(); StartOperation(action);
            }
            else
            {
                ClosePalette();
            }

            return;
        }

        if (_paletteMode == PaletteMode.Instances)
        {
            bool selected = choice.Instance is MuMuInstanceId identity && _session.SelectInstance(identity);
            if (selected && choice.Instance is MuMuInstanceId chosen)
            {
                AddMessage("Выбор экземпляра", chosen.ToString());
                if (_saveChosenTarget)
                {
                    _saveChosenTarget = false;
                    _ = _controller.SaveSettings(_controller.SavedConfiguration with
                    {
                        MuMu = new MuMuConfiguration { Instance = chosen.ToString() },
                    });
                }
            }

            // Выбор точного ID завершает исходное пользовательское действие: подтверждение
            // предъявляется сразу, а не требует повторного запуска команды.
            if (selected && _pendingConfirmation is OperatorUiAction pending)
            {
                _pendingConfirmation = null;
                ShowConfirmation(OperatorUiCommand.For(pending));
                return;
            }

            ClosePalette();
            return;
        }

        if (_paletteMode == PaletteMode.Details)
        {
            if (completeOnly)
            {
                return;
            }

            _detailsMessage = choice.Message;
            _detailsLogEntry = choice.LogEntry;
            _settingsMessage = null;
            _followMessages = true;
            _messagesDirty = true;
            HidePalette();
            return;
        }

        if (_paletteMode == PaletteMode.Category)
        {
            // Enter в категории исполняет действие, Tab оставляет выбор в меню.
            if (completeOnly)
            {
                return;
            }

            OperatorUiCommand categoryCommand = OperatorUiCommand.For(choice.Action!.Value);
            _session.DiscardComposerDraft();
            SetComposerText(categoryCommand.Command);
            HidePalette();
            SubmitComposer();
            return;
        }

        if (choice.Category is OperatorUiCategory category)
        {
            if (completeOnly)
            {
                return;
            }

            OpenCategory(category);
            return;
        }

        OperatorUiCommand command = OperatorUiCommand.For(choice.Action!.Value);
        _session.DiscardComposerDraft();
        SetComposerText(command.Command);
        HidePalette();
        if (!completeOnly)
        {
            SubmitComposer();
        }
    }

    private void HidePalette()
    {
        _paletteMode = PaletteMode.Hidden;
        _openCategory = null;
        _choices.Clear();
        _paletteView!.Visible = false;
        _composer!.Enabled = true;
    }

    private void ClosePalette(bool restoreDraft = true)
    {
        _session.CancelConfirmation();
        HidePalette();
        _pendingConfirmation = null;
        _awaitingTargetResolution = false;
        _saveChosenTarget = false;
        if (!restoreDraft)
        {
            _session.DiscardComposerDraft();
        }
        else if (_session.RestoreComposerDraft() is string restored)
        {
            // Восстановленный текст не должен снова открыть палитру.
            SetComposerText(restored);
        }

        _ = _composer!.SetFocus();
    }

    private void SetComposerText(string text)
    {
        _suppressCompletion = true;
        _composer!.Text = text;
        _composer.InsertionPoint = text.Length;
        _suppressCompletion = false;
    }

    /// <summary>
    /// Разрешает введённую команду с учётом режима доступа.
    /// </summary>
    /// <remarks>
    /// Отделено от обработчика ввода, чтобы правило доступа можно было проверить детерминированным
    /// тестом: Terminal.Gui 2.5.0 не поставляет тестовый драйвер, а <c>Run()</c> требует терминала.
    /// Техническая команда, введённая прямо в обычном режиме, не исполняется — она распознаётся и
    /// отклоняется, а не выглядит как неизвестная.
    /// </remarks>
    /// <param name="input">Введённая оператором строка.</param>
    /// <param name="mode">Текущий режим интерфейса.</param>
    /// <returns>Разрешение команды: исполнение, отказ по режиму либо отсутствие такой команды.</returns>
    internal static CommandResolution ResolveCommand(string input, OperatorUiMode mode)
    {
        ArgumentNullException.ThrowIfNull(input);
        OperatorUiCommand? command = OperatorUiCommand.Match(input);
        if (command is null)
        {
            return new CommandResolution(CommandResolutionKind.Unknown, null);
        }

        return command.IsAvailableIn(mode) && mode >= command.RequiredModeFor(input)
            ? new CommandResolution(CommandResolutionKind.Available, command)
            : new CommandResolution(CommandResolutionKind.NotAvailableInMode, command);
    }

    /// <summary>Точное имя команды, которое Enter должен передать на общую проверку доступа и маршрутизации.</summary>
    internal static bool IsDirectCommand(string input) => OperatorUiCommand.Match(input.Trim()) is not null;

    /// <summary>Запоминает черновик поля ввода и сообщает, что палитру нужно открыть.</summary>
    /// <remarks>
    /// Отделено от обработчика клавиш, чтобы привязку источника открытия к состоянию поля ввода можно было
    /// проверить детерминированным тестом: Terminal.Gui 2.5.0 не поставляет тестовый драйвер, а Run()
    /// требует реального терминала.
    /// </remarks>
    /// <param name="session">Сессия, владеющая сохранённым черновиком.</param>
    /// <param name="paletteMode">Текущее состояние палитры.</param>
    /// <param name="source">Источник открытия.</param>
    /// <param name="composerText">Текущий текст поля ввода.</param>
    /// <returns>Переход состояния.</returns>
    internal static ComposerKeyTransition OpenPalette(
        OperatorUiSession session,
        PaletteMode paletteMode,
        ComposerPaletteSource source,
        string composerText)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (paletteMode != PaletteMode.Hidden)
        {
            return new ComposerKeyTransition(ComposerKeyEffect.OpenPalette, null);
        }

        if (source == ComposerPaletteSource.FunctionKey)
        {
            // Первое F2 сохраняет исходный текст до подстановки триггера.
            session.DiscardComposerDraft();
            session.CaptureComposerDraft(composerText);
            return new ComposerKeyTransition(ComposerKeyEffect.OpenPalette, "/");
        }

        session.CaptureComposerDraft(composerText);
        return new ComposerKeyTransition(ComposerKeyEffect.OpenPalette, null);
    }

    /// <summary>Переводит клавишу в переход состояния поля ввода и палитры без обращения к представлению.</summary>
    /// <param name="key">Нажатая клавиша.</param>
    /// <param name="composerText">Текущий текст поля ввода.</param>
    /// <param name="paletteMode">Текущее состояние палитры.</param>
    /// <param name="detailsVisible">Открыты ли технические детали.</param>
    /// <param name="session">Сессия, владеющая сохранённым черновиком.</param>
    /// <returns>Переход состояния; <see cref="ComposerKeyEffect.NotHandled"/> для прочих клавиш.</returns>
    internal static ComposerKeyTransition ApplyComposerKey(
        Key key,
        string composerText,
        PaletteMode paletteMode,
        bool detailsVisible,
        OperatorUiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (key == Key.Esc)
        {
            if (paletteMode == PaletteMode.Hidden)
            {
                return detailsVisible
                    ? new ComposerKeyTransition(ComposerKeyEffect.ClearDetails, null)
                    : new ComposerKeyTransition(ComposerKeyEffect.ClosePalette, null);
            }

            // Esc закрывает ровно один слой: вложенная категория возвращает на первый уровень палитры,
            // а не выбрасывает оператора из меню и не теряет черновик.
            if (paletteMode == PaletteMode.Category)
            {
                return new ComposerKeyTransition(ComposerKeyEffect.BackToCommands, null);
            }

            session.CancelConfirmation();
            return new ComposerKeyTransition(ComposerKeyEffect.ClosePalette, session.RestoreComposerDraft());
        }

        if (key == Key.F2)
        {
            return OpenPalette(session, paletteMode, ComposerPaletteSource.FunctionKey, composerText);
        }

        if (key == Key.Tab)
        {
            return OpenPalette(session, paletteMode, ComposerPaletteSource.Completion, composerText);
        }

        return new ComposerKeyTransition(ComposerKeyEffect.NotHandled, null);
    }

    private void SubmitComposer()
    {
        string input = _composer!.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        _session.RememberCommand(input);
        _session.DiscardComposerDraft();
        AddMessage(OperatorEchoTitle, input);
        SetComposerText(string.Empty);
        CommandResolution resolution = ResolveCommand(input, _mode);
        if (resolution.Kind == CommandResolutionKind.Unknown)
        {
            AddMessage("Действие не найдено", "Введите / и выберите пункт меню. /help — доступные действия.");
            return;
        }

        // Режим — настоящее правило доступа: недоступное действие не исполняется даже при прямом вводе.
        if (resolution.Kind == CommandResolutionKind.NotAvailableInMode)
        {
            OperatorUiCommand denied = resolution.Command!;
            OperatorUiMode required = denied.RequiredModeFor(input);
            AddMessage("Действие недоступно в текущем режиме",
                $"«{denied.Title}» доступно в {OperatorUiModePresentation.PrepositionalLabel(required)} режиме. "
                + OperatorUiModePresentation.SwitchHint(required),
                OperatorUiMessageKind.Warning);
            return;
        }

        OperatorUiCommand command = resolution.Command!;
        if (!command.CanExecute(_session.IsOperationActive))
        {
            AddMessage("Операция выполняется", "Дождитесь завершения операции; F4 — запрос отмены.", OperatorUiMessageKind.Warning);
            return;
        }

        if (command.Action == OperatorUiAction.Cancel)
        {
            CancelOperation();
            return;
        }

        if (command.Action == OperatorUiAction.Exit)
        {
            RequestExit();
            return;
        }

        if (command.Action == OperatorUiAction.Details)
        {
            ShowDetails();
            return;
        }

        if (command.Action == OperatorUiAction.Settings)
        {
            ShowSettings();
            return;
        }

        if (command.Action == OperatorUiAction.Help)
        {
            ShowHelp();
            return;
        }

        if (command.Action == OperatorUiAction.SelectInstance)
        {
            if (_session.IsOperationActive)
            {
                AddMessage("Операция выполняется", "Выбор target доступен после завершения текущей операции.");
            }
            else if (_instances.Count == 0)
            {
                StartOperation(OperatorUiAction.Instances);
            }
            else
            {
                ShowInstances();
            }

            return;
        }

        // «Запустить окружение» не требует ручных /instances и /select: точная identity целевого
        // экземпляра разрешается по конфигурации до единого подтверждения опасного действия.
        if (command.RequiresConfirmation && _session.SelectedInstance is null)
        {
            _pendingConfirmation = command.Action;
            _awaitingTargetResolution = true;
            StartOperation(OperatorUiAction.ResolveTarget);
            return;
        }

        if (command.RequiresConfirmation)
        {
            ShowConfirmation(command);
        }
        else
        {
            StartOperation(command.Action);
        }
    }

    private void ShowHelp()
    {
        List<string> lines = [.. OperatorUiCommand.PrimaryFor(_mode)
            .Where(command => command.ShowInPalette)
            .Select(command => command.Command + " — " + command.Title)];
        IReadOnlyList<OperatorUiCategoryDefinition> categories = [.. OperatorUiCommand.CategoriesFor(_mode)];
        if (categories.Count != 0)
        {
            lines.Add(string.Empty);
            lines.Add("Дополнительные действия по категориям:");
            foreach (OperatorUiCategoryDefinition category in categories)
            {
                lines.Add("  " + category.Title + ": "
                    + string.Join(", ", OperatorUiCommand.InCategory(category.Category, _mode)
                        .Select(command => command.Command)));
            }
        }

        lines.Add(string.Empty);
        lines.Add("↑↓ — история с сохранением черновика. PgUp/PgDn — лента; Ctrl+End — конец.");
        lines.Add("F2 или / — палитра действий; Esc закрывает текущий слой.");
        lines.Add("F4 или Ctrl+C во время операции — запрос отмены; /exit отменяет и дожидается её.");
        if (_mode == OperatorUiMode.Diagnostic)
        {
            lines.Add("/details открывает технические детали результата или записи журнала; Esc возвращает назад.");
        }

        lines.Add("Поле ввода однострочное: многострочная вставка сохраняет первую строку.");
        AddMessage("Помощь", string.Join('\n', lines));
    }

    /// <summary>
    /// Предъявляет единое подтверждение изменения состояния над уже доказанной точной identity.
    /// </summary>
    /// <param name="command">Подтверждаемое действие.</param>
    private void ShowConfirmation(OperatorUiCommand command)
    {
        if (!_session.RequestConfirmation(command.Action))
        {
            AddMessage("Действие недоступно", _session.IsOperationActive
                ? "Дождитесь завершения текущей операции; F4 — запрос отмены."
                : "Сначала выберите конкретный экземпляр.",
                OperatorUiMessageKind.Warning);
            return;
        }

        _paletteMode = PaletteMode.Confirmation;
        _openCategory = null;
        _composer!.Enabled = false;
        _choices.Clear();
        // По умолчанию выделена отмена: подтверждение изменения состояния нельзя получить случайным Enter.
        _choices.Add(new PaletteChoice("Отмена"));
        MuMuInstanceId target = _session.PendingConfirmation!.Target;
        foreach ((string title, bool saveTarget) in MutationConfirmationChoices(
            command.Action, command.Title, target, ShouldSaveTarget(command.Action)))
        {
            _choices.Add(new PaletteChoice(title, command.Action, Confirm: true, SaveTarget: saveTarget));
        }

        _choiceIndex = 0;
        RenderPalette();
    }

    private bool ShouldSaveTarget(OperatorUiAction action) => _mode == OperatorUiMode.Normal
        && action is OperatorUiAction.EnvironmentPrepare or OperatorUiAction.GameStart
        && _session.SelectedInstance is MuMuInstanceId target
        && _controller.SavedConfiguration.MuMu.Instance != target.ToString();

    /// <summary>Строит подтверждение изменения состояния с отдельным выбором сохранения конфигурации.</summary>
    /// <param name="action">Подтверждаемое действие.</param>
    /// <param name="actionTitle">Название действия для оператора.</param>
    /// <param name="target">Точная identity, предъявленная для подтверждения.</param>
    /// <param name="includeSaveChoice">Нужно ли предлагать сохранение отличающегося target.</param>
    /// <returns>Варианты подтверждения; вариант без сохранения всегда первый.</returns>
    internal static IReadOnlyList<(string Title, bool SaveTarget)> MutationConfirmationChoices(
        OperatorUiAction action,
        string actionTitle,
        MuMuInstanceId target,
        bool includeSaveChoice)
    {
        string confirmation = action == OperatorUiAction.EnvironmentPrepare
            ? $"Подтвердить для {target}"
            : $"Подтвердить для {target}: {actionTitle}";
        return includeSaveChoice
            ? [(confirmation, false), (confirmation + " и сохранить выбор", true)]
            : [(confirmation, false)];
    }

    internal static bool ShouldRetainPendingConfirmation(
        string? targetFailureCode,
        bool inventoryReturned,
        int instanceCount)
        => targetFailureCode == ApplicationFailure.MuMuInstanceAmbiguous
            || inventoryReturned && instanceCount > 0;

    private void ShowInstances()
    {
        _paletteMode = PaletteMode.Instances;
        _openCategory = null;
        _composer!.Enabled = false;
        _session.CancelConfirmation();
        _choices.Clear();
        foreach (AzurPilotOperatorMuMuInstance instance in _instances)
        {
            string state = instance.Observation is null
                ? "не наблюдалось"
                : OperatorUiController.DescribeMuMuState(instance.Observation.State);
            _choices.Add(new PaletteChoice(OperatorUiSession.CleanText(
                $"{instance.Instance.Id} · {instance.Instance.DisplayName} · {state}", 256), Instance: instance.Instance.Id));
        }

        _choiceIndex = 0;
        RenderPalette();
    }

    /// <summary>Показывает действующую эффективную конфигурацию без второго источника истины.</summary>
    private void ShowSettings()
    {
        OperatorUiMessage settings = _controller.DescribeSettings(_mode);
        _settingsMessage = settings;
        _detailsMessage = null;
        _detailsLogEntry = null;
        _followMessages = true;
        _messagesDirty = true;
        _paletteMode = PaletteMode.Settings;
        _composer!.Enabled = false;
        _choices.Clear();
        _choices.Add(new PaletteChoice("Выбрать и сохранить точный экземпляр MuMu", Setting: SettingAction.Target));
        _choices.Add(new PaletteChoice("Сохранить YAML / перенести устаревший JSON с резервной копией", Setting: SettingAction.Save));
        _choices.Add(new PaletteChoice("Режим следующего запуска: normal", Setting: SettingAction.Normal));
        _choices.Add(new PaletteChoice("Режим следующего запуска: manual", Setting: SettingAction.Manual));
        _choices.Add(new PaletteChoice("Режим следующего запуска: diagnostic", Setting: SettingAction.Diagnostic));
        _choices.Add(new PaletteChoice(_controller.SavedConfiguration.Logging.Viewer.Enabled
            ? "Отключить отдельную лог-консоль" : "Включить отдельную лог-консоль", Setting: SettingAction.Viewer));
        _choices.Add(new PaletteChoice("Открыть отдельную лог-консоль снова", Setting: SettingAction.ReopenViewer));
        _choiceIndex = 0;
        RenderPalette();
    }

    private void ApplySetting(SettingAction? setting)
    {
        if (_session.IsOperationActive)
        {
            AddMessage("Настройки", "Дождитесь завершения текущей операции.", OperatorUiMessageKind.Warning);
            ClosePalette();
            return;
        }

        ClosePalette();
        _settingsMessage = null;
        if (setting == SettingAction.Target)
        {
            _saveChosenTarget = true;
            StartOperation(OperatorUiAction.Instances);
            return;
        }

        if (setting == SettingAction.ReopenViewer)
        {
            StartOperation(OperatorUiAction.ReopenLogViewer);
            return;
        }

        AzurPilotConfiguration configuration = _controller.SavedConfiguration;
        configuration = setting switch
        {
            SettingAction.Normal => configuration with { Ui = new UiConfiguration { Mode = OperatorUiMode.Normal } },
            SettingAction.Manual => configuration with { Ui = new UiConfiguration { Mode = OperatorUiMode.Manual } },
            SettingAction.Diagnostic => configuration with { Ui = new UiConfiguration { Mode = OperatorUiMode.Diagnostic } },
            SettingAction.Viewer => configuration with
            {
                Logging = new LoggingConfiguration
                {
                    Viewer = new LogViewerConfiguration { Enabled = !configuration.Logging.Viewer.Enabled },
                },
            },
            SettingAction.Save or SettingAction.Target or SettingAction.ReopenViewer or null => configuration,
            _ => configuration,
        };
        _ = _controller.SaveSettings(configuration);
        _messagesDirty = true;
    }

    private void ShowDetails()
    {
        OperatorUiMessage[] messages = [.. _session.SnapshotMessages()
            .Where(message => !string.IsNullOrWhiteSpace(message.Details))
            .TakeLast(128)
            .Reverse()];
        AzurPilotOperatorLogEntry[] logEntries = [.. _logSource.ReadSnapshot().TakeLast(128).Reverse()];
        if (messages.Length == 0 && logEntries.Length == 0)
        {
            AddMessage("Детали недоступны", "Нет недавних результатов или записей журнала для просмотра.");
            return;
        }

        _detailsMessage = null;
        _detailsLogEntry = null;
        _settingsMessage = null;
        _paletteMode = PaletteMode.Details;
        _openCategory = null;
        _composer!.Enabled = false;
        _session.CancelConfirmation();
        _choices.Clear();
        foreach (OperatorUiMessage message in messages)
        {
            string summary = string.Join(' ', message.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            _choices.Add(new PaletteChoice(
                OperatorUiSession.CleanText($"{KindLabel(message.Kind)} · {message.Title} — {summary}", 256),
                Message: message));
        }

        foreach (AzurPilotOperatorLogEntry entry in logEntries)
        {
            _choices.Add(new PaletteChoice(
                OperatorUiSession.CleanText(
                    $"ЖУРНАЛ · {OperatorUiLogPresentation.FormatSummaryHeader(entry)} — {OperatorUiLogPresentation.SummaryMessage(entry)}",
                    256),
                LogEntry: entry));
        }

        _choiceIndex = 0;
        RenderPalette();
    }

    private void StartOperation(OperatorUiAction action)
    {
        _ = _controller.TryStartOperation(action);
        _messagesDirty = true;
    }

    private void CompleteOperation()
    {
        bool inventoryChanged = _controller.CompleteOperation();
        _messagesDirty = true;
        if (_awaitingTargetResolution)
        {
            _awaitingTargetResolution = false;
            if (_session.SelectedInstance is MuMuInstanceId)
            {
                // Identity доказана продуктовым резолвером: предъявляем её в едином подтверждении.
                OperatorUiAction action = _pendingConfirmation ?? OperatorUiAction.EnvironmentPrepare;
                _pendingConfirmation = null;
                ShowConfirmation(OperatorUiCommand.For(action));
                return;
            }

            if (ShouldRetainPendingConfirmation(
                _controller.LastOperationFailure?.Code,
                inventoryReturned: false,
                instanceCount: 0))
            {
                // Неоднозначный target: выбор точного ID — часть того же пользовательского действия,
                // а старт остаётся запрещённым до однозначного выбора.
                StartOperation(OperatorUiAction.Instances);
            }
            else
            {
                // Остальной отказ завершает исходное действие: старое подтверждение не переносится
                // на следующий пользовательский выбор экземпляра.
                _pendingConfirmation = null;
            }

            return;
        }

        if (inventoryChanged)
        {
            _instances = _controller.Inventory!.Instances;
        }

        if (_pendingConfirmation is not null
            && !ShouldRetainPendingConfirmation(
                targetFailureCode: null,
                inventoryReturned: inventoryChanged,
                instanceCount: _instances.Count))
        {
            _pendingConfirmation = null;
        }

        if (inventoryChanged)
        {
            if (_instances.Count != 0 && !_exitRequested
                && (_mode != OperatorUiMode.Normal || _pendingConfirmation is not null || _saveChosenTarget))
            {
                ShowInstances();
            }
        }
    }

    private void CancelOperation()
    {
        _controller.CancelOperation();
        _messagesDirty = true;
    }

    private void RequestExit()
    {
        _exitRequested = true;
        ClosePalette();
        if (_session.IsOperationActive)
        {
            CancelOperation();
        }
        else
        {
            _application!.RequestStop();
        }
    }

    private void AddMessage(
        string title,
        string text,
        OperatorUiMessageKind kind = OperatorUiMessageKind.Information,
        string? details = null)
    {
        _session.AddMessage(title, text, kind, details);
        _messagesDirty = true;
    }

    private void RenderMessages()
    {
        if (!_historyView!.Visible || _historyView.Viewport.Width <= 0)
        {
            return;
        }

        List<string> lines = [];
        Dictionary<int, Scheme> schemes = [];
        if (_settingsMessage is OperatorUiMessage settings)
        {
            lines.Add("\u2500\u2500 Настройки \u00b7 Esc возвращает к истории \u2500\u2500");
            schemes[0] = _mutedScheme;
            schemes[lines.Count] = MessageScheme(settings.Kind);
            lines.Add(settings.Title);
            lines.AddRange(Wrap(settings.Text, _historyView.Viewport.Width));
            if (!string.IsNullOrWhiteSpace(settings.Details))
            {
                lines.Add(string.Empty);
                lines.Add("Действующая конфигурация");
                lines.AddRange(Wrap(settings.Details, _historyView.Viewport.Width));
            }
        }
        else if (_detailsMessage is OperatorUiMessage selected)
        {
            lines.Add("\u2500\u2500 Технические детали \u00b7 Esc возвращает к истории \u2500\u2500");
            schemes[0] = _mutedScheme;
            schemes[lines.Count] = MessageScheme(selected.Kind);
            lines.Add(selected.Title);
            lines.AddRange(Wrap(selected.Text, _historyView.Viewport.Width));
            lines.Add(string.Empty);
            lines.Add("Технические данные");
            lines.AddRange(Wrap(selected.Details ?? "Для этого сообщения детали отсутствуют.", _historyView.Viewport.Width));
        }
        else if (_detailsLogEntry is AzurPilotOperatorLogEntry logEntry)
        {
            lines.Add("\u2500\u2500 Технические детали журнала \u00b7 Esc возвращает к истории \u2500\u2500");
            schemes[0] = _mutedScheme;
            schemes[lines.Count] = LogLevelScheme(logEntry.Level);
            lines.Add($"{OperatorUiLogPresentation.LevelLabel(logEntry.Level)} \u00b7 {OperatorUiLogPresentation.FormatSummaryHeader(logEntry)}");
            lines.Add(string.Empty);
            lines.AddRange(OperatorUiLogPresentation.FormatTechnicalDetails(logEntry)
                .SelectMany(line => Wrap(line, _historyView.Viewport.Width)));
        }
        else
        {
            schemes[0] = _mutedScheme;
            foreach (OperatorUiMessage message in _session.SnapshotMessages())
            {
                int row = lines.Count;
                if (string.Equals(message.Title, OperatorEchoTitle, StringComparison.Ordinal))
                {
                    // Введённая команда — одна компактная строка вместо пары «заголовок + блок».
                    lines.Add("> " + message.Text);
                    schemes[row] = _mutedScheme;
                }
                else
                {
                    // Смысл результата несёт сам заголовок; дублирующая метка статуса не добавляется.
                    lines.Add(message.Title);
                    lines.AddRange(Wrap(message.Text, _historyView.Viewport.Width));
                    schemes[row] = MessageScheme(message.Kind);
                }

                lines.Add(string.Empty);
            }
        }

        _messageWrapWidth = _historyView.Viewport.Width;
        _messageRows = RenderLines(_historyView, lines, _followMessages, schemes);
        _messagesDirty = false;
    }

    private static int RenderLines(
        View pane,
        List<string> lines,
        bool follow,
        Dictionary<int, Scheme>? schemes = null)
    {
        int oldY = pane.Viewport.Y;
        RemoveChildren(pane);
        int row = 0;
        int firstSourceRow = Math.Max(0, lines.Count - MaximumRenderedRows);
        for (int sourceRow = firstSourceRow; sourceRow < lines.Count; sourceRow++)
        {
            Label label = AddLine(pane, row++, lines[sourceRow]);
            if (schemes?.TryGetValue(sourceRow, out Scheme? scheme) == true)
            {
                _ = label.SetScheme(scheme);
            }
        }

        pane.SetContentHeight(row);
        Scroll(pane, row, follow ? int.MaxValue : oldY);
        return row;
    }

    private static void RemoveChildren(View pane)
    {
        foreach (View child in pane.SubViews.ToArray())
        {
            _ = pane.Remove(child);
            child.Dispose();
        }
    }

    private static Label AddLine(View pane, int row, string text)
    {
        // Ширина по тексту: строчная Label не растягивается на всю панель и не может закрасить строку фоном.
        Label label = new() { X = 0, Y = row, Width = Dim.Auto(), Height = 1, Text = text, HotKeySpecifier = new Rune(0xffff) };
        _ = pane.Add(label);
        return label;
    }

    private Scheme MessageScheme(OperatorUiMessageKind kind) => kind switch
    {
        OperatorUiMessageKind.Success => _successScheme,
        OperatorUiMessageKind.Warning => _warningScheme,
        OperatorUiMessageKind.Error => _errorScheme,
        OperatorUiMessageKind.Information => _primaryScheme,
        _ => _primaryScheme,
    };

    private Scheme LogLevelScheme(LogLevel level) => level switch
    {
        LogLevel.Warning => _warningScheme,
        LogLevel.Error or LogLevel.Critical => _errorScheme,
        LogLevel.Trace or LogLevel.Debug or LogLevel.Information or LogLevel.None => _mutedScheme,
        _ => _mutedScheme,
    };

    private static string KindLabel(OperatorUiMessageKind kind) => kind switch
    {
        OperatorUiMessageKind.Success => "Готово",
        OperatorUiMessageKind.Warning => "Внимание",
        OperatorUiMessageKind.Error => "Отказ",
        OperatorUiMessageKind.Information => "Инфо",
        _ => "Инфо",
    };

    internal static IEnumerable<string> Wrap(string text, int columns)
    {
        columns = Math.Max(1, columns);
        foreach (string sourceLine in text.Split('\n'))
        {
            if (sourceLine.StartsWith(OperatorUiController.ConfigurationPathLabel, StringComparison.Ordinal))
            {
                // У пути значимы все пробелы, в том числе на границе переноса.
                StringBuilder pathRow = new();
                int pathWidth = 0;
                foreach (Rune rune in sourceLine.EnumerateRunes())
                {
                    int runeWidth = Math.Max(0, rune.GetColumns());
                    if (pathWidth + runeWidth > columns && pathRow.Length > 0)
                    {
                        yield return pathRow.ToString();
                        _ = pathRow.Clear();
                        pathWidth = 0;
                    }

                    _ = pathRow.Append(rune.ToString());
                    pathWidth += runeWidth;
                }

                yield return pathRow.ToString();
                continue;
            }

            StringBuilder row = new();
            int width = 0;
            string[] words = sourceLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in words)
            {
                int wordWidth = word.EnumerateRunes().Sum(static rune => Math.Max(0, rune.GetColumns()));
                if (width > 0 && width + 1 + wordWidth > columns)
                {
                    yield return row.ToString();
                    _ = row.Clear();
                    width = 0;
                }
                else if (width > 0)
                {
                    _ = row.Append(' ');
                    width++;
                }

                foreach (Rune rune in word.EnumerateRunes())
                {
                    int cellWidth = Math.Max(0, rune.GetColumns());
                    if (width + cellWidth > columns && row.Length != 0)
                    {
                        yield return row.ToString();
                        _ = row.Clear();
                        width = 0;
                    }

                    _ = row.Append(rune.ToString());
                    width += cellWidth;
                }
            }

            yield return row.ToString();
        }
    }

    private void ScrollMessages(Key key)
    {
        _followMessages = key == Key.End.WithCtrl;
        int change = key == Key.PageUp ? -_historyView!.Viewport.Height : _historyView!.Viewport.Height;
        Scroll(_historyView, _messageRows, _followMessages ? int.MaxValue : _historyView.Viewport.Y + change);
    }

    private void HandleMouse(Mouse mouse)
    {
        if (!OperatorUiScrollMath.TryGetWheelDelta(mouse.Flags, out int delta))
        {
            return;
        }

        View? pane = FindScrollablePane(mouse.View, _historyView);
        if (pane is null)
        {
            return;
        }

        Scroll(pane, _messageRows, pane.Viewport.Y + delta);
        _followMessages = pane.Viewport.Y >= Math.Max(0, _messageRows - pane.Viewport.Height);
        mouse.Handled = true;
    }

    /// <summary>Находит видимую прокручиваемую панель под указателем.</summary>
    /// <param name="view">View, получивший mouse event.</param>
    /// <param name="historyView">Панель истории.</param>
    /// <returns>Панель истории, если указатель внутри неё и она видима.</returns>
    internal static View? FindScrollablePane(View? view, View? historyView)
    {
        if (historyView is { Visible: true } history && IsWithin(view, history))
        {
            return history;
        }

        return null;
    }

    private static bool IsWithin(View? view, View pane)
    {
        while (view is not null)
        {
            if (ReferenceEquals(view, pane))
            {
                return true;
            }

            view = view.SuperView;
        }

        return false;
    }

    private static void Scroll(View pane, int rows, int y)
    {
        Rectangle viewport = pane.Viewport;
        pane.Viewport = new Rectangle(0, OperatorUiScrollMath.ClampViewportY(y, rows, viewport.Height), viewport.Width, viewport.Height);
    }

    private sealed record PaletteChoice(
        string Title,
        OperatorUiAction? Action = null,
        MuMuInstanceId? Instance = null,
        bool Confirm = false,
        OperatorUiMessage? Message = null,
        AzurPilotOperatorLogEntry? LogEntry = null,
        OperatorUiCategory? Category = null,
        SettingAction? Setting = null,
        bool SaveTarget = false);

    private enum SettingAction { Target, Save, Normal, Manual, Diagnostic, Viewer, ReopenViewer }

    /// <summary>Результат разрешения введённой команды.</summary>
    internal enum CommandResolutionKind
    {
        /// <summary>Такой команды в каталоге нет.</summary>
        Unknown,
        /// <summary>Команда существует, но недоступна в текущем режиме.</summary>
        NotAvailableInMode,
        /// <summary>Команда доступна и может исполняться.</summary>
        Available,
    }

    /// <summary>Разрешение введённой команды и её описание.</summary>
    /// <param name="Kind">Итог разрешения.</param>
    /// <param name="Command">Найденная команда либо <see langword="null"/>.</param>
    internal readonly record struct CommandResolution(CommandResolutionKind Kind, OperatorUiCommand? Command);

    /// <summary>Состояние палитры действий.</summary>
    internal enum PaletteMode
    {
        Hidden,
        Commands,
        Category,
        Instances,
        Details,
        Confirmation,
        Settings,
    }

    /// <summary>Источник открытия палитры действий.</summary>
    internal enum ComposerPaletteSource
    {
        /// <summary>Клавиша F2.</summary>
        FunctionKey,
        /// <summary>Ввод триггера `/`.</summary>
        Trigger,
        /// <summary>Клавиша Tab.</summary>
        Completion,
    }

    /// <summary>Действие, которое представление применяет к палитре и техническим деталям.</summary>
    internal enum ComposerKeyEffect
    {
        /// <summary>Клавиша не относится к полю ввода и палитре.</summary>
        NotHandled,
        /// <summary>Открыть палитру действий.</summary>
        OpenPalette,
        /// <summary>Закрыть палитру.</summary>
        ClosePalette,
        /// <summary>Вернуться с вложенного уровня палитры на первый.</summary>
        BackToCommands,
        /// <summary>Закрыть технические детали.</summary>
        ClearDetails,
    }

    /// <summary>Переход состояния поля ввода и палитры.</summary>
    /// <param name="Effect">Что применить к представлению.</param>
    /// <param name="ComposerText">Текст поля ввода после перехода либо отсутствие изменения.</param>
    internal readonly record struct ComposerKeyTransition(ComposerKeyEffect Effect, string? ComposerText);
}
