using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Configuration;
using Xunit;

namespace AzurPilot.Tests.OperatorUi;

/// <summary>
/// Проверяет каталог действий и правило доступа режимов: обычный режим не исполняет технические команды
/// даже при прямом вводе, а палитра первого уровня остаётся короткой.
/// </summary>
[Trait("Category", "Operator")]
public sealed class OperatorUiActionCatalogTests
{
    [Theory(DisplayName = "Сообщение о недоступном действии использует предложный падеж")]
    [InlineData(OperatorUiMode.Normal, "обычном")]
    [InlineData(OperatorUiMode.Manual, "ручном")]
    [InlineData(OperatorUiMode.Diagnostic, "диагностическом")]
    public void ModePresentationUsesPrepositionalCase(OperatorUiMode mode, string expected)
        => Assert.Equal(expected, OperatorUiModePresentation.PrepositionalLabel(mode));

    [Theory(DisplayName = "Полная техническая команда передаётся из палитры в dispatch, slash и префикс остаются меню")]
    [InlineData("/screenshot", true)]
    [InlineData("/android", true)]
    [InlineData("/instances", true)]
    [InlineData("/environment-prepare", true)]
    [InlineData("/", false)]
    [InlineData("/scree", false)]
    [InlineData("/resolve-target", false)]
    public void DirectCommandTakesPrecedenceOverCategories(string input, bool expected)
        => Assert.Equal(expected, AzurPilotOperatorUi.IsDirectCommand(input));
    [Theory(DisplayName = "Legacy technical alias запрещён в normal и адресует тот же use case в manual")]
    [InlineData("/environment-prepare", OperatorUiAction.EnvironmentPrepare)]
    [InlineData("/mumu-stop", OperatorUiAction.MuMuStop)]
    public void TechnicalAliasKeepsModeBoundary(string alias, OperatorUiAction action)
    {
        Assert.Equal(AzurPilotOperatorUi.CommandResolutionKind.NotAvailableInMode,
            AzurPilotOperatorUi.ResolveCommand(alias, OperatorUiMode.Normal).Kind);
        AzurPilotOperatorUi.CommandResolution manual = AzurPilotOperatorUi.ResolveCommand(alias, OperatorUiMode.Manual);
        Assert.Equal(AzurPilotOperatorUi.CommandResolutionKind.Available, manual.Kind);
        Assert.Equal(action, manual.Command!.Action);
    }

    [Fact(DisplayName = "Внутренний шаг разрешения target нельзя вызвать из composer")]
    public void InternalTargetResolutionIsNotAUserCommand()
        => Assert.Null(OperatorUiCommand.Match("/resolve-target"));

    [Fact(DisplayName = "Предикат занятости запрещает новые операции, сохраняя отмену и выход")]
    public void BusyPredicateRejectsNewOperations()
    {
        Assert.All(OperatorUiCommand.All.Where(command => command.IsOperation), command => Assert.False(command.CanExecute(true)));
        Assert.True(OperatorUiCommand.For(OperatorUiAction.Cancel).CanExecute(true));
        Assert.True(OperatorUiCommand.For(OperatorUiAction.Exit).CanExecute(true));
    }
    /// <summary>Технические команды, недоступные в обычном режиме.</summary>
    public static TheoryData<string> TechnicalCommands =>
    [
        "/instances",
        "/select",
        "/mumu-start",
        "/mumu-restart",
        "/android",
        "/android-prepare",
        "/game",
        "/game-restart",
        "/diagnostics",
        "/screenshot",
        "/details",
    ];

    [Fact(DisplayName = "Каталог не содержит повторяющихся команд и действий")]
    public void CatalogHasUniqueCommandsAndActions()
    {
        Assert.Equal(
            OperatorUiCommand.All.Count,
            OperatorUiCommand.All.Select(command => command.Command).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            OperatorUiCommand.All.Count,
            OperatorUiCommand.All.Select(command => command.Action).Distinct().Count());
        Assert.All(OperatorUiCommand.All, command => Assert.StartsWith("/", command.Command, StringComparison.Ordinal));
        Assert.All(OperatorUiCommand.All, command => Assert.False(string.IsNullOrWhiteSpace(command.Title)));
    }

    [Fact(DisplayName = "Каждый пункт каталога описан ровно один раз")]
    public void EveryCatalogEntryIsReachableByItsAction()
    {
        foreach (OperatorUiCommand command in OperatorUiCommand.All)
        {
            Assert.Same(command, OperatorUiCommand.For(command.Action));
        }
    }

    [Fact(DisplayName = "Обычный режим показывает только пользовательские действия")]
    public void NormalModePaletteCarriesOnlyUserActions()
    {
        string[] expected =
        [
            "/environment-start",
            "/environment-stop",
            "/game-start",
            "/game-stop",
            "/status",
            "/settings",
            "/help",
            "/exit",
        ];

        Assert.Equal(expected, OperatorUiCommand.PrimaryFor(OperatorUiMode.Normal).Select(command => command.Command));
        Assert.Empty(OperatorUiCommand.CategoriesFor(OperatorUiMode.Normal));
    }

    [Theory(DisplayName = "Прямой ввод технической команды в обычном режиме не исполняется")]
    [MemberData(nameof(TechnicalCommands))]
    public void TechnicalCommandIsDeniedInNormalMode(string command)
    {
        AzurPilotOperatorUi.CommandResolution resolution =
            AzurPilotOperatorUi.ResolveCommand(command, OperatorUiMode.Normal);

        Assert.Equal(AzurPilotOperatorUi.CommandResolutionKind.NotAvailableInMode, resolution.Kind);
        Assert.NotNull(resolution.Command);
        Assert.True(resolution.Command!.MinimumMode > OperatorUiMode.Normal);
    }

    [Theory(DisplayName = "Та же техническая команда исполняется в ручном или диагностическом режиме")]
    [MemberData(nameof(TechnicalCommands))]
    public void TechnicalCommandIsAvailableInItsMinimumMode(string command)
    {
        OperatorUiCommand catalog = OperatorUiCommand.All.Single(item => item.Command == command);

        Assert.Equal(
            AzurPilotOperatorUi.CommandResolutionKind.Available,
            AzurPilotOperatorUi.ResolveCommand(command, catalog.MinimumMode).Kind);
        Assert.Equal(
            AzurPilotOperatorUi.CommandResolutionKind.Available,
            AzurPilotOperatorUi.ResolveCommand(command, OperatorUiMode.Diagnostic).Kind);
    }

    [Fact(DisplayName = "Ручной режим не даёт диагностических команд, диагностический включает ручные")]
    public void DiagnosticModeIsSupersetOfManualMode()
    {
        string[] manual = [.. OperatorUiCommand.All
            .Where(command => command.IsAvailableIn(OperatorUiMode.Manual))
            .Select(command => command.Command)];
        string[] diagnostic = [.. OperatorUiCommand.All
            .Where(command => command.IsAvailableIn(OperatorUiMode.Diagnostic))
            .Select(command => command.Command)];

        Assert.All(manual, command => Assert.Contains(command, diagnostic));
        Assert.Contains("/screenshot", diagnostic);
        Assert.DoesNotContain("/screenshot", manual);
        Assert.DoesNotContain("/diagnostics", manual);
    }

    [Fact(DisplayName = "Ручной режим скрывает технические команды за категориями, а не плоским списком")]
    public void ManualModeGroupsTechnicalCommandsIntoCategories()
    {
        string[] firstLevel = [.. OperatorUiCommand.PrimaryFor(OperatorUiMode.Manual)
            .Select(command => command.Command)];
        OperatorUiCategoryDefinition[] categories = [.. OperatorUiCommand.CategoriesFor(OperatorUiMode.Manual)];

        Assert.DoesNotContain("/instances", firstLevel);
        Assert.DoesNotContain("/mumu-start", firstLevel);
        Assert.DoesNotContain("/android-prepare", firstLevel);
        Assert.Equal(3, categories.Length);
        Assert.All(categories, category => Assert.NotEmpty(OperatorUiCommand.InCategory(category.Category, OperatorUiMode.Manual)));
        Assert.Contains(OperatorUiCommand.InCategory(OperatorUiCategory.Environment, OperatorUiMode.Manual),
            command => command.Command == "/mumu-start");
        Assert.Contains(OperatorUiCommand.InCategory(OperatorUiCategory.Android, OperatorUiMode.Manual),
            command => command.Command == "/android-prepare");
        Assert.Contains(OperatorUiCommand.InCategory(OperatorUiCategory.Game, OperatorUiMode.Manual),
            command => command.Command == "/game-restart");
    }

    [Fact(DisplayName = "Диагностическая категория появляется только в диагностическом режиме")]
    public void DiagnosticCategoryAppearsOnlyInDiagnosticMode()
    {
        Assert.DoesNotContain(OperatorUiCommand.CategoriesFor(OperatorUiMode.Manual),
            category => category.Category == OperatorUiCategory.Diagnostics);
        OperatorUiCategoryDefinition diagnostics = Assert.Single(
            OperatorUiCommand.CategoriesFor(OperatorUiMode.Diagnostic),
            category => category.Category == OperatorUiCategory.Diagnostics);

        Assert.Equal("Диагностика", diagnostics.Title);
        string[] commands = [.. OperatorUiCommand.InCategory(OperatorUiCategory.Diagnostics, OperatorUiMode.Diagnostic)
            .Select(command => command.Command)];
        Assert.Equal(["/diagnostics", "/screenshot", "/details"], commands);
    }

    [Fact(DisplayName = "Отмена доступна всегда, но не занимает строку меню")]
    public void CancelIsAvailableEverywhereButHiddenFromPalette()
    {
        OperatorUiCommand cancel = OperatorUiCommand.For(OperatorUiAction.Cancel);

        Assert.True(cancel.IsAvailableIn(OperatorUiMode.Normal));
        Assert.False(cancel.ShowInPalette);
        Assert.DoesNotContain(OperatorUiCommand.PrimaryFor(OperatorUiMode.Normal), command => command.Action == OperatorUiAction.Cancel);
        Assert.DoesNotContain(OperatorUiCommand.PrimaryFor(OperatorUiMode.Diagnostic), command => command.Action == OperatorUiAction.Cancel);
    }

    [Fact(DisplayName = "Локальные действия не занимают operation slot и не требуют target")]
    public void LocalActionsAreNotOperations()
    {
        foreach (OperatorUiAction action in new[]
        {
            OperatorUiAction.Settings, OperatorUiAction.Help, OperatorUiAction.Exit,
            OperatorUiAction.Cancel, OperatorUiAction.Details, OperatorUiAction.SelectInstance,
        })
        {
            OperatorUiCommand command = OperatorUiCommand.For(action);
            Assert.False(command.IsOperation);
            Assert.False(command.RequiresInstance);
            Assert.False(command.RequiresConfirmation);
        }
    }

    [Fact(DisplayName = "Read-only состояние и полная диагностика — разные операции без подтверждения")]
    public void StatusAndDiagnosticsAreDistinctReadOnlyOperations()
    {
        OperatorUiCommand status = OperatorUiCommand.For(OperatorUiAction.Status);
        OperatorUiCommand diagnostics = OperatorUiCommand.For(OperatorUiAction.Diagnostics);

        Assert.True(status.IsOperation);
        Assert.True(diagnostics.IsOperation);
        Assert.False(status.RequiresInstance);
        Assert.False(diagnostics.RequiresInstance);
        Assert.False(status.RequiresConfirmation);
        Assert.False(diagnostics.RequiresConfirmation);
        Assert.NotEqual(status.MinimumMode, diagnostics.MinimumMode);
    }

    [Fact(DisplayName = "Подтверждаемая mutation всегда требует exact target")]
    public void EveryConfirmationRequiresExactInstance()
    {
        Assert.All(OperatorUiCommand.All.Where(command => command.RequiresConfirmation), command =>
        {
            Assert.True(command.RequiresInstance);
            Assert.True(command.IsOperation);
        });
    }

    [Fact(DisplayName = "Режимы упорядочены по возрастанию объёма доступных действий")]
    public void ModesAreOrderedByScope()
    {
        Assert.True(OperatorUiMode.Normal < OperatorUiMode.Manual);
        Assert.True(OperatorUiMode.Manual < OperatorUiMode.Diagnostic);
        Assert.All(OperatorUiCommand.All, command => Assert.True(command.IsAvailableIn(command.MinimumMode)));
        Assert.All(OperatorUiCommand.All, command =>
            Assert.True(command.IsAvailableIn(OperatorUiMode.Diagnostic)));
    }

    [Theory(DisplayName = "Значение режима разбирается строго")]
    [InlineData("normal", OperatorUiMode.Normal, true)]
    [InlineData("manual", OperatorUiMode.Manual, true)]
    [InlineData("diagnostic", OperatorUiMode.Diagnostic, true)]
    [InlineData("Normal", OperatorUiMode.Normal, false)]
    [InlineData("diag", OperatorUiMode.Normal, false)]
    [InlineData("", OperatorUiMode.Normal, false)]
    [InlineData(null, OperatorUiMode.Normal, false)]
    public void ModeParsingIsStrict(string? text, OperatorUiMode expected, bool success)
    {
        Assert.Equal(success, OperatorUiModePresentation.TryParse(text, out OperatorUiMode mode));
        if (success)
        {
            Assert.Equal(expected, mode);
        }
    }

    [Fact(DisplayName = "Неизвестная команда отличается от команды, недоступной в режиме")]
    public void UnknownCommandIsDistinguishedFromDeniedCommand()
    {
        Assert.Equal(
            AzurPilotOperatorUi.CommandResolutionKind.Unknown,
            AzurPilotOperatorUi.ResolveCommand("/nope", OperatorUiMode.Diagnostic).Kind);
        Assert.Null(AzurPilotOperatorUi.ResolveCommand("/nope", OperatorUiMode.Diagnostic).Command);
        Assert.Equal(
            AzurPilotOperatorUi.CommandResolutionKind.Available,
            AzurPilotOperatorUi.ResolveCommand("/environment-start", OperatorUiMode.Normal).Kind);
    }

    [Fact(DisplayName = "Подсказка перехода указывает реальный механизм выбора режима")]
    public void SwitchHintNamesRealMechanism()
    {
        Assert.Contains("--mode manual", OperatorUiModePresentation.SwitchHint(OperatorUiMode.Manual));
        Assert.Contains("ui.mode: manual", OperatorUiModePresentation.SwitchHint(OperatorUiMode.Manual));
        Assert.Contains("--mode diagnostic", OperatorUiModePresentation.SwitchHint(OperatorUiMode.Diagnostic));
    }
}
