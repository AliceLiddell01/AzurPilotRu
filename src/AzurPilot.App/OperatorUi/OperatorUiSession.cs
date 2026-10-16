using System.Text;
using AzurPilot.Core.MuMu;

namespace AzurPilot.App.OperatorUi;

/// <summary>Ограниченное сообщение операторской сессии.</summary>
/// <param name="Title">Тип или заголовок сообщения.</param>
/// <param name="Text">Текст, очищенный от управляющих символов терминала.</param>
/// <param name="Kind">Семантический статус, не зависящий от цвета renderer-а.</param>
/// <param name="Details">Технические наблюдения, показываемые только по запросу.</param>
public sealed record OperatorUiMessage(
    string Title,
    string Text,
    OperatorUiMessageKind Kind = OperatorUiMessageKind.Information,
    string? Details = null);

/// <summary>Семантический тип сообщения операторской поверхности.</summary>
public enum OperatorUiMessageKind
{
    /// <summary>Справочная информация или нейтральное состояние.</summary>
    Information,
    /// <summary>Успешно завершённое действие.</summary>
    Success,
    /// <summary>Наблюдённое ограничение или состояние, требующее внимания.</summary>
    Warning,
    /// <summary>Операция завершилась отказом.</summary>
    Error,
}

/// <summary>Неизменяемая пара действия и точного целевого экземпляра, предъявленная для подтверждения.</summary>
/// <param name="Action">Mutation.</param>
/// <param name="Target">Предъявленный идентификатор.</param>
public sealed record OperatorUiConfirmation(OperatorUiAction Action, MuMuInstanceId Target);

/// <summary>Чистое UI-состояние: ограниченная история, явно выбранный экземпляр и однократное подтверждение mutation.</summary>
/// <remarks>Все изменения выполняются в потоке UI; бизнес-операции принадлежат сервису оператора.</remarks>
public sealed class OperatorUiSession
{
    private const int MessageCapacity = 128;
    private const int HistoryCapacity = 64;
    private const int MaximumMessageLength = 8192;
    private const int MaximumDetailsLength = 8192;
    private readonly Queue<OperatorUiMessage> _messages = new();
    private readonly List<string> _history = [];
    private OperatorUiConfirmation? _authorized;
    private int _historyPosition;
    private string _draft = string.Empty;
    private string _composerDraft = string.Empty;
    private bool _composerDraftCaptured;

    /// <summary>Текущий явно выбранный экземпляр; отсутствие выбора не означает автоматический выбор.</summary>
    public MuMuInstanceId? SelectedInstance { get; private set; }

    /// <summary>Признак занятого слота операции.</summary>
    public bool IsOperationActive { get; private set; }

    /// <summary>Ожидающее подтверждение точного действия и экземпляра.</summary>
    public OperatorUiConfirmation? PendingConfirmation { get; private set; }

    /// <summary>Добавляет ограниченное сообщение, удаляя самое старое при заполнении.</summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="text">Краткое сообщение оператору.</param>
    /// <param name="kind">Семантический статус сообщения.</param>
    /// <param name="details">Ограниченные диагностические данные, доступные в отдельном просмотре.</param>
    public void AddMessage(
        string title,
        string text,
        OperatorUiMessageKind kind = OperatorUiMessageKind.Information,
        string? details = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        if (_messages.Count == MessageCapacity)
        {
            _ = _messages.Dequeue();
        }

        _messages.Enqueue(new OperatorUiMessage(
            CleanText(title, 128),
            CleanText(text, MaximumMessageLength),
            kind,
            details is null ? null : CleanText(details, MaximumDetailsLength)));
    }

    /// <summary>Возвращает независимый снимок сообщений.</summary>
    /// <returns>Сообщения в порядке поступления.</returns>
    public IReadOnlyList<OperatorUiMessage> SnapshotMessages() => [.. _messages];

    /// <summary>Меняет выбор только в idle, сбрасывая любое старое подтверждение.</summary>
    /// <param name="instance">Identity из фактического inventory либо отсутствие выбора.</param>
    /// <returns>Удалось ли изменить выбор.</returns>
    public bool SelectInstance(MuMuInstanceId? instance)
    {
        if (IsOperationActive)
        {
            return false;
        }

        SelectedInstance = instance;
        CancelConfirmation();
        return true;
    }

    /// <summary>Предъявляет отдельное подтверждение изменения состояния явно выбранного экземпляра.</summary>
    /// <param name="action">Запрошенное действие.</param>
    /// <returns>Создано ли подтверждение.</returns>
    public bool RequestConfirmation(OperatorUiAction action)
    {
        CancelConfirmation();
        if (IsOperationActive || SelectedInstance is not MuMuInstanceId target
            || !OperatorUiCommand.For(action).RequiresConfirmation)
        {
            return false;
        }

        PendingConfirmation = new OperatorUiConfirmation(action, target);
        return true;
    }

    /// <summary>Авторизует только предъявленную пару action/target, если выбор не изменился.</summary>
    /// <returns>Подтверждение применимо к текущему idle-состоянию.</returns>
    public bool ConfirmMutation()
    {
        if (IsOperationActive || PendingConfirmation is not OperatorUiConfirmation pending
            || SelectedInstance != pending.Target)
        {
            CancelConfirmation();
            return false;
        }

        _authorized = pending;
        PendingConfirmation = null;
        return true;
    }

    /// <summary>Удаляет предъявленное и ранее разрешённое подтверждение.</summary>
    public void CancelConfirmation()
    {
        PendingConfirmation = null;
        _authorized = null;
    }

    /// <summary>Занимает operation slot и однократно потребляет подтверждение mutation.</summary>
    /// <param name="action">Service operation.</param>
    /// <param name="target">Захваченная identity; UI передаёт сервису именно её.</param>
    /// <returns>Операция разрешена.</returns>
    public bool TryBeginOperation(OperatorUiAction action, out MuMuInstanceId? target)
    {
        target = SelectedInstance;
        OperatorUiCommand command = OperatorUiCommand.For(action);
        if (IsOperationActive || !command.IsOperation || (command.RequiresInstance && target is null))
        {
            return false;
        }

        if (command.RequiresConfirmation
            && (_authorized is null || _authorized.Action != action || _authorized.Target != target))
        {
            return false;
        }

        CancelConfirmation();
        IsOperationActive = true;
        return true;
    }

    /// <summary>Освобождает operation slot после фактического завершения task.</summary>
    public void CompleteOperation() => IsOperationActive = false;

    /// <summary>Запоминает отправленную команду и сбрасывает navigation position.</summary>
    /// <param name="command">Отправленная строка.</param>
    public void RememberCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_history.Count == HistoryCapacity)
        {
            _history.RemoveAt(0);
        }

        _history.Add(CleanText(command, 512));
        _historyPosition = _history.Count;
        _draft = string.Empty;
    }

    /// <summary>Перемещает курсор истории, сохраняя несохранённый черновик ввода.</summary>
    /// <param name="direction">Отрицательное значение — предыдущая команда; положительное — следующая.</param>
    /// <param name="currentDraft">Текущий текст ввода.</param>
    /// <returns>Команда либо сохранённый черновик.</returns>
    public string NavigateHistory(int direction, string currentDraft)
    {
        ArgumentNullException.ThrowIfNull(currentDraft);
        if (_historyPosition == _history.Count)
        {
            _draft = currentDraft;
        }

        _historyPosition = Math.Clamp(_historyPosition + Math.Sign(direction), 0, _history.Count);
        return _historyPosition == _history.Count ? _draft : _history[_historyPosition];
    }

    /// <summary>Запоминает состояние поля ввода перед открытием палитры.</summary>
    /// <param name="composerText">Текст поля ввода до подстановки триггера палитры.</param>
    public void CaptureComposerDraft(string composerText)
    {
        ArgumentNullException.ThrowIfNull(composerText);
        _composerDraft = composerText;
        _composerDraftCaptured = true;
    }

    /// <summary>Забывает сохранённый черновик ввода после принятого действия.</summary>
    public void DiscardComposerDraft()
    {
        _composerDraft = string.Empty;
        _composerDraftCaptured = false;
    }

    /// <summary>Возвращает текст поля ввода для отмены палитры и очищает сохранённое состояние.</summary>
    /// <remarks>
    /// Одиночный <c>/</c> — триггер палитры, а не черновик оператора: после отмены он не восстанавливается.
    /// Осмысленный черновик возвращается без изменений.
    /// </remarks>
    /// <returns>Текст для восстановления либо <see langword="null"/>, если палитра не открывалась.</returns>
    public string? RestoreComposerDraft()
    {
        if (!_composerDraftCaptured)
        {
            return null;
        }

        string draft = _composerDraft;
        DiscardComposerDraft();
        return draft.Length == 0 || string.Equals(draft, "/", StringComparison.Ordinal) ? string.Empty : draft;
    }

    /// <summary>Убирает C0/C1 terminal controls, сохраняя Unicode и переводы строк.</summary>
    /// <param name="text">Внешний текст.</param>
    /// <param name="maximumLength">Предел UTF-16 длины без обрыва surrogate pair.</param>
    /// <returns>Ограниченный текст для renderer.</returns>
    public static string CleanText(string text, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        StringBuilder clean = new(Math.Min(text.Length, maximumLength));
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value != '\n')
            {
                continue;
            }

            if (clean.Length + rune.Utf16SequenceLength > maximumLength)
            {
                break;
            }

            _ = clean.Append(rune.ToString());
        }

        return clean.ToString();
    }
}
