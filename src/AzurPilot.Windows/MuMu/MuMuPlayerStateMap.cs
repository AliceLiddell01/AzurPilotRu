using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Правило отображения ответа control surface в host-side состояние экземпляра.
/// </summary>
/// <remarks>
/// <para>
/// Единственный владелец правила на платформенной стороне. Отображение fail-closed и даёт ровно три
/// значения словаря <see cref="MuMuLifecycleState"/>: <see cref="MuMuLifecycleState.Running"/>,
/// <see cref="MuMuLifecycleState.Stopped"/> и <see cref="MuMuLifecycleState.Unknown"/>.
/// </para>
/// <para>
/// Успешным запуском признаётся только комбинация «<c>player_state</c> равен
/// <see cref="FinishedPlayerState"/> и процесс, и Android запущены». Доказанной остановкой — только
/// комбинация «процесс не запущен, Android не запущен и <c>player_state</c> отсутствует»: у
/// остановленного экземпляра провайдер не сообщает это поле вовсе. Любая другая комбинация даёт
/// <see cref="MuMuLifecycleState.Unknown"/>, а не догадку.
/// </para>
/// <para>
/// Наблюдавшиеся на реальной установке значения <c>player_state</c>: <c>starting_renderer</c>,
/// <c>starting_vm</c>, <c>starting_rom</c>, <c>start_finished</c> и отсутствие поля у остановленного
/// экземпляра. Перечень наблюдавшихся значений — доказательство формы, а не allowlist: любое другое
/// значение при запущенном процессе даёт <see cref="MuMuLifecycleState.Unknown"/>, то есть не считается
/// ни запущенным, ни остановленным.
/// </para>
/// <para>
/// Ненулевой код ошибки провайдера (<c>error_code</c> или <c>launch_err_code</c>) отменяет
/// авторитетность ответа: такой ответ даёт <see cref="MuMuLifecycleState.Unknown"/>.
/// </para>
/// </remarks>
public static class MuMuPlayerStateMap
{
    /// <summary>Значение <c>player_state</c>, соответствующее завершённому запуску.</summary>
    public const string FinishedPlayerState = "start_finished";

    /// <summary>Отображает поля ответа control surface в host-side состояние экземпляра.</summary>
    /// <param name="isProcessStarted">Значение поля <c>is_process_started</c>.</param>
    /// <param name="isAndroidStarted">Значение поля <c>is_android_started</c>.</param>
    /// <param name="rawPlayerState">
    /// Значение поля <c>player_state</c> или <see langword="null"/>, если поле отсутствует.
    /// </param>
    /// <param name="hasProviderError">Признак ненулевого кода ошибки провайдера в том же ответе.</param>
    /// <returns>Доказанное состояние экземпляра или <see cref="MuMuLifecycleState.Unknown"/>.</returns>
    public static MuMuLifecycleState Map(
        bool isProcessStarted,
        bool isAndroidStarted,
        string? rawPlayerState,
        bool hasProviderError)
    {
        if (hasProviderError)
        {
            return MuMuLifecycleState.Unknown;
        }

        if (isProcessStarted && isAndroidStarted)
        {
            return string.Equals(rawPlayerState, FinishedPlayerState, StringComparison.Ordinal)
                ? MuMuLifecycleState.Running
                : MuMuLifecycleState.Unknown;
        }

        if (!isProcessStarted && !isAndroidStarted)
        {
            return rawPlayerState is null ? MuMuLifecycleState.Stopped : MuMuLifecycleState.Unknown;
        }

        return MuMuLifecycleState.Unknown;
    }
}
