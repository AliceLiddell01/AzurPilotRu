using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства контракта «не доказано ≠ отсутствует» на уровне наблюдения состояния игры и lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Недоказанное значение (<c>QueryFailed</c>, <c>ProcessIds == null</c>, <c>Unknown</c>) не сворачивается
/// в «отсутствует», «процессов нет» и «не foreground»: наблюдение, не доказавшее факт, даёт честное
/// состояние <see cref="AzurLaneGameState.Unknown"/>, а не <see cref="AzurLaneGameState.NotInstalled"/>,
/// <see cref="AzurLaneGameState.Stopped"/> или <see cref="AzurLaneGameState.Background"/>. Иначе
/// недостижимый target выдавался бы за доказанно остановленную игру, и операция остановки сообщала бы
/// успех без доказательства.
/// </para>
/// <para>
/// Lifecycle-операция, начавшаяся с недоказанного состояния, отказывает кодом
/// <see cref="ApplicationFailure.AzurLaneStateUnknown"/> и не выполняет mutation: отказ описывает
/// «состояние не доказано», а не «игра остановлена».
/// </para>
/// <para>
/// Проверки намеренно выражают ожидание через состояние и через единственное правило вывода
/// <see cref="AzurLaneGameStateService.DeriveState"/>, а не через форму фактов: правило остаётся
/// владельцем вывода состояния при любой форме контракта фактов.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidUnprovenObservationTests
{
    [Fact(DisplayName = "Недоказанный запрос присутствия пакета не выдаётся за отсутствие пакета")]
    public void UnprovenPackageQueryIsNotNotInstalled()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetPackageQueryFailed();

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.Observe(AndroidTestContext.Endpoint);

        Assert.True(
            result.IsSuccess,
            "Недоказанное наблюдение остаётся наблюдением и не подменяется отказом.");
        Assert.Equal(AzurLaneGameState.Unknown, result.Value!.State);
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(result.Value!.Facts));
        Assert.Contains("state=unknown", result.Value!.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Недоказанное наблюдение процессов не выдаётся за отсутствие процессов")]
    public void UnprovenProcessesAreNotStopped()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnprovenProcesses();

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.Observe(AndroidTestContext.Endpoint);

        Assert.True(
            result.IsSuccess,
            "Недоказанное наблюдение остаётся наблюдением и не подменяется отказом.");
        Assert.Equal(AzurLaneGameState.Unknown, result.Value!.State);
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(result.Value!.Facts));
    }

    [Fact(DisplayName = "Недоказанный передний план не выдаётся за «игра не на переднем плане»")]
    public void UnprovenForegroundIsNotBackground()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnknownForeground();

        ApplicationResult<AzurLaneGameObservation> result =
            context.GameState.Observe(AndroidTestContext.Endpoint);

        Assert.True(
            result.IsSuccess,
            "Недоказанное наблюдение остаётся наблюдением и не подменяется отказом.");
        Assert.Equal(AzurLaneGameState.Unknown, result.Value!.State);
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(result.Value!.Facts));
    }

    [Fact(DisplayName = "Наблюдение фактов не подставляет недоказанное значение вместо доказанного факта")]
    public void UnprovenFactsAreNotNegativeFacts()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnprovenProcesses();

        ApplicationResult<AzurLaneGameFacts> facts =
            context.GameState.ObserveFacts(AndroidTestContext.Endpoint);

        Assert.True(
            facts.IsSuccess,
            "Недоказанное наблюдение остаётся наблюдением и не подменяется отказом.");
        Assert.Equal(
            AzurLaneGameState.Unknown,
            AzurLaneGameStateService.DeriveState(facts.Value!));
    }

    [Fact(DisplayName = "Остановка при недоказанном наблюдении не сообщает доказанную остановку")]
    public async Task StopWithUnprovenObservationIsNotAProvenStop()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnprovenProcesses();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(
            result.IsFailure,
            "Отсутствие процессов и переднего плана не доказано, поэтому остановка не доказана.");
        Assert.Equal(ApplicationFailure.AzurLaneStateUnknown, result.FailureInfo!.Code);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Запуск при недоказанном присутствии пакета не выдаётся за отсутствие пакета")]
    public async Task StartWithUnprovenPackageQueryIsNotPackageMissing()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetPackageQueryFailed();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(
            result.IsFailure,
            "Присутствие пакета не доказано, поэтому «пакет не установлен» не доказано.");
        Assert.Equal(ApplicationFailure.AzurLaneStateUnknown, result.FailureInfo!.Code);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Недоказанное присутствие пакета остаётся недоказанным фактом, а не отрицательным")]
    public void UnprovenPackageQueryKeepsFactUnproven()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetPackageQueryFailed();

        ApplicationResult<AzurLaneGameFacts> facts =
            context.GameState.ObserveFacts(AndroidTestContext.Endpoint);

        Assert.True(facts.IsSuccess);

        // null — «не доказано», а не «пакета нет»; процессов у недоказанного пакета не наблюдается.
        Assert.Null(facts.Value!.Installed);
        Assert.Null(facts.Value!.ProcessRunning);
        Assert.Null(facts.Value!.Foreground);
    }

    [Fact(DisplayName = "Недоказанное наблюдение процессов остаётся недоказанным фактом, а не «процессов нет»")]
    public void UnprovenProcessesKeepFactUnproven()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnprovenProcesses();

        ApplicationResult<AzurLaneGameFacts> facts =
            context.GameState.ObserveFacts(AndroidTestContext.Endpoint);

        Assert.True(facts.IsSuccess);
        Assert.True(facts.Value!.Installed);
        Assert.Null(facts.Value!.ProcessRunning);
    }

    [Fact(DisplayName = "Недоказанный передний план остаётся недоказанным фактом, а не «не foreground»")]
    public void UnprovenForegroundKeepsFactUnproven()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetUnknownForeground();

        ApplicationResult<AzurLaneGameFacts> facts =
            context.GameState.ObserveFacts(AndroidTestContext.Endpoint);

        Assert.True(facts.IsSuccess);
        Assert.True(facts.Value!.Installed);
        Assert.Null(facts.Value!.Foreground);
    }
}
