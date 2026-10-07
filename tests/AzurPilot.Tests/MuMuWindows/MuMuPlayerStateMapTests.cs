using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства правила отображения ответа control surface в host-side состояние экземпляра.
/// </summary>
/// <remarks>
/// Проверяются все значения <c>player_state</c>, наблюдавшиеся на реальной установке, и все комбинации
/// полей, которые не дают права ни на <see cref="MuMuLifecycleState.Running"/>, ни на
/// <see cref="MuMuLifecycleState.Stopped"/>.
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuPlayerStateMapTests
{
    [Theory(DisplayName = "Завершённый запуск с запущенным Android даёт Running")]
    [InlineData(MuMuPlayerStateMap.FinishedPlayerState)]
    public void FinishedStartGivesRunning(string rawPlayerState)
    {
        MuMuLifecycleState state = MuMuPlayerStateMap.Map(
            isProcessStarted: true,
            isAndroidStarted: true,
            rawPlayerState,
            hasProviderError: false);

        Assert.Equal(MuMuLifecycleState.Running, state);
    }

    [Fact(DisplayName = "Остановленный экземпляр без player_state даёт Stopped")]
    public void MissingPlayerStateWithStoppedProcessGivesStopped()
    {
        MuMuLifecycleState state = MuMuPlayerStateMap.Map(
            isProcessStarted: false,
            isAndroidStarted: false,
            rawPlayerState: null,
            hasProviderError: false);

        Assert.Equal(MuMuLifecycleState.Stopped, state);
    }

    [Theory(DisplayName = "Переходные значения player_state не дают ни Running, ни Stopped")]
    [InlineData("starting_renderer")]
    [InlineData("starting_vm")]
    [InlineData("starting_rom")]
    [InlineData("start_failed")]
    [InlineData("START_FINISHED")]
    [InlineData("")]
    public void TransitionalPlayerStatesAreNotAuthoritative(string rawPlayerState)
    {
        MuMuLifecycleState whileProcessRuns = MuMuPlayerStateMap.Map(
            isProcessStarted: true,
            isAndroidStarted: false,
            rawPlayerState,
            hasProviderError: false);

        MuMuLifecycleState whileProcessStopped = MuMuPlayerStateMap.Map(
            isProcessStarted: false,
            isAndroidStarted: false,
            rawPlayerState,
            hasProviderError: false);

        Assert.Equal(MuMuLifecycleState.Unknown, whileProcessRuns);
        Assert.Equal(MuMuLifecycleState.Unknown, whileProcessStopped);
    }

    [Theory(DisplayName = "Противоречивая комбинация полей даёт Unknown")]
    [InlineData(true, true, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, true, MuMuPlayerStateMap.FinishedPlayerState)]
    [InlineData(true, true, "starting_vm")]
    public void ConflictingFieldCombinationGivesUnknown(bool isProcessStarted, bool isAndroidStarted, string? raw)
    {
        MuMuLifecycleState state = MuMuPlayerStateMap.Map(
            isProcessStarted,
            isAndroidStarted,
            raw,
            hasProviderError: false);

        Assert.Equal(MuMuLifecycleState.Unknown, state);
    }

    [Fact(DisplayName = "Ненулевой код ошибки провайдера отменяет авторитетность ответа")]
    public void ProviderErrorCancelsAuthoritativeness()
    {
        MuMuLifecycleState running = MuMuPlayerStateMap.Map(
            isProcessStarted: true,
            isAndroidStarted: true,
            MuMuPlayerStateMap.FinishedPlayerState,
            hasProviderError: true);

        MuMuLifecycleState stopped = MuMuPlayerStateMap.Map(
            isProcessStarted: false,
            isAndroidStarted: false,
            rawPlayerState: null,
            hasProviderError: true);

        Assert.Equal(MuMuLifecycleState.Unknown, running);
        Assert.Equal(MuMuLifecycleState.Unknown, stopped);
    }
}
