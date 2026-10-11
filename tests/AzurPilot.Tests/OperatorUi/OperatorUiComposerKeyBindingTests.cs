using AzurPilot.App.OperatorUi;
using Terminal.Gui.Input;
using Xunit;

using ComposerKeyEffect = AzurPilot.App.OperatorUi.AzurPilotOperatorUi.ComposerKeyEffect;
using ComposerKeyTransition = AzurPilot.App.OperatorUi.AzurPilotOperatorUi.ComposerKeyTransition;
using ComposerPaletteSource = AzurPilot.App.OperatorUi.AzurPilotOperatorUi.ComposerPaletteSource;
using PaletteMode = AzurPilot.App.OperatorUi.AzurPilotOperatorUi.PaletteMode;

namespace AzurPilot.Tests.OperatorUi;

/// <summary>
/// Регрессия F2: привязка клавиш F2, <c>/</c>, Tab и Esc к состоянию composer и палитры.
/// </summary>
/// <remarks>
/// Тесты вызывают production-seam <see cref="AzurPilotOperatorUi.ApplyComposerKey"/> и
/// <see cref="AzurPilotOperatorUi.OpenPalette"/>, то есть тот же код, которому делегируют
/// <c>AzurPilotOperatorUi.HandleKey</c>, Tab-ветка и триггер <c>/</c> в <c>UpdateCommandPalette</c>.
/// Terminal.Gui-драйвер не нужен: <c>Key</c> — значимый тип, а seam не обращается к представлению.
/// Собственной логики перехода в тесте нет: эффект, возвращённый production-кодом, применяется к тексту
/// composer и к <see cref="OperatorUiSession"/> ровно так же, как это делает представление.
/// </remarks>
[Trait("Category", "Operator")]
public sealed class OperatorUiComposerKeyBindingTests
{
    private const string Trigger = "/";
    private const string Draft = "подготовить среду";
    private const string Command = "/instances";

    [Theory(DisplayName = "F2: каждый источник открытия переводит скрытую палитру в Commands и запоминает черновик")]
    [InlineData(ComposerPaletteSource.FunctionKey, Draft)]
    [InlineData(ComposerPaletteSource.Trigger, Draft)]
    [InlineData(ComposerPaletteSource.Completion, Draft)]
    [InlineData(ComposerPaletteSource.FunctionKey, "")]
    [InlineData(ComposerPaletteSource.Trigger, "")]
    [InlineData(ComposerPaletteSource.Completion, "")]
    internal void OpenPaletteFromEverySourceCapturesDraftAndOpensCommands(ComposerPaletteSource source, string composer)
    {
        OperatorUiSession session = new();

        ComposerKeyTransition transition = AzurPilotOperatorUi.OpenPalette(
            session, PaletteMode.Hidden, source, composer);

        Assert.Equal(ComposerKeyEffect.OpenPalette, transition.Effect);

        // F2 подставляет триггер, Trigger и Completion оставляют текст composer как есть.
        string expectedComposer = source == ComposerPaletteSource.FunctionKey ? Trigger : composer;
        Assert.Equal(expectedComposer, transition.ComposerText ?? composer);

        // Черновик захвачен до подстановки триггера: именно он вернётся при отмене.
        string expectedDraft = composer.Length == 0 || composer == Trigger ? string.Empty : composer;
        Assert.Equal(expectedDraft, session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "F2: F2 на пустом composer → Esc → composer пуст, а не '/'")]
    public void F2OnEmptyComposerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = string.Empty;

        composer = PressKey(Key.F2, session, ref palette, composer);
        Assert.Equal(Trigger, composer);
        Assert.Equal(PaletteMode.Commands, palette);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(string.Empty, composer);
        Assert.NotEqual(Trigger, composer);
        Assert.Equal(PaletteMode.Hidden, palette);
    }

    [Fact(DisplayName = "F2: одиночный триггер '/' → Esc → composer пуст")]
    public void TriggerFromEmptyComposerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = string.Empty;

        composer = OpenFromTrigger(session, ref palette, composer);

        // Триггер уже набран оператором: seam не меняет текст, но палитра открыта.
        Assert.Equal(string.Empty, composer);
        Assert.Equal(PaletteMode.Commands, palette);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(string.Empty, composer);
        Assert.NotEqual(Trigger, composer);
    }

    [Fact(DisplayName = "F2: Tab на пустом composer → Esc → composer пуст")]
    public void CompletionFromEmptyComposerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = string.Empty;

        composer = PressKey(Key.Tab, session, ref palette, composer);
        Assert.Equal(string.Empty, composer);
        Assert.Equal(PaletteMode.Commands, palette);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(string.Empty, composer);
    }

    [Theory(DisplayName = "F2: осмысленный черновик сохраняется при открытии и восстанавливается после Esc")]
    [InlineData(ComposerPaletteSource.FunctionKey)]
    [InlineData(ComposerPaletteSource.Trigger)]
    [InlineData(ComposerPaletteSource.Completion)]
    internal void MeaningfulDraftIsRestoredAfterEscapeForEverySource(ComposerPaletteSource source)
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = Draft;

        composer = OpenFrom(session, source, ref palette, composer);
        Assert.Equal(PaletteMode.Commands, palette);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(Draft, composer);
        Assert.Equal(PaletteMode.Hidden, palette);
        Assert.Null(session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "F2: набранная команда '/details' после Esc не остаётся в composer")]
    public void TypedCommandPrefixIsNotRestoredAfterEscape()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = string.Empty;
        bool paletteHidden = true;

        foreach (char symbol in "/details")
        {
            composer = TypeCharacter(session, ref palette, composer, symbol, paletteHidden);
            paletteHidden = palette == PaletteMode.Hidden;
        }

        Assert.Equal("/details", composer);
        Assert.Equal(PaletteMode.Commands, palette);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        // Черновиком был одиночный '/' (триггер палитры), поэтому восстановление даёт пустую строку.
        Assert.Equal(string.Empty, composer);
    }

    [Fact(DisplayName = "F2: принятое Tab-дополнение не стирается последующим Esc")]
    public void AcceptedCompletionIsNotClearedByEscape()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = string.Empty;
        bool paletteHidden = true;

        foreach (char symbol in "/inst")
        {
            composer = TypeCharacter(session, ref palette, composer, symbol, paletteHidden);
            paletteHidden = palette == PaletteMode.Hidden;
        }

        Assert.Equal("/inst", composer);
        Assert.Equal(PaletteMode.Commands, palette);

        // AcceptChoice(Tab): черновик забывается, composer получает выбранную команду.
        session.DiscardComposerDraft();
        composer = Command;
        Assert.Null(session.RestoreComposerDraft());

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(Command, composer);
        Assert.Equal(PaletteMode.Hidden, palette);
    }

    [Fact(DisplayName = "F2: Esc при скрытой палитре не восстанавливает черновик и не трогает composer")]
    public void EscapeWithoutPaletteKeepsComposerAndDraftUntouched()
    {
        OperatorUiSession session = new();
        string composer = Draft;

        ComposerKeyTransition transition = AzurPilotOperatorUi.ApplyComposerKey(
            Key.Esc, composer, PaletteMode.Hidden, detailsVisible: false, session);

        Assert.Equal(ComposerKeyEffect.ClosePalette, transition.Effect);
        Assert.Null(transition.ComposerText);
        Assert.Equal(Draft, composer);
    }

    [Fact(DisplayName = "F2: Esc при открытых деталях закрывает детали, а не палитру")]
    public void EscapeWithVisibleDetailsClearsDetailsOnly()
    {
        OperatorUiSession session = new();

        ComposerKeyTransition transition = AzurPilotOperatorUi.ApplyComposerKey(
            Key.Esc, Draft, PaletteMode.Hidden, detailsVisible: true, session);

        Assert.Equal(ComposerKeyEffect.ClearDetails, transition.Effect);
        Assert.Null(transition.ComposerText);
        Assert.Null(session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "F2: F2 при открытой палитре обновляет черновик текстом composer")]
    public void FunctionKeyWhilePaletteOpenCapturesCurrentComposerText()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = Draft;

        composer = PressKey(Key.F2, session, ref palette, composer);
        Assert.Equal(Trigger, composer);

        composer = PressKey(Key.F2, session, ref palette, composer);
        Assert.Equal(Trigger, composer);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        // Второй F2 запомнил текущий текст composer, то есть триггер; триггер после Esc не восстанавливается.
        Assert.Equal(string.Empty, composer);
    }

    [Fact(DisplayName = "F2: палитра, открытая не из F2, при повторном Trigger не перезаписывает черновик")]
    public void TriggerWhilePaletteOpenDoesNotRecaptureDraft()
    {
        OperatorUiSession session = new();
        PaletteMode palette = PaletteMode.Hidden;
        string composer = Draft;

        composer = PressKey(Key.F2, session, ref palette, composer);

        ComposerKeyTransition repeated = AzurPilotOperatorUi.OpenPalette(
            session, palette, ComposerPaletteSource.Trigger, Trigger);

        Assert.Equal(ComposerKeyEffect.OpenPalette, repeated.Effect);
        Assert.Null(repeated.ComposerText);

        composer = PressKey(Key.Esc, session, ref palette, composer);

        Assert.Equal(Draft, composer);
    }

    [Fact(DisplayName = "F2: клавиша Tab из скрытой палитры не подставляет триггер и запоминает черновик")]
    public void TabKeyKeepsComposerTextAndCapturesDraft()
    {
        OperatorUiSession session = new();

        ComposerKeyTransition transition = AzurPilotOperatorUi.ApplyComposerKey(
            Key.Tab, Draft, PaletteMode.Hidden, detailsVisible: false, session);

        Assert.Equal(ComposerKeyEffect.OpenPalette, transition.Effect);

        // Tab, в отличие от F2, не подставляет триггер: текст composer остаётся прежним.
        Assert.Null(transition.ComposerText);
        Assert.Equal(Draft, session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "F2: клавиши вне composer и палитры не обрабатываются")]
    public void UnrelatedKeyIsNotHandled()
    {
        OperatorUiSession session = new();

        ComposerKeyTransition transition = AzurPilotOperatorUi.ApplyComposerKey(
            Key.F3, Draft, PaletteMode.Hidden, detailsVisible: false, session);

        Assert.Equal(ComposerKeyEffect.NotHandled, transition.Effect);
        Assert.Null(transition.ComposerText);
    }

    /// <summary>
    /// Прогон клавиши через production-seam с применением эффекта так же, как это делает представление.
    /// </summary>
    private static string PressKey(Key key, OperatorUiSession session, ref PaletteMode palette, string composer)
    {
        ComposerKeyTransition transition = AzurPilotOperatorUi.ApplyComposerKey(
            key, composer, palette, detailsVisible: false, session);

        switch (transition.Effect)
        {
            case ComposerKeyEffect.OpenPalette:
                palette = PaletteMode.Commands;
                break;
            case ComposerKeyEffect.ClosePalette:
                palette = PaletteMode.Hidden;
                break;
            case ComposerKeyEffect.ClearDetails:
            case ComposerKeyEffect.NotHandled:
            default:
                return composer;
        }

        return transition.ComposerText ?? composer;
    }

    /// <summary>Открытие палитры из конкретного источника.</summary>
    private static string OpenFrom(
        OperatorUiSession session, ComposerPaletteSource source, ref PaletteMode palette, string composer)
    {
        if (source == ComposerPaletteSource.FunctionKey)
        {
            return PressKey(Key.F2, session, ref palette, composer);
        }

        if (source == ComposerPaletteSource.Completion)
        {
            return PressKey(Key.Tab, session, ref palette, composer);
        }

        return OpenFromTrigger(session, ref palette, composer);
    }

    /// <summary>Триггер '/' в composer — путь UpdateCommandPalette.</summary>
    private static string OpenFromTrigger(OperatorUiSession session, ref PaletteMode palette, string composer)
    {
        ComposerKeyTransition transition = AzurPilotOperatorUi.OpenPalette(
            session, palette, ComposerPaletteSource.Trigger, composer);
        palette = transition.Effect == ComposerKeyEffect.OpenPalette ? PaletteMode.Commands : palette;
        return transition.ComposerText ?? composer;
    }

    /// <summary>Ввод одного символа: триггер открывает палитру, остальные символы только меняют текст.</summary>
    private static string TypeCharacter(
        OperatorUiSession session,
        ref PaletteMode palette,
        string composer,
        char symbol,
        bool paletteHidden)
    {
        string updated = composer + symbol;
        if (paletteHidden && updated.StartsWith(Trigger, StringComparison.Ordinal))
        {
            return OpenFromTrigger(session, ref palette, updated);
        }

        return updated;
    }
}
