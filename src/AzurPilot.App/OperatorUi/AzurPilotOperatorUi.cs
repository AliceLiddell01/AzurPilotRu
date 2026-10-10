using System.Drawing;
using System.Text;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.MuMu;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AzurPilot.App.OperatorUi;

/// <summary>Интерактивная операторская поверхность поверх существующих production use cases.</summary>
/// <remarks>
/// UI владеет только presentation-состоянием. Task исполняется вне UI thread, а все изменения views
/// применяются одним timer callback. Выход отменяет и дожидается текущей операции до disposal терминала.
/// </remarks>
public sealed class AzurPilotOperatorUi : IDisposable
{
    private const int MinimumWidth = 60;
    private const int MinimumHeight = 18;
    private const int MaximumRenderedRows = 2048;
    private readonly OperatorUiController _controller;
    private readonly IOperatorUiLogSource _logSource;
    private readonly OperatorUiSession _session;
    private readonly List<PaletteChoice> _choices = [];
    private IApplication? _application;
    private Window? _window;
    private View? _messagesView;
    private View? _logsView;
    private View? _paletteView;
    private TextField? _composer;
    private Label? _header;
    private Label? _operation;
    private Label? _footer;
    private Label? _sizeNotice;
    private IReadOnlyList<AzurPilotOperatorMuMuInstance> _instances = [];
    private string[] _lastLogs = [];
    private PaletteMode _paletteMode;
    private int _choiceIndex;
    private int _messageRows;
    private int _logRows;
    private int _messageWrapWidth;
    private int _logWrapWidth;
    private Size _lastSize;
    private bool _messagesDirty = true;
    private bool _showLogs = true;
    private bool _followMessages = true;
    private bool _followLogs = true;
    private bool _exitRequested;
    private bool _suppressCompletion;
    private bool _hasRun;

    /// <summary>Использует уже собранный startup snapshot и общий источник логов application host.</summary>
    /// <param name="service">Владелец production use cases.</param>
    /// <param name="initialDiagnostics">Диагностика текущего startup без повторной инициализации.</param>
    /// <param name="logSource">Потокобезопасный bounded источник логов.</param>
    public AzurPilotOperatorUi(
        AzurPilotOperatorService service,
        AzurPilotDiagnosticReport initialDiagnostics,
        IOperatorUiLogSource logSource)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(initialDiagnostics);
        ArgumentNullException.ThrowIfNull(logSource);
        _controller = new OperatorUiController(service, initialDiagnostics);
        _session = _controller.Session;
        _logSource = logSource;
    }

    /// <summary>Запускает одну интерактивную сессию на вызывающем thread.</summary>
    /// <param name="cancellationToken">Запрос graceful выхода от application host.</param>
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
        using IApplication application = Application.Create();
        _application = application;
        _ = application.Init();
        using Window window = new() { Title = "AzurPilot — оператор", Width = Dim.Fill(), Height = Dim.Fill() };
        _window = window;
        _header = new Label { Height = 1 };
        _operation = new Label { Height = 1 };
        _messagesView = new View { CanFocus = false };
        _logsView = new View { CanFocus = false };
        _composer = new TextField { Height = 1 };
        _footer = new Label { Height = 1 };
        _paletteView = new View { CanFocus = false, Visible = false };
        _sizeNotice = new Label { Visible = false };
        window.Add(_header, _operation, _messagesView, _logsView, _composer, _footer, _paletteView, _sizeNotice);
        _composer.TextChanged += (_, _) => UpdateCommandPalette();
        application.Keyboard.KeyDown += (_, key) => HandleKey(key);
        window.Initialized += (_, _) => { RefreshLayout(); _ = _composer.SetFocus(); };
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

    /// <summary>Освобождает coordinator после graceful завершения операции.</summary>
    public void Dispose()
    {
        _controller.Dispose();
        GC.SuppressFinalize(this);
    }
    private bool Tick(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested && !_exitRequested)
        {
            RequestExit();
        }

        if (_controller.IsOperationCompleted)
        {
            CompleteOperation();
        }

        if (_exitRequested && !_session.IsOperationActive)
        {
            _application!.RequestStop();
            return false;
        }

        RefreshLayout();
        RefreshLogs();
        _messagesDirty |= _messageWrapWidth != _messagesView!.Viewport.Width;
        if (_messagesDirty)
        {
            RenderMessages();
        }

        _header!.Text = $"Экземпляр: {_session.SelectedInstance?.ToString() ?? "не выбран"}  |  / — действия";
        _operation!.Text = _controller.OperationText;
        return true;
    }

    private void RefreshLayout()
    {
        Size size = _window!.Viewport.Size;
        if (size == _lastSize)
        {
            return;
        }

        _lastSize = size;
        bool small = size.Width < MinimumWidth - 2 || size.Height < MinimumHeight - 2;
        _sizeNotice!.Visible = small;
        _sizeNotice.Text = $"Увеличьте терминал минимум до {MinimumWidth}×{MinimumHeight}. Ctrl+C — выход.";
        _sizeNotice.Width = Math.Max(1, size.Width);
        _sizeNotice.Height = Math.Max(1, size.Height);
        foreach (View view in new View[] { _header!, _operation!, _messagesView!, _composer!, _footer! })
        {
            view.Visible = !small;
        }

        _logsView!.Visible = !small && _showLogs;
        _paletteView!.Visible = !small && _paletteMode != PaletteMode.Hidden;
        if (small)
        {
            return;
        }

        _header!.X = 0; _header.Y = 0; _header.Width = size.Width;
        _operation!.X = 0; _operation.Y = 1; _operation.Width = size.Width;
        _composer!.X = 0; _composer.Y = size.Height - 2; _composer.Width = size.Width;
        _footer!.X = 0; _footer.Y = size.Height - 1; _footer.Width = size.Width;
        _footer.Text = "↑↓ история · PgUp/PgDn лента · Ctrl+End конец · F3 логи · F4 отмена · Ctrl+C выход";
        int contentHeight = size.Height - 4;
        bool sideLogs = _showLogs && size.Width >= 100;
        int logHeight = _showLogs && !sideLogs ? Math.Max(4, contentHeight / 3) : 0;
        int messageWidth = sideLogs ? size.Width - 34 : size.Width;
        _messagesView!.X = 0; _messagesView.Y = 2;
        _messagesView.Width = messageWidth; _messagesView.Height = contentHeight - logHeight;
        _logsView.X = sideLogs ? messageWidth + 1 : 0;
        _logsView.Y = sideLogs ? 2 : 2 + contentHeight - logHeight;
        _logsView.Width = sideLogs ? 33 : size.Width;
        _logsView.Height = sideLogs ? contentHeight : Math.Max(1, logHeight);
        _lastLogs = [];
        _messagesDirty = true;
        RenderPalette();
    }

    private void HandleKey(Key key)
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

        if (key == Key.Esc)
        {
            ClosePalette(); key.Handled = true; return;
        }

        if (key == Key.F2)
        {
            ClosePalette(); _composer!.Text = "/"; OpenCommands(); key.Handled = true; return;
        }

        if (key == Key.F3)
        {
            ToggleLogs(); key.Handled = true; return;
        }

        if (key == Key.PageUp || key == Key.PageDown || key == Key.End.WithCtrl)
        {
            ScrollMessages(key); key.Handled = true; return;
        }

        if (key == Key.PageUp.WithAlt || key == Key.PageDown.WithAlt || key == Key.End.WithAlt)
        {
            ScrollLogs(key); key.Handled = true; return;
        }

        if (_paletteMode != PaletteMode.Hidden)
        {
            if (key == Key.CursorUp || key == Key.CursorDown)
            {
                _choiceIndex = Math.Clamp(_choiceIndex + (key == Key.CursorUp ? -1 : 1), 0,
                    Math.Max(0, _choices.Count - 1));
                RenderPalette(); key.Handled = true; return;
            }

            if (key == Key.Tab || key == Key.Enter)
            {
                AcceptChoice(key == Key.Tab); key.Handled = true; return;
            }

            // В подтверждении текст и paste не должны неявно выбирать положительный ответ.
            if (_paletteMode is PaletteMode.Confirmation or PaletteMode.Instances)
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
            OpenCommands(); key.Handled = true;
        }
    }

    private void UpdateCommandPalette()
    {
        if (_suppressCompletion || _paletteMode is PaletteMode.Confirmation or PaletteMode.Instances)
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
            OpenCommands();
        }
        else if (_paletteMode == PaletteMode.Commands)
        {
            ClosePalette();
        }
    }

    private void OpenCommands()
    {
        _session.CancelConfirmation();
        _composer!.Enabled = true;
        _paletteMode = PaletteMode.Commands;
        _choices.Clear();
        string filter = _composer!.Text.Trim();
        foreach (OperatorUiCommand command in OperatorUiCommand.All)
        {
            if (filter.Length <= 1 || command.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || command.Title.Contains(filter.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            {
                string suffix = command.RequiresConfirmation ? " [подтверждение]" : string.Empty;
                _choices.Add(new PaletteChoice(command.Command + " — " + command.Title + suffix, command.Action));
            }
        }

        _choiceIndex = 0;
        RenderPalette();
    }

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
        int rows = Math.Min(Math.Max(3, _lastSize.Height - 5), _choices.Count + 2);
        rows = Math.Min(rows, 10);
        _paletteView.X = 0; _paletteView.Y = Math.Max(2, _lastSize.Height - 2 - rows);
        _paletteView.Width = Math.Max(1, _lastSize.Width); _paletteView.Height = rows;
        string title = _paletteMode switch
        {
            PaletteMode.Hidden => string.Empty,
            PaletteMode.Commands => "Действия · ↑↓ выбор · Tab дополнить · Enter выполнить · Esc закрыть",
            PaletteMode.Instances => "Выберите exact MuMu ID · ↑↓ Enter · Esc отмена",
            PaletteMode.Confirmation => "Подтверждение изменения состояния · по умолчанию отмена",
            _ => string.Empty,
        };
        AddLine(_paletteView, 0, title);
        int first = Math.Max(0, _choiceIndex - (rows - 3));
        for (int index = first; index < Math.Min(_choices.Count, first + rows - 1); index++)
        {
            AddLine(_paletteView, index - first + 1, (index == _choiceIndex ? "> " : "  ") + _choices[index].Title);
        }

        if (_choices.Count == 0)
        {
            AddLine(_paletteView, 1, "Нет подходящих команд. Esc — закрыть.");
        }
    }

    private void AcceptChoice(bool completeOnly)
    {
        if (_choices.Count == 0)
        {
            return;
        }

        PaletteChoice choice = _choices[_choiceIndex];
        if (_paletteMode == PaletteMode.Confirmation)
        {
            if (!completeOnly && choice.Confirm && _session.ConfirmMutation())
            {
                OperatorUiAction action = choice.Action!.Value;
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
            if (choice.Instance is MuMuInstanceId identity && _session.SelectInstance(identity))
            {
                AddMessage("Выбор экземпляра", identity.ToString());
            }

            ClosePalette();
            return;
        }

        OperatorUiCommand command = OperatorUiCommand.For(choice.Action!.Value);
        _suppressCompletion = true;
        _composer!.Text = command.Command;
        _composer.InsertionPoint = command.Command.Length;
        _suppressCompletion = false;
        HidePalette();
        if (!completeOnly)
        {
            SubmitComposer();
        }
    }

    private void HidePalette()
    {
        _paletteMode = PaletteMode.Hidden;
        _choices.Clear();
        _paletteView!.Visible = false;
        _composer!.Enabled = true;
    }

    private void ClosePalette()
    {
        _session.CancelConfirmation();
        HidePalette();
        _ = _composer!.SetFocus();
    }

    private void SubmitComposer()
    {
        string input = _composer!.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        _session.RememberCommand(input);
        AddMessage("Оператор", input);
        _suppressCompletion = true; _composer.Text = string.Empty; _suppressCompletion = false;
        OperatorUiCommand? command = OperatorUiCommand.All.FirstOrDefault(item =>
            string.Equals(item.Command, input, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            AddMessage("Команда не найдена", "Введите / и выберите действие из меню. /help — помощь.");
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

        if (command.Action == OperatorUiAction.Logs)
        {
            ToggleLogs();
            return;
        }

        if (command.Action == OperatorUiAction.Help)
        {
            AddMessage("Помощь", string.Join('\n', OperatorUiCommand.All.Select(item => item.Command + " — " + item.Title))
                + "\n↑↓ — история с сохранением draft. PgUp/PgDn — лента; Ctrl+End — конец."
                + "\nAlt+PgUp/PgDn — логи; Alt+End — конец логов."
                + "\nCtrl+C отменяет действующую операцию; /exit отменяет и дожидается её завершения."
                + "\nComposer однострочный: многострочная вставка сохраняет первую строку.");
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

        if (command.RequiresConfirmation)
        {
            ShowConfirmation(command);
        }
        else
        {
            StartOperation(command.Action);
        }
    }

    private void ShowConfirmation(OperatorUiCommand command)
    {
        if (!_session.RequestConfirmation(command.Action))
        {
            AddMessage("Действие недоступно", _session.IsOperationActive
                ? "Дождитесь завершения текущей операции; /cancel — запрос отмены."
                : "Сначала выберите конкретный экземпляр через /instances.");
            return;
        }

        _paletteMode = PaletteMode.Confirmation;
        _composer!.Enabled = false;
        _choices.Clear();
        _choices.Add(new PaletteChoice("Отмена"));
        _choices.Add(new PaletteChoice($"Подтвердить для {_session.PendingConfirmation!.Target}: {command.Title}",
            command.Action, Confirm: true));
        _choiceIndex = 0;
        RenderPalette();
    }

    private void ShowInstances()
    {
        _paletteMode = PaletteMode.Instances;
        _composer!.Enabled = false;
        _session.CancelConfirmation();
        _choices.Clear();
        foreach (AzurPilotOperatorMuMuInstance instance in _instances)
        {
            string state = instance.Observation?.State.ToString() ?? "не наблюдалось";
            _choices.Add(new PaletteChoice(OperatorUiSession.CleanText(
                $"{instance.Instance.Id} · {instance.Instance.DisplayName} · {state}", 256), Instance: instance.Instance.Id));
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
        if (inventoryChanged)
        {
            _instances = _controller.Inventory!.Instances;
            if (_instances.Count != 0 && !_exitRequested)
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

    private void ToggleLogs()
    {
        _showLogs = !_showLogs;
        _lastSize = Size.Empty;
        RefreshLayout();
    }

    private void AddMessage(string title, string text)
    {
        _session.AddMessage(title, text);
        _messagesDirty = true;
    }

    private void RenderMessages()
    {
        if (!_messagesView!.Visible || _messagesView.Viewport.Width <= 0)
        {
            return;
        }

        List<string> lines = ["── Сообщения и результаты ──"];
        foreach (OperatorUiMessage message in _session.SnapshotMessages())
        {
            lines.Add("[" + message.Title + "]");
            lines.AddRange(Wrap(message.Text, _messagesView.Viewport.Width));
            lines.Add(string.Empty);
        }

        _messageWrapWidth = _messagesView.Viewport.Width;
        _messageRows = RenderLines(_messagesView, lines, _followMessages);
        _messagesDirty = false;
    }

    private void RefreshLogs()
    {
        if (!_logsView!.Visible || _logsView.Viewport.Width <= 0)
        {
            return;
        }

        string[] snapshot = [.. _logSource.ReadSnapshot().TakeLast(128)
            .Select(line => OperatorUiSession.CleanText(line, 2048))];
        if (snapshot.SequenceEqual(_lastLogs, StringComparer.Ordinal) && _logWrapWidth == _logsView.Viewport.Width)
        {
            return;
        }

        _lastLogs = snapshot;
        _logWrapWidth = _logsView.Viewport.Width;
        List<string> lines = ["── Логи · Alt+PgUp/PgDn ──"];
        foreach (string line in snapshot)
        {
            lines.AddRange(Wrap(line, _logsView.Viewport.Width));
        }

        _logRows = RenderLines(_logsView, lines, _followLogs);
    }

    private static int RenderLines(View pane, List<string> lines, bool follow)
    {
        int oldY = pane.Viewport.Y;
        RemoveChildren(pane);
        int row = 0;
        foreach (string line in lines.TakeLast(MaximumRenderedRows))
        {
            AddLine(pane, row++, line);
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

    private static void AddLine(View pane, int row, string text)
        => pane.Add(new Label { X = 0, Y = row, Width = Dim.Fill(), Height = 1, Text = text, HotKeySpecifier = new Rune(0xffff) });

    private static IEnumerable<string> Wrap(string text, int columns)
    {
        StringBuilder row = new();
        int width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                yield return row.ToString(); _ = row.Clear(); width = 0; continue;
            }

            int cellWidth = Math.Max(0, rune.GetColumns());
            if (width + cellWidth > columns && row.Length != 0)
            {
                yield return row.ToString(); _ = row.Clear(); width = 0;
            }

            _ = row.Append(rune.ToString()); width += cellWidth;
        }

        yield return row.ToString();
    }

    private void ScrollMessages(Key key)
    {
        _followMessages = key == Key.End.WithCtrl;
        int change = key == Key.PageUp ? -_messagesView!.Viewport.Height : _messagesView!.Viewport.Height;
        Scroll(_messagesView, _messageRows, _followMessages ? int.MaxValue : _messagesView.Viewport.Y + change);
    }

    private void ScrollLogs(Key key)
    {
        _followLogs = key == Key.End.WithAlt;
        int change = key == Key.PageUp.WithAlt ? -_logsView!.Viewport.Height : _logsView!.Viewport.Height;
        Scroll(_logsView, _logRows, _followLogs ? int.MaxValue : _logsView.Viewport.Y + change);
    }

    private static void Scroll(View pane, int rows, int y)
    {
        Rectangle viewport = pane.Viewport;
        pane.Viewport = new Rectangle(0, Math.Clamp(y, 0, Math.Max(0, rows - viewport.Height)), viewport.Width, viewport.Height);
    }

    private sealed record PaletteChoice(string Title, OperatorUiAction? Action = null, MuMuInstanceId? Instance = null,
        bool Confirm = false);
    private enum PaletteMode
    {
        Hidden,
        Commands,
        Instances,
        Confirmation,
    }
}
