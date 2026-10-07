namespace AzurPilot.Core.Android;

/// <summary>
/// Разрешённый launcher-компонент Android-пакета: пакет и activity.
/// </summary>
/// <remarks>
/// <para>
/// Компонент — это identity launcher-а, а не отображаемая метка: сравнение и адресация выполняются по
/// паре «пакет + activity», и именно эта пара используется для запроса mutation.
/// </para>
/// <para>
/// <see cref="Flattened"/> — каноническая текстовая форма <c>package/activity</c>, которую понимает
/// платформенная сторона и которую используют структурированные логи и bounded details отказов.
/// </para>
/// </remarks>
/// <param name="Package">Идентификатор пакета, которому принадлежит компонент.</param>
/// <param name="Activity">Полное имя activity внутри пакета.</param>
/// <param name="Flattened">Каноническая форма <c>package/activity</c>.</param>
public sealed record AndroidComponent(AndroidPackageId Package, string Activity, string Flattened);
