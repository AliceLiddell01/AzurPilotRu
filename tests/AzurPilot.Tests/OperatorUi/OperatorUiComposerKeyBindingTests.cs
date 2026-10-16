using AzurPilot.App;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Configuration;
using AzurPilot.Tests.Configuration;
using AzurPilot.Tests.MuMu;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

using PaletteMode = AzurPilot.App.OperatorUi.AzurPilotOperatorUi.PaletteMode;

namespace AzurPilot.Tests.OperatorUi;

/// <summary>Настоящие обработчики клавиш и TextChanged на production-элементах без Run().</summary>
[Trait("Category", "Operator")]
public sealed class OperatorUiComposerKeyBindingTests : IDisposable
{
    private readonly TemporaryConfigurationDirectory _directory = new();
    private readonly IHost _host;
    private readonly AzurPilotOperatorUi _ui;
    private readonly Runnable _window = new();

    public OperatorUiComposerKeyBindingTests()
    {
        var builder = AzurPilotHost.CreateBuilder(AzurPilotConfigurationLoader.Load(_directory.MissingConfigurationFilePath));
        _ = builder.Services.RemoveAll<IMuMuHost>();
        _ = builder.Services.AddSingleton<IMuMuHost>(new TestMuMuHost());
        _ = builder.Logging.ClearProviders();
        _host = builder.Build();
        _ui = new AzurPilotOperatorUi(_host.Services.GetRequiredService<AzurPilotOperatorService>(),
            _host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture(),
            new OperatorUiLogSourceAdapter(static () => []), OperatorUiMode.Diagnostic);
        _ui.InitializeViews(_window);
    }

    [Fact]
    public void LateTerminalBackgroundChangesPaletteWithoutLosingDraft()
    {
        _ui.ComposerText = "черновик";
        _ui.ApplyTerminalBackground(new Terminal.Gui.Drawing.Color("#F2F2F2"));
        Assert.Contains(_window.SubViews, view => view.GetAttributeForRole(Terminal.Gui.Drawing.VisualRole.Normal).Foreground
            == new Terminal.Gui.Drawing.Color("#5F6368"));
        _ui.ApplyTerminalBackground(Terminal.Gui.Drawing.Color.None);
        Assert.Contains(_window.SubViews, view => view.GetAttributeForRole(Terminal.Gui.Drawing.VisualRole.Normal).Foreground
            == new Terminal.Gui.Drawing.Color("#5F6368"));
        _ui.ApplyTerminalBackground(new Terminal.Gui.Drawing.Color("#0C0C0C"));
        Assert.Contains(_window.SubViews, view => view.GetAttributeForRole(Terminal.Gui.Drawing.VisualRole.Normal).Foreground
            == new Terminal.Gui.Drawing.Color("#9A9A9A"));
        Assert.Equal("черновик", _ui.ComposerText);
    }

    [Theory(DisplayName = "F2 сохраняет исходный текст при одном и повторных открытиях")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RepeatedF2PreservesOriginalDraft(int count)
    {
        _ui.ComposerText = "подготовить среду";
        for (int i = 0; i < count; i++)
        {
            Press(Key.F2);
        }

        Assert.Equal(PaletteMode.Commands, _ui.CurrentPalette);
        Press(Key.Esc);
        Assert.Equal("подготовить среду", _ui.ComposerText);
        Assert.Equal(PaletteMode.Hidden, _ui.CurrentPalette);
        Assert.Null(_ui.Session.RestoreComposerDraft());
    }

    [Theory(DisplayName = "Отмена набранного запроса возвращает пустой ввод")]
    [InlineData("/")]
    [InlineData("/details")]
    public void TriggerThenEscapeRestoresEmptyDraft(string text)
    {
        foreach (char character in text)
        {
            _ui.ComposerText += character;
        }

        Assert.Equal(PaletteMode.Commands, _ui.CurrentPalette);
        Press(Key.Esc);
        Assert.Empty(_ui.ComposerText);
    }

    [Fact(DisplayName = "Ввод в открытой палитре не заменяет сохранённый черновик")]
    public void TypingInsidePaletteKeepsOriginalDraft()
    {
        _ui.ComposerText = "черновик";
        Press(Key.F2);
        _ui.ComposerText = "/det";
        Press(Key.F2);
        Press(Key.Esc);
        Assert.Equal("черновик", _ui.ComposerText);
    }

    [Fact(DisplayName = "Tab принимает конечную команду через HandleKey и AcceptChoice")]
    public void AcceptedCompletionIsNotClearedByEscape()
    {
        _ui.ComposerText = "черновик";
        Press(Key.F2);
        _ui.ComposerText = "/sett";
        Press(Key.Tab);
        Assert.Equal("/settings", _ui.ComposerText);
        Assert.Equal(PaletteMode.Hidden, _ui.CurrentPalette);
        Assert.Null(_ui.Session.RestoreComposerDraft());
        Press(Key.Esc);
        Assert.Equal("/settings", _ui.ComposerText);
    }

    [Fact(DisplayName = "Tab на категории и вложенном действии оставляет меню; Esc закрывает один уровень")]
    public void CategoryTabDoesNotCompleteAndEscapeReturnsOneLevel()
    {
        _ui.ComposerText = "черновик";
        Press(Key.F2);
        _ui.ComposerText = "/inst";
        Press(Key.Tab);
        Assert.Equal(PaletteMode.Commands, _ui.CurrentPalette);
        Assert.Equal("/inst", _ui.ComposerText);
        Press(Key.Enter);
        Assert.Equal(PaletteMode.Category, _ui.CurrentPalette);
        Press(Key.Tab);
        Assert.Equal(PaletteMode.Category, _ui.CurrentPalette);
        Assert.Equal("/inst", _ui.ComposerText);
        Press(Key.Esc);
        Assert.Equal(PaletteMode.Commands, _ui.CurrentPalette);
        Press(Key.Esc);
        Assert.Equal("черновик", _ui.ComposerText);
    }

    [Fact(DisplayName = "Enter исполняет выбранную конечную команду и забывает черновик")]
    public void EnterSubmitsSelectedCommand()
    {
        _ui.ComposerText = "черновик";
        Press(Key.F2);
        _ui.ComposerText = "/sett";
        Press(Key.Enter);
        Assert.Empty(_ui.ComposerText);
        Assert.Equal(PaletteMode.Settings, _ui.CurrentPalette);
        Assert.Null(_ui.Session.RestoreComposerDraft());
        Press(Key.Esc);
        Assert.Empty(_ui.ComposerText);
    }

    [Fact(DisplayName = "Каждое новое открытие захватывает новый черновик")]
    public void SequentialOpeningsRestoreTheirOwnDrafts()
    {
        foreach (string draft in new[] { "первый", "второй", "" })
        {
            _ui.ComposerText = draft;
            Press(Key.F2);
            Press(Key.Esc);
            Assert.Equal(draft, _ui.ComposerText);
        }
    }

    [Theory(DisplayName = "Реальный Enter соблюдает режим доступа к технической команде")]
    [InlineData(OperatorUiMode.Normal, false)]
    [InlineData(OperatorUiMode.Manual, false)]
    [InlineData(OperatorUiMode.Diagnostic, true)]
    public void EnterUsesModePermissionGate(OperatorUiMode mode, bool allowed)
    {
        using AzurPilotOperatorUi ui = new(_host.Services.GetRequiredService<AzurPilotOperatorService>(),
            _host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture(),
            new OperatorUiLogSourceAdapter(static () => []), mode);
        using Runnable window = new();
        ui.InitializeViews(window);
        ui.ComposerText = "/details";
        ui.HandleKey(new Key(Key.Enter.KeyCode));
        Assert.Equal(allowed ? PaletteMode.Details : PaletteMode.Hidden, ui.CurrentPalette);
        Assert.False(ui.Session.IsOperationActive);
        if (!allowed)
        {
            Assert.Equal(OperatorUiMessageKind.Warning, ui.Session.SnapshotMessages()[^1].Kind);
        }
    }

    [Fact(DisplayName = "Tab из закрытой палитры сохраняет текст до Esc")]
    public void TabFromHiddenPaletteRestoresDraft()
    {
        _ui.ComposerText = "черновик";
        Press(Key.Tab);
        Assert.Equal(PaletteMode.Commands, _ui.CurrentPalette);
        Assert.Equal("черновик", _ui.ComposerText);
        Press(Key.Esc);
        Assert.Equal(PaletteMode.Hidden, _ui.CurrentPalette);
        Assert.Equal("черновик", _ui.ComposerText);
    }

    [Fact(DisplayName = "Enter во вложенной категории исполняет действие и забывает исходный черновик")]
    public void CategoryEnterDispatchesAndDiscardsDraft()
    {
        _ui.ComposerText = "черновик";
        Press(Key.F2);
        _ui.ComposerText = "/det";
        Press(Key.Enter);
        Assert.Equal(PaletteMode.Category, _ui.CurrentPalette);
        Press(Key.End);
        Press(Key.Enter);
        Assert.Equal(PaletteMode.Details, _ui.CurrentPalette);
        Assert.Empty(_ui.ComposerText);
        Assert.Null(_ui.Session.RestoreComposerDraft());
        Press(Key.Esc);
        Assert.Empty(_ui.ComposerText);
    }

    private void Press(Key key) => _ui.HandleKey(new Key(key.KeyCode));

    public void Dispose()
    {
        _ui.Dispose();
        _window.Dispose();
        _host.Dispose();
        _directory.Dispose();
    }
}
