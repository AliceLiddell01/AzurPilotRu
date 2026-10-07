using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства точной формы аргументов control surface: аргументы передаются списком, а не строкой
/// командной строки, и соответствуют форме, подтверждённой на реальной установке.
/// </summary>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuManagerCommandBuilderTests
{
    private static readonly MuMuInstanceId FirstInstance = MuMuInstanceId.FromIndex("1");

    [Fact(DisplayName = "Подкоманда version вызывается без аргументов")]
    public void VersionArgumentsAreExact()
        => AssertArguments(["version"], MuMuManagerCommandBuilder.BuildVersionArguments());

    [Fact(DisplayName = "Запрос сведений об экземпляре адресуется длинной формой vmindex")]
    public void InstanceInfoArgumentsAreExact()
        => AssertArguments(
            ["info", "--vmindex", "1"],
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(FirstInstance));

    [Fact(DisplayName = "Перечисление всех экземпляров адресуется литералом all")]
    public void AllInstancesArgumentsAreExact()
        => AssertArguments(
            ["info", "--vmindex", "all"],
            MuMuManagerCommandBuilder.BuildAllInstancesInfoArguments());

    [Theory(DisplayName = "Операция изменения состояния адресуется номером экземпляра и именем операции")]
    [InlineData(MuMuControlCommand.Launch, "launch")]
    [InlineData(MuMuControlCommand.Shutdown, "shutdown")]
    public void ControlArgumentsAreExact(MuMuControlCommand command, string operationName)
    {
        AssertArguments(
            ["control", "--vmindex", "1", operationName],
            MuMuManagerCommandBuilder.BuildControlArguments(FirstInstance, command));

        Assert.Equal(operationName, MuMuManagerCommandBuilder.GetOperationName(command));
    }

    [Fact(DisplayName = "Имя провайдерской операции перезапуска сохранено как доказанный факт control surface")]
    public void RestartOperationNameIsRecorded()
    {
        // Verb перезапуска подтверждён на реальной установке, но в перечень операций adapter-а не
        // входит: lifecycle подтверждает переход наблюдением состояния, поэтому production-путь
        // перезапуска выражается композицией остановки и запуска.
        Assert.Equal("restart", MuMuManagerCommandBuilder.RestartOperation);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => MuMuManagerCommandBuilder.BuildControlArguments(FirstInstance, (MuMuControlCommand)42));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => MuMuManagerCommandBuilder.GetOperationName((MuMuControlCommand)42));
    }

    [Fact(DisplayName = "Номер экземпляра передаётся без канонического префикса identity")]
    public void IndexArgumentHasNoCanonicalPrefix()
    {
        Assert.Equal("mumu:7", MuMuInstanceId.FromIndex("7").ToString());

        AssertArguments(
            ["info", "--vmindex", "7"],
            MuMuManagerCommandBuilder.BuildInstanceInfoArguments(MuMuInstanceId.FromIndex("7")));
    }

    private static void AssertArguments(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }
}
