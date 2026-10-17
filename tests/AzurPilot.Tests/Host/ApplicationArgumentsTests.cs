using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.Host;

public sealed class ApplicationArgumentsTests
{
    [Fact]
    public async Task IsolatedUiCannotFallBackToUserSettingsWhenExplicitFileIsMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "azurpilot-absent-" + Guid.NewGuid().ToString("N"), "config.yaml");
        _ = await Assert.ThrowsAsync<ArgumentException>(() => AzurPilotHost.RunInteractiveAsync(
            static (_, _, _, _, _, _) => Task.CompletedTask,
            configurationPath: missing, isolatedUi: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void IsolatedUiRequiresAbsoluteRootAndCannotLaunchViewer()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "azurpilot-isolated-ui"));
        ApplicationArguments parsed = ApplicationArguments.Parse(["--isolated-ui", root, "--mode", "diagnostic"]);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(root, parsed.IsolatedUiRoot);
        Assert.False(ApplicationArguments.Parse(["--isolated-ui", "relative"]).IsSuccess);
        Assert.False(ApplicationArguments.Parse(["--isolated-ui", root, "--log-viewer", "--log-viewer-pipe", "test"]).IsSuccess);
        Assert.Null(ApplicationArguments.Parse([]).IsolatedUiRoot);
    }

    [Fact]
    public void LiveEnvironmentTargetRequiresIsolatedUiAndCanonicalExactIdentity()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "azurpilot-isolated-ui"));
        ApplicationArguments parsed = ApplicationArguments.Parse(
            ["--isolated-ui", root, "--live-environment-target", "mumu:7"]);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(MuMuInstanceId.FromIndex("7"), parsed.LiveEnvironmentTarget);
        Assert.False(ApplicationArguments.Parse(["--live-environment-target", "mumu:7"]).IsSuccess);
        Assert.False(ApplicationArguments.Parse(
            ["--isolated-ui", root, "--live-environment-target", "auto"]).IsSuccess);
        Assert.False(ApplicationArguments.Parse(
            ["--isolated-ui", root, "--live-environment-target", "7"]).IsSuccess);
    }

    [Theory(DisplayName = "CLI выбирает точный режим без изменения настроек")]
    [InlineData("normal", OperatorUiMode.Normal)]
    [InlineData("manual", OperatorUiMode.Manual)]
    [InlineData("diagnostic", OperatorUiMode.Diagnostic)]
    public void AcceptsMode(string text, OperatorUiMode mode)
    {
        ApplicationArguments parsed = ApplicationArguments.Parse(["--mode", text]);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(mode, parsed.Mode);
    }

    [Theory(DisplayName = "Неверные, повторные и несовместимые CLI параметры отклоняются")]
    [InlineData("--mode")]
    [InlineData("--mode Normal")]
    [InlineData("--mode 0")]
    [InlineData("--mode normal --mode diagnostic")]
    [InlineData("--unknown")]
    [InlineData("--log-viewer")]
    [InlineData("--log-viewer-level 2")]
    [InlineData("--log-viewer --log-viewer-pipe ok --log-viewer-level 2")]
    [InlineData("--mode normal --log-viewer --log-viewer-pipe ok")]
    public void RejectsInvalidArguments(string text)
    {
        Assert.False(ApplicationArguments.Parse(text.Split(' ')).IsSuccess);
    }

    [Fact(DisplayName = "Справка не требует запуска host или конфигурации")]
    public void HelpIsExplicit()
    {
        Assert.True(ApplicationArguments.Parse(["--help"]).Help);
        Assert.True(ApplicationArguments.Parse(["-h"]).Help);
        Assert.False(ApplicationArguments.Parse([]).Help);
    }

    [Theory(DisplayName = "Флаг справки нельзя сочетать с другими CLI-параметрами")]
    [InlineData("--help --mode manual")]
    [InlineData("--mode manual -h")]
    public void HelpCannotBeCombinedWithOtherArguments(string arguments)
    {
        ApplicationArguments parsed = ApplicationArguments.Parse(arguments.Split(' '));

        Assert.False(parsed.IsSuccess);
        Assert.Contains("не сочетается с другими параметрами", parsed.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Отказ режима и usage строятся из единого списка допустимых режимов")]
    public void InvalidModeUsesOwnedModeNames()
    {
        ApplicationArguments parsed = ApplicationArguments.Parse(["--mode", "bogus"]);
        string allowedModes = string.Join(", ", OperatorUiModeNames.All);
        string usageModes = string.Join("|", OperatorUiModeNames.All);

        Assert.Contains(allowedModes, parsed.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("[--mode " + usageModes + "]", parsed.Failure.ReportLines()[1], StringComparison.Ordinal);
    }
}
