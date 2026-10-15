using AzurPilot.App;
using AzurPilot.Core.Configuration;
using Xunit;

namespace AzurPilot.Tests.Host;

public sealed class ApplicationArgumentsTests
{
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
    [InlineData("--mode normal --log-viewer --log-viewer-pipe ok")]
    public void RejectsInvalidArguments(string text)
        => Assert.False(ApplicationArguments.Parse(text.Split(' ')).IsSuccess);

    [Fact(DisplayName = "Справка не требует запуска host или конфигурации")]
    public void HelpIsExplicit()
    {
        Assert.True(ApplicationArguments.Parse(["--help"]).Help);
        Assert.True(ApplicationArguments.Parse(["-h"]).Help);
        Assert.False(ApplicationArguments.Parse([]).Help);
    }
}
