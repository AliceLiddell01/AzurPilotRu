using System.Reflection;
using AzurPilot.App;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки проекции кодов отказа в коды выхода процесса.
/// </summary>
/// <remarks>
/// Соответствие «application-код отказа → код выхода» принадлежит единственному владельцу
/// <see cref="AzurPilotExitCode"/>: проверки доказывают, что каждый код отказа capability — MuMu, ADB
/// readiness и lifecycle игры — получает явный стабильный ненулевой код, что коды не совпадают друг с
/// другом и что второй каталог кодов не появился.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AzurPilotExitCodeTests
{
    /// <summary>
    /// Префиксы кодов отказа capability в каталоге <see cref="ApplicationFailure"/>: каждый такой код обязан
    /// иметь собственную проекцию, а не общий <see cref="ApplicationFailure.InternalError"/>.
    /// </summary>
    private static readonly string[] CapabilityFailureCodePrefixes = ["mumu_", "android_", "azurlane_"];

    [Theory(DisplayName = "MuMu-код отказа проецируется в свой явный код выхода")]
    [InlineData(ApplicationFailure.MuMuInstallationNotFound, AzurPilotExitCode.MuMuInstallationNotFound)]
    [InlineData(ApplicationFailure.MuMuInstallationAmbiguous, AzurPilotExitCode.MuMuInstallationAmbiguous)]
    [InlineData(ApplicationFailure.MuMuInstanceNotFound, AzurPilotExitCode.MuMuInstanceNotFound)]
    [InlineData(ApplicationFailure.MuMuInstanceAmbiguous, AzurPilotExitCode.MuMuInstanceAmbiguous)]
    [InlineData(ApplicationFailure.MuMuControlSurfaceUnsupported, AzurPilotExitCode.MuMuControlSurfaceUnsupported)]
    [InlineData(
        ApplicationFailure.MuMuLifecyclePostconditionNotMet,
        AzurPilotExitCode.MuMuLifecyclePostconditionNotMet)]
    [InlineData(ApplicationFailure.MuMuLifecycleTimeout, AzurPilotExitCode.MuMuLifecycleTimeout)]
    public void MuMuFailureCodesHaveExplicitExitCodes(string failureCode, int expectedExitCode)
    {
        Assert.NotEqual(AzurPilotExitCode.Success, expectedExitCode);
        Assert.NotEqual(AzurPilotExitCode.InternalError, expectedExitCode);
        Assert.Equal(expectedExitCode, AzurPilotExitCode.FromFailure(Failure(failureCode)));
    }

    [Theory(DisplayName = "Код отказа ADB readiness и lifecycle игры проецируется в свой явный код выхода")]
    [InlineData(ApplicationFailure.AndroidAdbUnavailable, AzurPilotExitCode.AndroidAdbUnavailable)]
    [InlineData(ApplicationFailure.AndroidEndpointUnavailable, AzurPilotExitCode.AndroidEndpointUnavailable)]
    [InlineData(ApplicationFailure.AndroidTransportNotReady, AzurPilotExitCode.AndroidTransportNotReady)]
    [InlineData(ApplicationFailure.AndroidNotReady, AzurPilotExitCode.AndroidNotReady)]
    [InlineData(ApplicationFailure.AzurLanePackageMissing, AzurPilotExitCode.AzurLanePackageMissing)]
    [InlineData(ApplicationFailure.AzurLaneStateUnknown, AzurPilotExitCode.AzurLaneStateUnknown)]
    [InlineData(ApplicationFailure.AzurLaneLauncherUnresolved, AzurPilotExitCode.AzurLaneLauncherUnresolved)]
    [InlineData(ApplicationFailure.AzurLaneLauncherAmbiguous, AzurPilotExitCode.AzurLaneLauncherAmbiguous)]
    [InlineData(
        ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
        AzurPilotExitCode.AzurLaneLifecyclePostconditionNotMet)]
    [InlineData(ApplicationFailure.AzurLaneLifecycleTimeout, AzurPilotExitCode.AzurLaneLifecycleTimeout)]
    public void AndroidAndGameFailureCodesHaveExplicitExitCodes(string failureCode, int expectedExitCode)
    {
        Assert.NotEqual(AzurPilotExitCode.Success, expectedExitCode);
        Assert.NotEqual(AzurPilotExitCode.InternalError, expectedExitCode);
        Assert.Equal(expectedExitCode, AzurPilotExitCode.FromFailure(Failure(failureCode)));
    }

    [Fact(DisplayName = "Новый код выхода получает следующее свободное значение, а опубликованные не меняются")]
    public void NewMuMuExitCodeIsAppendedWithoutRenumbering()
    {
        // Значения кодов выхода — часть контракта startup: уже опубликованные коды остаются на своих
        // номерах, а новый получает следующее свободное значение после них.
        Assert.Equal(7, AzurPilotExitCode.MuMuInstallationNotFound);
        Assert.Equal(8, AzurPilotExitCode.MuMuInstanceNotFound);
        Assert.Equal(9, AzurPilotExitCode.MuMuInstanceAmbiguous);
        Assert.Equal(10, AzurPilotExitCode.MuMuControlSurfaceUnsupported);
        Assert.Equal(11, AzurPilotExitCode.MuMuLifecyclePostconditionNotMet);
        Assert.Equal(12, AzurPilotExitCode.MuMuLifecycleTimeout);
        Assert.Equal(13, AzurPilotExitCode.MuMuInstallationAmbiguous);
    }

    [Fact(DisplayName = "Коды ADB readiness и lifecycle игры добавлены в конец блока без перенумерации")]
    public void AndroidAndGameExitCodesAreAppendedWithoutRenumbering()
    {
        // Значения публикуются впервые, поэтому проверка фиксирует и их порядок, и то, что они не
        // вторглись в уже опубликованный диапазон MuMu-кодов выше.
        Assert.Equal(14, AzurPilotExitCode.AndroidAdbUnavailable);
        Assert.Equal(15, AzurPilotExitCode.AndroidEndpointUnavailable);
        Assert.Equal(16, AzurPilotExitCode.AndroidTransportNotReady);
        Assert.Equal(17, AzurPilotExitCode.AndroidNotReady);
        Assert.Equal(18, AzurPilotExitCode.AzurLanePackageMissing);
        Assert.Equal(19, AzurPilotExitCode.AzurLaneStateUnknown);
        Assert.Equal(20, AzurPilotExitCode.AzurLaneLauncherUnresolved);
        Assert.Equal(21, AzurPilotExitCode.AzurLaneLauncherAmbiguous);
        Assert.Equal(22, AzurPilotExitCode.AzurLaneLifecyclePostconditionNotMet);
        Assert.Equal(23, AzurPilotExitCode.AzurLaneLifecycleTimeout);
    }

    [Fact(DisplayName = "Каждый код отказа capability имеет проекцию, а не общий internal_error")]
    public void EveryCapabilityFailureCodeIsProjected()
    {
        // Набор кодов читается у его владельца ApplicationFailure, поэтому проверка не закрепляет их число
        // и остаётся верной при добавлении нового кода вместе с его проекцией. Префиксы перечисляют все
        // capability, у которых есть собственные коды: новый код Android или Azur Lane без проекции тоже
        // не сможет незаметно стать общим internal_error.
        string[] capabilityCodes = [.. FailureCodeConstants()
            .Where(code => CapabilityFailureCodePrefixes.Any(
                prefix => code.StartsWith(prefix, StringComparison.Ordinal)))];
        Assert.NotEmpty(capabilityCodes);

        Assert.All(capabilityCodes, code =>
        {
            int exitCode = AzurPilotExitCode.FromFailure(Failure(code));
            Assert.NotEqual(AzurPilotExitCode.Success, exitCode);
            Assert.NotEqual(AzurPilotExitCode.InternalError, exitCode);
        });
    }

    [Fact(DisplayName = "Коды выхода уникальны: соответствие живёт ровно в одном месте")]
    public void ExitCodesAreUnique()
    {
        int[] exitCodes = [.. typeof(AzurPilotExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => (int)field.GetRawConstantValue()!)];

        Assert.Contains(AzurPilotExitCode.Success, exitCodes);
        Assert.Equal(exitCodes.Length, exitCodes.Distinct().Count());
    }

    /// <summary>Читает стабильные строковые коды отказа у их владельца.</summary>
    /// <returns>Значения публичных строковых констант <see cref="ApplicationFailure"/>.</returns>
    private static IEnumerable<string> FailureCodeConstants()
        => typeof(ApplicationFailure)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static ApplicationFailure Failure(string code)
        => new() { Code = code, Message = "Отказ, проверяемый проекцией в код выхода." };
}
