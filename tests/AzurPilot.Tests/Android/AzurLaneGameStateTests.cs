using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства вывода состояния игры Azur Lane из независимых наблюдённых фактов.
/// </summary>
/// <remarks>
/// <para>
/// Правило вывода проверяется у его единственного владельца
/// <see cref="AzurLaneGameStateService.DeriveState"/>, а наблюдение — на подменяемой host-границе:
/// проверки не повторяют правило состояния и не заводят отдельный fake device service.
/// </para>
/// <para>
/// Передний план проверяется пакетом, а не launcher-компонентом: после запуска launcher-компонента на
/// переднем плане может оказаться другая activity того же пакета, поэтому сравнение компонентов
/// postcondition не доказывало бы.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AzurLaneGameStateTests
{
    [Theory(DisplayName = "Состояние игры выводится из доказанных фактов")]
    [InlineData(false, false, false, AzurLaneGameState.NotInstalled)]
    [InlineData(true, false, false, AzurLaneGameState.Stopped)]
    [InlineData(true, true, false, AzurLaneGameState.Background)]
    [InlineData(true, false, true, AzurLaneGameState.Foreground)]
    [InlineData(true, true, true, AzurLaneGameState.Foreground)]
    [InlineData(false, true, false, AzurLaneGameState.Unknown)]
    [InlineData(false, false, true, AzurLaneGameState.Unknown)]
    [InlineData(false, true, true, AzurLaneGameState.Unknown)]
    [InlineData(null, null, null, AzurLaneGameState.Unknown)]
    [InlineData(null, false, false, AzurLaneGameState.Unknown)]
    [InlineData(true, null, false, AzurLaneGameState.Unknown)]
    [InlineData(true, true, null, AzurLaneGameState.Unknown)]
    [InlineData(true, false, null, AzurLaneGameState.Unknown)]
    [InlineData(false, null, false, AzurLaneGameState.NotInstalled)]
    [InlineData(false, false, null, AzurLaneGameState.NotInstalled)]
    public void StateFollowsProvenFacts(
        bool? installed,
        bool? processRunning,
        bool? foreground,
        AzurLaneGameState expected)
        => Assert.Equal(
            expected,
            AzurLaneGameStateService.DeriveState(new AzurLaneGameFacts(installed, processRunning, foreground)));

    [Fact(DisplayName = "Недоказанный факт не достраивается ни в наличие, ни в отсутствие")]
    public void UnprovenFactIsNotGuessed()
    {
        // Каждый недоказанный факт (null) по отдельности оставляет состояние недоказанным.
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(new AzurLaneGameFacts(null, true, true)));
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(new AzurLaneGameFacts(true, null, true)));
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(new AzurLaneGameFacts(true, null, null)));
    }

    [Fact(DisplayName = "Вывод состояния требует фактов, а не значения по умолчанию")]
    public void DeriveStateRequiresFacts()
        => _ = Assert.Throws<ArgumentNullException>(() => AzurLaneGameStateService.DeriveState(null!));

    [Fact(DisplayName = "Отсутствие пакета даёт not_installed без наблюдения процессов и переднего плана")]
    public void AbsentPackageGivesNotInstalled()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetNotInstalled();

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint);

        Assert.True(result.IsSuccess);

        AzurLaneGameObservation observation = result.Value!;

        Assert.Equal(AzurLaneGameState.NotInstalled, observation.State);
        Assert.Equal(new AzurLaneGameFacts(false, false, false), observation.Facts);
        Assert.Contains("state=not_installed", observation.Evidence, StringComparison.Ordinal);

        // Факт об отсутствующем пакете не является фактом о его процессах.
        Assert.Empty(context.Host.ProcessRequests);
        Assert.Empty(context.Host.ForegroundRequests);
    }

    [Fact(DisplayName = "Установленная игра без наблюдённого процесса даёт stopped")]
    public void InstalledWithoutProcessesGivesStopped()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Stopped, result.Value!.State);
        Assert.False(result.Value!.Facts.ProcessRunning);
        Assert.False(result.Value!.Facts.Foreground);
        Assert.Contains("state=stopped", result.Value!.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Наблюдённые процессы exact package без переднего плана дают background")]
    public void RunningProcessesWithoutForegroundGiveBackground()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242, 4243);

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint);

        Assert.True(result.IsSuccess);

        AzurLaneGameObservation observation = result.Value!;

        Assert.Equal(AzurLaneGameState.Background, observation.State);
        Assert.True(observation.Facts.ProcessRunning);
        Assert.False(observation.Facts.Foreground);
        Assert.Contains("state=background", observation.Evidence, StringComparison.Ordinal);

        // Наблюдение процессов адресовано ровно пакету игры на точном endpoint-е.
        Assert.All(
            context.Host.ProcessRequests,
            request => Assert.Equal(AndroidTestContext.Package, request.Package));
        Assert.All(
            context.Host.ProcessRequests,
            request => Assert.Equal(AndroidTestContext.Endpoint, request.Endpoint));
    }

    [Fact(DisplayName = "Передний план игры доказывается пакетом, а не launcher-компонентом")]
    public void ForegroundIsProvenByPackageNotByComponent()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground("com.manjuu.azurlane.MainActivity");

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint);

        Assert.True(result.IsSuccess);

        AzurLaneGameObservation observation = result.Value!;

        Assert.Equal(AzurLaneGameState.Foreground, observation.State);
        Assert.True(observation.Facts.Foreground);

        // Компонент переднего плана отличается от launcher-компонента, но пакет тот же: состояние
        // доказано пакетом.
        Assert.NotNull(device.Foreground.Component);
        Assert.Equal(AzurLaneProduct.Package, device.Foreground.Component!.Package.ToString());
        Assert.NotEqual(device.Launcher.Component!.Flattened, device.Foreground.Component.Flattened);
    }

    [Fact(DisplayName = "Отказ host-а при наблюдении состояния пробрасывается без изменений")]
    public void HostFailureIsPropagated()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        ApplicationFailure failure = new()
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = "test-failure",
        };
        context.Host.ForegroundHandler = _ =>
            ApplicationResult<AndroidForegroundObservation>.Failure(failure);

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint);

        Assert.True(result.IsFailure);
        Assert.Equal(failure, result.FailureInfo!);
    }

    [Fact(DisplayName = "Наблюдение состояния игры ничего не меняет и не координирует mutation")]
    public void ObservationIsReadOnly()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();

        Assert.True(context.GameState.ObserveAsync(AndroidTestContext.Endpoint).IsSuccess);

        Assert.Empty(context.Host.ConnectRequests);
        Assert.Empty(context.Host.MutationRequests);
        Assert.Empty(context.Host.LauncherRequests);
        Assert.Empty(context.Host.AdbDiscoveryRequests);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Наблюдение фактов адресует ровно пакет игры Global/EN")]
    public void FactsAddressProductPackage()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground();

        Assert.Equal(AzurLaneProduct.Package, AzurLaneGameStateService.Package.ToString());

        ApplicationResult<AzurLaneGameFacts> facts = context.GameState.ObserveFactsAsync(AndroidTestContext.Endpoint);

        Assert.True(facts.IsSuccess);
        Assert.Equal(new AzurLaneGameFacts(true, true, false), facts.Value!);

        Assert.All(
            context.Host.PackageRequests,
            request => Assert.Equal(AndroidTestContext.Package, request.Package));
        Assert.All(
            context.Host.PackageRequests,
            request => Assert.Equal(AndroidTestContext.Endpoint, request.Endpoint));
        Assert.Empty(context.Host.LauncherRequests);
    }

    [Fact(DisplayName = "Evidence наблюдения состояния игры ограничен и однострочен")]
    public void EvidenceIsBounded()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();

        AzurLaneGameObservation observation =
            context.GameState.ObserveAsync(AndroidTestContext.Endpoint).Value!;

        Assert.InRange(observation.Evidence.Length, 1, BoundedDiagnosticText.MaxLength);
        Assert.DoesNotContain("\n", observation.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", observation.Evidence, StringComparison.Ordinal);
    }
}
