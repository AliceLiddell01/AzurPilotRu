namespace AzurPilot.Core.MuMu;

/// <summary>
/// Единственный владелец чисел времени MuMu lifecycle: интервала опроса, deadline-значений и окна
/// наблюдения эффекта запуска.
/// </summary>
/// <remarks>
/// <para>
/// Числовых литералов таймаута вне этого типа нет — ни в orchestration, ни в тестах: и код, и проверки
/// берут значения отсюда, иначе deadline и интервал опроса разъезжались бы между вызывающими сторонами.
/// </para>
/// <para>
/// Deadline ограничивает операцию целиком: у <see cref="MuMuLifecycleOperation.Restart"/> фаза
/// остановки и фаза запуска укладываются в <see cref="RestartDeadline"/> вместе.
/// </para>
/// <para>
/// Значения по умолчанию пригодны для MuMuPlayer 6.8.0 и переопределяются вызывающей стороной;
/// значения по умолчанию не являются частью контракта postcondition.
/// </para>
/// </remarks>
public sealed record MuMuLifecycleTimings
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan DefaultStartDeadline = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan DefaultStopDeadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultRestartDeadline = TimeSpan.FromSeconds(180);

    private static readonly TimeSpan DefaultLaunchEffectWindow = TimeSpan.FromSeconds(15);

    /// <summary>Значения времени по умолчанию.</summary>
    public static MuMuLifecycleTimings Default { get; } = new();

    /// <summary>Создаёт набор значений времени по умолчанию.</summary>
    public MuMuLifecycleTimings()
        : this(
            DefaultPollInterval,
            DefaultStartDeadline,
            DefaultStopDeadline,
            DefaultRestartDeadline,
            DefaultLaunchEffectWindow)
    {
    }

    /// <summary>Создаёт набор значений времени.</summary>
    /// <param name="pollInterval">Интервал между наблюдениями состояния при bounded polling.</param>
    /// <param name="startDeadline">Deadline операции <see cref="MuMuLifecycleOperation.Start"/>.</param>
    /// <param name="stopDeadline">Deadline операции <see cref="MuMuLifecycleOperation.Stop"/>.</param>
    /// <param name="restartDeadline">Deadline операции <see cref="MuMuLifecycleOperation.Restart"/>.</param>
    /// <param name="launchEffectWindow">
    /// Bounded окно наблюдения эффекта запуска после launch: оно принадлежит единому переходу к
    /// <see cref="MuMuLifecycleState.Running"/> и поэтому одинаково для обычного start и для обоих
    /// вариантов restart.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Любое из значений не положительно: нулевой интервал опроса не даёт времени идти вперёд,
    /// неположительный deadline не оставляет шанса доказать postcondition, а нулевое окно эффекта не
    /// оставляет времени увидеть признак начала запуска.
    /// </exception>
    public MuMuLifecycleTimings(
        TimeSpan pollInterval,
        TimeSpan startDeadline,
        TimeSpan stopDeadline,
        TimeSpan restartDeadline,
        TimeSpan launchEffectWindow)
    {
        PollInterval = RequirePositive(pollInterval, nameof(pollInterval));
        StartDeadline = RequirePositive(startDeadline, nameof(startDeadline));
        StopDeadline = RequirePositive(stopDeadline, nameof(stopDeadline));
        RestartDeadline = RequirePositive(restartDeadline, nameof(restartDeadline));
        LaunchEffectWindow = RequirePositive(launchEffectWindow, nameof(launchEffectWindow));
    }

    /// <summary>Интервал между наблюдениями состояния при bounded polling.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>Deadline операции <see cref="MuMuLifecycleOperation.Start"/>.</summary>
    public TimeSpan StartDeadline { get; }

    /// <summary>Deadline операции <see cref="MuMuLifecycleOperation.Stop"/>.</summary>
    public TimeSpan StopDeadline { get; }

    /// <summary>Deadline операции <see cref="MuMuLifecycleOperation.Restart"/>.</summary>
    public TimeSpan RestartDeadline { get; }

    /// <summary>
    /// Bounded окно наблюдения эффекта запуска после launch: оно принадлежит единому переходу к
    /// <see cref="MuMuLifecycleState.Running"/> и поэтому одинаково для обычного start и для обоих
    /// вариантов restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Эффектом считается признак начала запуска, а не достижение <see cref="MuMuLifecycleState.Running"/>.
    /// Значение выведено из наблюдений на реальной установке MuMuPlayer 6.8.0: успешный запуск проявляет
    /// себя промежуточными состояниями (<c>player_state</c> одного из <c>starting_*</c> либо
    /// <c>is_process_started</c>) за секунды и доходит до <c>Running</c> за 8.7–10.7 s, а молча не
    /// сработавший launch не меняет состояние вообще: наблюдённые отказы держались 180.0 и 180.4 s, то
    /// есть весь <see cref="RestartDeadline"/>. Гонка вызывается не операцией restart, а отправкой launch
    /// вскоре после остановки, поэтому окно относится к launch, а не к композиции restart.
    /// </para>
    /// <para>
    /// 15 s — примерно 1.4 самого медленного наблюдённого успешного запуска (10.7 s) и в двенадцать раз
    /// меньше <see cref="StartDeadline"/>, поэтому единственный повтор launch успевает уложиться в
    /// оставшуюся часть deadline операции (для restart при значениях по умолчанию — не менее 165 s, для
    /// start — не менее 105 s), а ложный повтор из-за случайной задержки маловероятен: окно вмещает около
    /// тридцати наблюдений.
    /// </para>
    /// <para>
    /// Окно не является deadline и не продлевает его: оно измеряется от момента launch и живёт внутри
    /// deadline операции и того же cancellation contract.
    /// </para>
    /// </remarks>
    public TimeSpan LaunchEffectWindow { get; }

    /// <summary>Возвращает deadline запрошенной lifecycle-операции.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <returns>Deadline операции целиком.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Операция не определена.</exception>
    public TimeSpan DeadlineFor(MuMuLifecycleOperation operation) => operation switch
    {
        MuMuLifecycleOperation.Start => StartDeadline,
        MuMuLifecycleOperation.Stop => StopDeadline,
        MuMuLifecycleOperation.Restart => RestartDeadline,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation),
            operation,
            "Deadline lifecycle-операции MuMu не определён."),
    };

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName)
        => value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Значение времени MuMu lifecycle должно быть положительным.");
}
