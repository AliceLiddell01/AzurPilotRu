namespace AzurPilot.Core.Android;

/// <summary>
/// Независимые наблюдённые факты об игре Azur Lane.
/// </summary>
/// <remarks>
/// <para>
/// Факты не смешиваются: каждый из них доказан своим наблюдением и ни один не выводится из другого.
/// В частности <see cref="ProcessRunning"/> не означает <see cref="Foreground"/>: запущенный процесс
/// может оставаться в фоне, а игра на переднем плане может быть показана без наблюдённого процесса —
/// тогда это два разных факта, а не один.
/// </para>
/// <para>
/// Каждый факт трёхзначен, и <see langword="null"/> означает недоказанность, а не «нет»:
/// <see langword="true"/> — доказанное наличие, <see langword="false"/> — доказанное отсутствие,
/// <see langword="null"/> — наблюдение не дало распознанного ответа, поэтому факт не доказан ни в одну
/// сторону. Сворачивать недоказанность в <see langword="false"/> запрещено: это выдало бы «не удалось
/// наблюдать» за доказанный отрицательный факт, и, например, недостижимая остановка игры сообщала бы
/// успех без доказательства.
/// </para>
/// <para>
/// <see cref="Installed"/> и <see cref="ProcessRunning"/> относятся к одному и тому же пакету
/// (<see cref="AzurLaneProduct.Package"/>): наблюдение про другой пакет фактом об игре не является.
/// </para>
/// </remarks>
/// <param name="Installed">
/// Доказанное присутствие пакета игры: <see langword="true"/> — установлен, <see langword="false"/> —
/// доказанно отсутствует, <see langword="null"/> — присутствие не доказано.
/// </param>
/// <param name="ProcessRunning">
/// Доказанное наличие процесса игры: <see langword="true"/> — процесс наблюдён, <see langword="false"/> —
/// процессов нет, <see langword="null"/> — наблюдение процессов не доказано.
/// </param>
/// <param name="Foreground">
/// Доказанный передний план игры: <see langword="true"/> — игра на переднем плане,
/// <see langword="false"/> — на переднем плане другое, <see langword="null"/> — передний план не доказан.
/// </param>
public sealed record AzurLaneGameFacts(bool? Installed, bool? ProcessRunning, bool? Foreground);
