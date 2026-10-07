namespace AzurPilot.Core.MuMu;

/// <summary>
/// Обнаруженная установка MuMuPlayer.
/// </summary>
/// <remarks>
/// <para>
/// Тип описывает установку как данные: версию и пути, которые сообщил платформенный adapter. Core не
/// знает, где именно установлен MuMuPlayer, не содержит конкретных путей установки и не проверяет
/// существование этих путей — это ответственность реализации <see cref="IMuMuHost"/>.
/// </para>
/// <para>
/// Пути установки — machine-specific данные: они не попадают ни в structured details отказов, ни в
/// логи, где достаточно версии установки.
/// </para>
/// </remarks>
/// <param name="Version">Версия обнаруженного MuMuPlayer.</param>
/// <param name="InstallRoot">Каталог установки, как его сообщил платформенный adapter.</param>
/// <param name="ControlExecutablePath">
/// Путь control utility установки, через которую выполняются lifecycle-операции.
/// </param>
public sealed record MuMuInstallation(string Version, string InstallRoot, string ControlExecutablePath);
