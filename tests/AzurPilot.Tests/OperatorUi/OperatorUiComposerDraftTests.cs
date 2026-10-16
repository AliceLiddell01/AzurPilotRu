using AzurPilot.App.OperatorUi;
using Xunit;

namespace AzurPilot.Tests.OperatorUi;

/// <summary>Контракт хранения черновик ввода в состоянии операторской сессии.</summary>
[Trait("Category", "Operator")]
public sealed class OperatorUiComposerDraftTests
{
    [Fact(DisplayName = "Сессия не восстанавливает одиночный триггер палитры")]
    public void RestoreComposerDraftClearsPaletteTrigger()
    {
        OperatorUiSession session = new();
        session.CaptureComposerDraft("/");

        Assert.Equal(string.Empty, session.RestoreComposerDraft());
        Assert.Null(session.RestoreComposerDraft());
    }

    [Fact(DisplayName = "Сессия однократно возвращает черновик и забывает принятый")]
    public void RestoreComposerDraftIsOneShotAndDiscardable()
    {
        const string draft = "подготовить среду";
        OperatorUiSession session = new();
        session.CaptureComposerDraft(draft);

        Assert.Equal(draft, session.RestoreComposerDraft());
        Assert.Null(session.RestoreComposerDraft());

        session.CaptureComposerDraft(draft);
        session.DiscardComposerDraft();
        Assert.Null(session.RestoreComposerDraft());
    }
}
