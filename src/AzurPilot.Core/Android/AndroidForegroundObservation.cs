namespace AzurPilot.Core.Android;

/// <summary>
/// Наблюдение foreground на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// Наблюдение отвечает на вопрос «что находится на переднем плане», а не «запущен ли процесс пакета»:
/// запущенный процесс в фоне foreground-ом не является.
/// </para>
/// <para>
/// <see cref="Component"/> заполнен, когда компонент доказан наблюдением, и равен <see langword="null"/>
/// при <see cref="AndroidForegroundStatus.Unknown"/>: недоказанное значение не додумывается. При
/// <see cref="AndroidForegroundStatus.Other"/> компонент — это наблюдённый компонент переднего плана, а
/// не запрошенный.
/// </para>
/// <para>
/// <see cref="Evidence"/> — bounded сведения о наблюдении: полный вывод ADB в них не попадает.
/// </para>
/// </remarks>
/// <param name="Status">Доказанный статус foreground.</param>
/// <param name="Component">Наблюдённый компонент переднего плана либо <see langword="null"/>.</param>
/// <param name="Evidence">Bounded evidence наблюдения, пригодный для диагностики.</param>
public sealed record AndroidForegroundObservation(
    AndroidForegroundStatus Status,
    AndroidComponent? Component,
    string Evidence);
