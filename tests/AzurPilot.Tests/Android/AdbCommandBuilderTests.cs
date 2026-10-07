using AzurPilot.Core.Android;
using AzurPilot.Windows.Android;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства точной формы аргументов ADB: target-explicit адресация, отсутствие строки командной
/// строки и отсутствие глобальных операций над сервером ADB.
/// </summary>
/// <remarks>
/// Форма аргументов проверяется у её владельца <see cref="AdbCommandBuilder"/>: проверка не повторяет
/// перечень аргументов, а требует, чтобы каждая команда над конкретным устройством несла точный target
/// <c>-s &lt;endpoint&gt;</c> первыми аргументами. Глобальные операции (в том числе <c>kill-server</c> и
/// повторное подключение) запрещены в любом сценарии.
/// </remarks>
[Trait("Category", "Android")]
public sealed class AdbCommandBuilderTests
{
    private static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);

    private static readonly AndroidEndpoint OtherEndpoint = new("127.0.0.1", 16417);

    private static readonly AndroidPackageId Package = new(AzurLaneProduct.Package);

    private static readonly AndroidComponent Launcher = new(
        Package,
        "com.manjuu.azurlane.PrePermissionActivity",
        AzurLaneProduct.Package + "/com.manjuu.azurlane.PrePermissionActivity");

    /// <summary>Метасимволы оболочки: их присутствие означало бы строку командной строки, а не аргумент.</summary>
    private const string ShellMetacharacters = "&|<>^;\"'`%!$(){}[]*?";

    [Fact(DisplayName = "Каждая команда над устройством несёт точный target первыми аргументами")]
    public void EveryTargetCommandCarriesExactEndpoint()
    {
        foreach (IReadOnlyList<string> command in TargetCommands(Endpoint))
        {
            Assert.Equal(AdbCommandBuilder.TargetArgument, command[0]);
            Assert.Equal(Endpoint.ToString(), command[1]);
        }
    }

    [Fact(DisplayName = "От endpoint-а зависит ровно элемент target-а, а не форма команды")]
    public void OnlyTheTargetElementDependsOnTheEndpoint()
    {
        IReadOnlyList<IReadOnlyList<string>> first = TargetCommands(Endpoint);
        IReadOnlyList<IReadOnlyList<string>> second = TargetCommands(OtherEndpoint);

        Assert.Equal(first.Count, second.Count);

        for (int index = 0; index < first.Count; index++)
        {
            Assert.Equal(Endpoint.ToString(), first[index][1]);
            Assert.Equal(OtherEndpoint.ToString(), second[index][1]);
            Assert.Equal(first[index].Take(1).ToArray(), second[index].Take(1).ToArray());
            Assert.Equal(first[index].Skip(2).ToArray(), second[index].Skip(2).ToArray());
        }
    }

    [Fact(DisplayName = "Подключение адресует точный endpoint и не несёт target-флаг")]
    public void ConnectAddressesExactEndpoint()
    {
        string[] expected = [AdbCommandBuilder.ConnectSubcommand, Endpoint.ToString()];

        Assert.Equal(expected, AdbCommandBuilder.BuildConnectArguments(Endpoint).ToArray());
    }

    [Fact(DisplayName = "Аргументы — список, а не строка командной строки")]
    public void ArgumentsAreNotACommandLine()
    {
        foreach (IReadOnlyList<string> command in AllCommands(Endpoint))
        {
            Assert.NotEmpty(command);

            foreach (string argument in command)
            {
                Assert.False(
                    argument.Any(char.IsWhiteSpace),
                    $"Аргумент «{argument}» содержит пробельный символ: строка командной строки не собирается.");
                Assert.False(
                    argument.Any(ShellMetacharacters.Contains),
                    $"Аргумент «{argument}» содержит метасимвол оболочки.");
            }
        }
    }

    [Fact(DisplayName = "Ни одна команда не запрашивает глобальных операций над сервером ADB")]
    public void NoGlobalAdbOperationsAreRequested()
        => AndroidTestOperations.AssertNoneRequested(AllCommands(Endpoint));

    [Fact(DisplayName = "Команда version состоит ровно из одной подкоманды")]
    public void VersionCommandIsSingleSubcommand()
    {
        string[] expected = [AdbCommandBuilder.VersionSubcommand];

        Assert.Equal(expected, AdbCommandBuilder.BuildVersionArguments().ToArray());
    }

    [Fact(DisplayName = "Команды чтения свойств читают точные свойства Android")]
    public void PropertyCommandsReadExactProperties()
    {
        AssertPropertyCommand(
            AdbCommandBuilder.BuildBootCompletedArguments(Endpoint),
            AdbCommandBuilder.BootCompletedProperty);
        AssertPropertyCommand(
            AdbCommandBuilder.BuildAndroidReleaseArguments(Endpoint),
            AdbCommandBuilder.AndroidReleaseProperty);
        AssertPropertyCommand(
            AdbCommandBuilder.BuildSdkLevelArguments(Endpoint),
            AdbCommandBuilder.SdkLevelProperty);
    }

    [Fact(DisplayName = "Команды пакета адресуют ровно запрошенный пакет")]
    public void PackageCommandsAddressExactPackage()
    {
        Assert.Equal(
            Package.ToString(),
            AdbCommandBuilder.BuildPackagePathArguments(Endpoint, Package)[^1]);
        Assert.Equal(
            Package.ToString(),
            AdbCommandBuilder.BuildLauncherQueryArguments(Endpoint, Package)[^1]);
        Assert.Equal(
            Package.ToString(),
            AdbCommandBuilder.BuildForceStopGameArguments(Endpoint, Package)[^1]);
    }

    [Fact(DisplayName = "Запрос launcher-компонента несёт главный launcher-intent пакета")]
    public void LauncherQueryCarriesMainLauncherIntent()
    {
        IReadOnlyList<string> command = AdbCommandBuilder.BuildLauncherQueryArguments(Endpoint, Package);
        List<string> arguments = [.. command];

        Assert.Equal(AdbCommandBuilder.ShellSubcommand, arguments[2]);
        Assert.Equal(AdbCommandBuilder.CommandSubcommand, arguments[3]);
        Assert.Equal(AdbCommandBuilder.PackageServiceName, arguments[4]);
        Assert.Equal(AdbCommandBuilder.QueryActivitiesOperation, arguments[5]);
        Assert.Contains(AdbCommandBuilder.BriefArgument, arguments);
        Assert.Contains(AdbCommandBuilder.ComponentsArgument, arguments);
        Assert.Contains(AdbCommandBuilder.MainAction, arguments);
        Assert.Contains(AdbCommandBuilder.LauncherCategory, arguments);
    }

    [Fact(DisplayName = "Запуск игры адресует разрешённый launcher-компонент, остановка — пакет")]
    public void StartAddressesLauncherAndStopAddressesPackage()
    {
        IReadOnlyList<string> start = AdbCommandBuilder.BuildStartGameArguments(Endpoint, Launcher);

        Assert.Equal(AdbCommandBuilder.StartOperation, start[4]);
        Assert.Equal(AdbCommandBuilder.ComponentArgument, start[5]);
        Assert.Equal(Launcher.Flattened, start[6]);
        Assert.Equal(Package, Launcher.Package);

        IReadOnlyList<string> stop = AdbCommandBuilder.BuildForceStopGameArguments(Endpoint, Package);

        Assert.Equal(AdbCommandBuilder.ForceStopOperation, stop[4]);
        Assert.Equal(Package.ToString(), stop[5]);
    }

    [Fact(DisplayName = "Команды, читающие состояние устройства, не перечисляют процессы целиком")]
    public void ProcessListRequestsOnlyPidAndName()
    {
        IReadOnlyList<string> command = AdbCommandBuilder.BuildProcessListArguments(Endpoint);

        Assert.Equal(AdbCommandBuilder.ProcessListSubcommand, command[3]);
        Assert.Equal(AdbCommandBuilder.AllProcessesArgument, command[4]);
        Assert.Equal(AdbCommandBuilder.FormatArgument, command[5]);
        Assert.Equal(AdbCommandBuilder.ProcessFormat, command[6]);
    }

    private static void AssertPropertyCommand(IReadOnlyList<string> command, string property)
    {
        Assert.Equal(AdbCommandBuilder.ShellSubcommand, command[2]);
        Assert.Equal(AdbCommandBuilder.GetPropertySubcommand, command[3]);
        Assert.Equal(property, command[4]);
    }

    private static IReadOnlyList<IReadOnlyList<string>> TargetCommands(AndroidEndpoint endpoint)
        =>
        [
            AdbCommandBuilder.BuildGetStateArguments(endpoint),
            AdbCommandBuilder.BuildBootCompletedArguments(endpoint),
            AdbCommandBuilder.BuildAndroidReleaseArguments(endpoint),
            AdbCommandBuilder.BuildSdkLevelArguments(endpoint),
            AdbCommandBuilder.BuildPackagePathArguments(endpoint, Package),
            AdbCommandBuilder.BuildLauncherQueryArguments(endpoint, Package),
            AdbCommandBuilder.BuildProcessListArguments(endpoint),
            AdbCommandBuilder.BuildWindowDumpArguments(endpoint),
            AdbCommandBuilder.BuildStartGameArguments(endpoint, Launcher),
            AdbCommandBuilder.BuildForceStopGameArguments(endpoint, Package),
        ];

    private static IReadOnlyList<IReadOnlyList<string>> AllCommands(AndroidEndpoint endpoint)
        =>
        [
            .. TargetCommands(endpoint),
            AdbCommandBuilder.BuildVersionArguments(),
            AdbCommandBuilder.BuildConnectArguments(endpoint),
        ];
}
