namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Единственный владелец чисел времени Android readiness и lifecycle игры: интервала опроса и
/// deadline-значений.
/// </summary>
/// <remarks>
/// <para>
/// Числовых литералов таймаута вне этого типа нет — ни в orchestration, ни в тестах: и код, и проверки
/// берут значения отсюда, иначе deadline и интервал опроса разъезжались бы между вызывающими сторонами.
/// </para>
/// <para>
/// У каждой операции один бюджет времени: <see cref="ReadinessDeadline"/> ограничивает достижение
/// готовности Android целиком (ожидание transport и ожидание готовности идут внутри него),
/// <see cref="GameStartDeadline"/> и <see cref="GameStopDeadline"/> — соответствующую фазу lifecycle игры,
/// а <see cref="GameRestartDeadline"/> — перезапуск целиком, то есть обе его фазы вместе.
/// </para>
/// <para>
/// <see cref="TransportConnectDeadline"/> — не отдельный бюджет операции, а внутренняя граница одного
/// bounded ожидания: ожидание готовности transport заканчивается, когда истекло либо оно, либо общий
/// <see cref="ReadinessDeadline"/>, поэтому ожидание transport не может съесть бюджет готовности Android
/// целиком и не может продлить его.
/// </para>
/// <para>
/// Значения по умолчанию пригодны для MuMuPlayer 6.8.0 с Android 15 и переопределяются вызывающей
/// стороной; значения по умолчанию не являются частью контракта postcondition.
/// </para>
/// </remarks>
public sealed record AndroidLifecycleTimings
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan DefaultTransportConnectDeadline = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DefaultReadinessDeadline = TimeSpan.FromSeconds(180);

    private static readonly TimeSpan DefaultGameStartDeadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultGameStopDeadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultGameRestartDeadline = TimeSpan.FromSeconds(150);

    /// <summary>Значения времени по умолчанию.</summary>
    public static AndroidLifecycleTimings Default { get; } = new();

    /// <summary>Создаёт набор значений времени по умолчанию.</summary>
    public AndroidLifecycleTimings()
        : this(
            DefaultPollInterval,
            DefaultTransportConnectDeadline,
            DefaultReadinessDeadline,
            DefaultGameStartDeadline,
            DefaultGameStopDeadline,
            DefaultGameRestartDeadline)
    {
    }

    /// <summary>Создаёт набор значений времени.</summary>
    /// <param name="pollInterval">Интервал между наблюдениями при bounded polling.</param>
    /// <param name="transportConnectDeadline">Граница bounded ожидания готовности ADB transport.</param>
    /// <param name="readinessDeadline">
    /// Deadline достижения готовности Android целиком: ожидание transport и ожидание готовности идут
    /// внутри него.
    /// </param>
    /// <param name="gameStartDeadline">Deadline фазы запуска игры.</param>
    /// <param name="gameStopDeadline">Deadline фазы остановки игры.</param>
    /// <param name="gameRestartDeadline">
    /// Deadline перезапуска игры целиком: фаза остановки и фаза запуска укладываются в него вместе.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Любое из значений не положительно: нулевой интервал опроса не даёт времени идти вперёд, а
    /// неположительный deadline не оставляет шанса доказать postcondition.
    /// </exception>
    public AndroidLifecycleTimings(
        TimeSpan pollInterval,
        TimeSpan transportConnectDeadline,
        TimeSpan readinessDeadline,
        TimeSpan gameStartDeadline,
        TimeSpan gameStopDeadline,
        TimeSpan gameRestartDeadline)
    {
        PollInterval = RequirePositive(pollInterval, nameof(pollInterval));
        TransportConnectDeadline = RequirePositive(transportConnectDeadline, nameof(transportConnectDeadline));
        ReadinessDeadline = RequirePositive(readinessDeadline, nameof(readinessDeadline));
        GameStartDeadline = RequirePositive(gameStartDeadline, nameof(gameStartDeadline));
        GameStopDeadline = RequirePositive(gameStopDeadline, nameof(gameStopDeadline));
        GameRestartDeadline = RequirePositive(gameRestartDeadline, nameof(gameRestartDeadline));
    }

    /// <summary>Интервал между наблюдениями при bounded polling.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>
    /// Граница bounded ожидания готовности ADB transport точного endpoint-а: ожидание заканчивается по
    /// ней либо по общему <see cref="ReadinessDeadline"/>, смотря что истекло раньше.
    /// </summary>
    public TimeSpan TransportConnectDeadline { get; }

    /// <summary>
    /// Deadline достижения готовности Android целиком: ожидание transport и ожидание готовности идут
    /// внутри него, поэтому суммарное время готовности его не превышает.
    /// </summary>
    public TimeSpan ReadinessDeadline { get; }

    /// <summary>Deadline фазы запуска игры.</summary>
    public TimeSpan GameStartDeadline { get; }

    /// <summary>Deadline фазы остановки игры.</summary>
    public TimeSpan GameStopDeadline { get; }

    /// <summary>
    /// Deadline перезапуска игры целиком: фаза остановки и фаза запуска укладываются в него вместе,
    /// поэтому перезапуск не продлевает бюджет остановкой.
    /// </summary>
    public TimeSpan GameRestartDeadline { get; }

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName)
        => value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Значение времени Android readiness и lifecycle игры должно быть положительным.");
}
