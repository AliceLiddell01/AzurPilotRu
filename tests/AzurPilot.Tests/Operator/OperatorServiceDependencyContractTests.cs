using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Game;
using Xunit;

namespace AzurPilot.Tests.OperatorServices;

public sealed class OperatorServiceDependencyContractTests
{
    [Fact(DisplayName = "Game UI host является обязательной зависимостью операторского сервиса")]
    public void GameUiHostIsNotOptional()
    {
        var parameter = Assert.Single(
            typeof(AzurPilotOperatorService)
                .GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters()),
            static parameter => parameter.ParameterType == typeof(IGameUiHost));

        Assert.False(parameter.IsOptional);
    }

    [Fact(DisplayName = "Операторский сервис требует общий TimeProvider")]
    public void TimeProviderIsNotOptional()
    {
        var parameter = Assert.Single(
            typeof(AzurPilotOperatorService)
                .GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters()),
            static parameter => parameter.ParameterType == typeof(TimeProvider));

        Assert.False(parameter.IsOptional);
    }
}
