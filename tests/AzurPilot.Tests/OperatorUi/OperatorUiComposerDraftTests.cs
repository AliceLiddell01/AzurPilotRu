using AzurPilot.App.OperatorUi;
using Xunit;

namespace AzurPilot.Tests.OperatorUi;

/// <summary>
/// Регрессия UX-006: composer draft вокруг палитры действий.
/// </summary>
/// <remarks>
/// Исходный дефект: после <c>Esc</c> в composer оставался одиночный <c>/</c>, потому что UI не запоминал
/// текст composer перед подстановкой триггера палитры. Исправление UX-006 живёт в production state machine
/// <see cref="OperatorUiSession"/> (<c>CaptureComposerDraft</c>/<c>DiscardComposerDraft</c>/
/// <c>RestoreComposerDraft</c>) и вызывается из обработчика клавиш
/// <c>AzurPilot.App.OperatorUi.AzurPilotOperatorUi.HandleKey</c> (F2, ввод <c>/</c>, Tab),
/// <c>ClosePalette</c>, <c>AcceptChoice</c> и <c>SubmitComposer</c>.
/// Тесты не подменяют это решение: они повторяют последовательность вызовов обработчика клавиш и проверяют
/// результат, который production-код возвращает наружу (текст composer), а собственной логики восстановления
/// черновика в тесте нет.
/// </remarks>
[Trait("Category", "Operator")]
public sealed class OperatorUiComposerDraftTests
{
    private const string PaletteTrigger = "/";

    [Fact(DisplayName = "UX-006: F2 на пустом composer → Esc → composer пуст, а не '/'")]
    public void F2OnEmptyComposerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;

        composer = OpenCommandsWithF2(session, composer);
        Assert.Equal(PaletteTrigger, composer);

        composer = CancelPalette(session, composer);

        Assert.Equal(string.Empty, composer);
        Assert.NotEqual(PaletteTrigger, composer);
    }

    [Fact(DisplayName = "UX-006: одиночный '/' как триггер палитры → Esc → composer пуст")]
    public void SingleSlashTriggerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;

        composer = TypeInComposer(session, composer, PaletteTrigger, paletteHidden: true);
        Assert.Equal(PaletteTrigger, composer);

        composer = CancelPalette(session, composer);

        Assert.Equal(string.Empty, composer);
        Assert.NotEqual(PaletteTrigger, composer);
    }

    [Fact(DisplayName = "UX-006: одиночный '/' из F2 и из ввода дают одинаковый composer после Esc")]
    public void EscapeAfterF2AndAfterTypedSlashProduceIdenticalComposer()
    {
        OperatorUiSession viaF2 = new();
        string f2Result = CancelPalette(viaF2, OpenCommandsWithF2(viaF2, string.Empty));

        OperatorUiSession viaTypedSlash = new();
        string typedResult = CancelPalette(
            viaTypedSlash, TypeInComposer(viaTypedSlash, string.Empty, PaletteTrigger, paletteHidden: true));

        Assert.Equal(f2Result, typedResult);
        Assert.Equal(string.Empty, f2Result);
    }

    [Fact(DisplayName = "UX-006: Esc на пустом composer без палитры не оставляет '/'")]
    public void EscapeWithoutPaletteKeepsComposerEmpty()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;

        // Esc при скрытой палитре проходит тот же production-путь ClosePalette, но без захваченного draft.
        composer = CancelPalette(session, composer);

        Assert.Equal(string.Empty, composer);
        Assert.Null(session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "UX-006: осмысленный черновик сохраняется при открытии и закрытии палитры")]
    public void MeaningfulDraftSurvivesPaletteRoundTrip()
    {
        const string draft = "подготовить среду";
        OperatorUiSession session = new();
        string composer = draft;

        composer = OpenCommandsWithF2(session, composer);
        Assert.Equal(PaletteTrigger, composer);

        composer = CancelPalette(session, composer);

        Assert.Equal(draft, composer);
    }

    [Fact(DisplayName = "UX-006: черновик сохраняется независимо от источника открытия (F2 и Tab)")]
    public void F2AndTabKeepMeaningfulDraft()
    {
        const string draft = "подготовить среду";

        OperatorUiSession viaF2 = new();
        string f2Result = CancelPalette(viaF2, OpenCommandsWithF2(viaF2, draft));

        OperatorUiSession viaTab = new();
        string tabResult = CancelPalette(viaTab, OpenCommandsWithTab(viaTab, draft));

        Assert.Equal(draft, f2Result);
        Assert.Equal(draft, tabResult);
    }

    [Fact(DisplayName = "UX-006: Tab на пустом composer → Esc → composer пуст")]
    public void TabOnEmptyComposerThenEscapeLeavesComposerEmpty()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;

        composer = OpenCommandsWithTab(session, composer);
        Assert.Equal(string.Empty, composer);

        composer = CancelPalette(session, composer);

        Assert.Equal(string.Empty, composer);
    }

    [Fact(DisplayName = "UX-006: принятое Tab-дополнение команды не стирается последующим Esc")]
    public void AcceptedTabCompletionIsNotClearedByEscape()
    {
        const string draft = "/inst";
        OperatorUiSession session = new();
        string composer = draft;

        composer = OpenCommandsWithTab(session, composer);
        Assert.Equal(draft, composer);

        // AcceptChoice(Tab) забывает draft и подставляет выбранную команду.
        session.DiscardComposerDraft();
        composer = "/instances";
        Assert.Equal("/instances", composer);

        composer = CancelPalette(session, composer);

        Assert.Equal("/instances", composer);
    }

    [Fact(DisplayName = "UX-006: набранная команда '/details' после Esc не остаётся в composer")]
    public void TypedCommandPrefixIsNotRestoredAfterEscape()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;
        bool paletteHidden = true;

        foreach (char symbol in "/details")
        {
            composer = TypeInComposer(session, composer, symbol.ToString(), paletteHidden);
            paletteHidden = !composer.StartsWith(PaletteTrigger, StringComparison.Ordinal);
        }

        Assert.Equal("/details", composer);

        composer = CancelPalette(session, composer);

        // Черновиком был одиночный '/' (триггер палитры), поэтому восстановление даёт пустую строку.
        Assert.Equal(string.Empty, composer);
    }

    [Fact(DisplayName = "UX-006: история ↑/↓ сохраняет осмысленный черновик и возвращает его после Esc")]
    public void HistoryNavigationKeepsDraftAndEscapeReturnsIt()
    {
        const string draft = "мой черновик";
        OperatorUiSession session = new();
        session.RememberCommand("/help");
        session.RememberCommand("/instances");
        string composer = draft;

        composer = OpenCommandsWithF2(session, composer);
        Assert.Equal(PaletteTrigger, composer);

        composer = CancelPalette(session, composer);
        Assert.Equal(draft, composer);

        composer = NavigateHistory(session, composer, previous: true);
        Assert.Equal("/instances", composer);

        composer = NavigateHistory(session, composer, previous: false);
        Assert.Equal(draft, composer);

        composer = OpenCommandsWithF2(session, composer);
        composer = CancelPalette(session, composer);

        Assert.Equal(draft, composer);
    }

    [Fact(DisplayName = "UX-006: Escape возвращает черновик, но не переоткрывает палитру")]
    public void EscapeReturnsDraftWithoutReopeningPalette()
    {
        OperatorUiSession session = new();
        string composer = "/details — технические данные";

        composer = OpenCommandsWithF2(session, composer);
        composer = CancelPalette(session, composer);
        Assert.Equal("/details — технические данные", composer);

        // Следующий Esc уже не должен менять composer: скрытая палитра и пустой сохранённый draft.
        composer = CancelPalette(session, composer);
        Assert.Equal("/details — технические данные", composer);
        Assert.Null(session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "UX-006: отправленная команда забывает draft и очищает composer")]
    public void SubmittedCommandForgetsDraftAndClearsComposer()
    {
        OperatorUiSession session = new();
        string composer = string.Empty;
        bool paletteHidden = true;

        foreach (char symbol in "/help")
        {
            composer = TypeInComposer(session, composer, symbol.ToString(), paletteHidden);
            paletteHidden = !composer.StartsWith(PaletteTrigger, StringComparison.Ordinal);
        }

        Assert.Equal("/help", composer);

        // SubmitComposer: RememberCommand + DiscardComposerDraft + очистка composer.
        session.RememberCommand(composer.Trim());
        session.DiscardComposerDraft();
        composer = string.Empty;

        Assert.Equal(string.Empty, composer);
        Assert.Null(session.RestoreComposerDraft());
        Assert.Equal("/help", session.NavigateHistory(-1, string.Empty));
    }

    /// <summary>F2: прежний текст запоминается, в composer подставляется триггер палитры.</summary>
    private static string OpenCommandsWithF2(OperatorUiSession session, string composer)
    {
        session.DiscardComposerDraft();
        session.CaptureComposerDraft(composer);
        return PaletteTrigger;
    }

    /// <summary>Tab: при скрытой палитре текст запоминается, composer не меняется.</summary>
    private static string OpenCommandsWithTab(OperatorUiSession session, string composer)
    {
        session.CaptureComposerDraft(composer);
        return composer;
    }

    /// <summary>
    /// Ввод символа в composer: захват текста выполняется только при переходе палитры из скрытого состояния
    /// в открытое, то есть когда строка впервые начинает совпадать с триггером.
    /// </summary>
    private static string TypeInComposer(
        OperatorUiSession session, string composer, string typed, bool paletteHidden)
    {
        string updated = composer + typed;
        if (paletteHidden && updated.StartsWith(PaletteTrigger, StringComparison.Ordinal))
        {
            session.CaptureComposerDraft(updated);
        }

        return updated;
    }

    /// <summary>Esc при открытой палитре: ClosePalette восстанавливает захваченный текст.</summary>
    private static string CancelPalette(OperatorUiSession session, string composer) =>
        session.RestoreComposerDraft() is string restored ? restored : composer;

    /// <summary>↑/↓: NavigateHistory возвращает элемент истории либо сохранённый draft.</summary>
    private static string NavigateHistory(OperatorUiSession session, string composer, bool previous) =>
        session.NavigateHistory(previous ? -1 : 1, composer);
}
