namespace AzurPilot.Core.Android;

/// <summary>
/// Результат разрешения launcher-компонента пакета на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Component"/> заполнен только при <see cref="AndroidLauncherResolutionStatus.Resolved"/>:
/// при <see cref="AndroidLauncherResolutionStatus.Missing"/>, <see
/// cref="AndroidLauncherResolutionStatus.Ambiguous"/> и <see
/// cref="AndroidLauncherResolutionStatus.QueryFailed"/> он равен <see langword="null"/>, потому что
/// компонент не доказан. Первый попавшийся launcher при неоднозначности не выбирается.
/// </para>
/// <para>
/// <see cref="MatchingComponentCount"/> — сколько компонентов совпало с запросом; значение осмысленно и
/// при неоднозначности, а при неудачном запросе равно нулю.
/// </para>
/// <para>
/// <see cref="Evidence"/> — bounded сведения о том, чем подтверждён статус: полный вывод ADB в них не
/// попадает.
/// </para>
/// </remarks>
/// <param name="Status">Статус разрешения launcher-компонента.</param>
/// <param name="Component">Разрешённый компонент либо <see langword="null"/>, если он не доказан.</param>
/// <param name="MatchingComponentCount">Число компонентов, совпавших с запросом.</param>
/// <param name="Evidence">Bounded evidence разрешения, пригодный для диагностики.</param>
public sealed record AndroidLauncherResolution(
    AndroidLauncherResolutionStatus Status,
    AndroidComponent? Component,
    int MatchingComponentCount,
    string Evidence);
