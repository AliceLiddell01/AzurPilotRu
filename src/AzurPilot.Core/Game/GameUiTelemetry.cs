using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Core.Game;

/// <summary>
/// Собственная работа бота за интервал. Observe включает захват, распознавание и проверку target;
/// Capture включает ADB-команду и декодирование; поля-части не складываются с объемлющими повторно.
/// </summary>
public sealed record GameUiWork(int Captures, double ObserveMs, double CaptureMs, double CaptureCommandMs, double MatchMs,
    int ViewportChecks, double ViewportMs, int Waits, double WaitMs, int Taps, double TapMs)
{
    /// <summary>Активная работа без пассивных пауз: наблюдения, проверки viewport и ввод.</summary>
    public double BotWorkMs => Math.Round(ObserveMs + ViewportMs + TapMs, 1);
}

/// <summary>Встретилась ли фаза в данном выполнении.</summary>
public enum GameUiPhaseStatus
{
    /// <summary>Фаза наблюдалась; времена относятся к её посещениям.</summary>
    Observed,
    /// <summary>Фаза не встретилась; это не нулевая длительность и не успешное прохождение.</summary>
    NotObserved,
}

/// <summary>
/// Фаза на монотонной шкале операции: миллисекунды от начала операции. Elapsed — сумма посещений,
/// равная времени, пока бот видел это состояние; Work — доля собственной работы бота внутри фазы.
/// </summary>
public sealed record GameUiPhase(string Id, GameUiPhaseStatus Status, double? EnteredAtMs, double? ExitedAtMs,
    double? ElapsedMs, int Visits, string? EndReason, GameUiWork? Work)
{
    /// <summary>Фаза, которая не встретилась.</summary>
    public static GameUiPhase NotObserved(string id)
    {
        return new(id, GameUiPhaseStatus.NotObserved, null, null, null, 0, null, null);
    }
}

/// <summary>Накопитель стоимости и фаз одного выполнения; хранит только агрегаты, без кадров и пикселей.</summary>
internal sealed class GameUiMeter(IGameUiHost host, TimeProvider clock, long origin)
{
    private readonly Accumulator _total = new();
    private Accumulator? _pending;
    private Accumulator? _action;
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PhaseAccumulator> _phases = new(StringComparer.Ordinal);
    private PhaseAccumulator? _current;

    internal TimeProvider Clock => clock;
    internal GameUiWork Total => _total.Snapshot();

    /// <summary>Закрывает текущую фазу и открывает новую в указанный момент.</summary>
    internal void Enter(string phaseId, long timestamp, string? previousEndReason = null)
    {
        if (_current is not null && string.Equals(_current.Id, phaseId, StringComparison.Ordinal))
        {
            Flush();
            return;
        }

        Accumulator? pending = _pending;
        _pending = null;
        Exit(timestamp, previousEndReason ?? "next:" + phaseId);
        _pending = pending;
        if (!_phases.TryGetValue(phaseId, out PhaseAccumulator? phase))
        {
            phase = new(phaseId) { FirstEntered = timestamp };
            _phases.Add(phaseId, phase);
            _order.Add(phaseId);
        }

        phase.Visits++;
        phase.Entered = timestamp;
        _current = phase;
        Flush();
    }

    /// <summary>Закрывает текущую фазу с причиной завершения.</summary>
    internal void Exit(long timestamp, string reason)
    {
        Flush();
        if (_current is null)
        {
            return;
        }

        _current.Elapsed += clock.GetElapsedTime(_current.Entered, Math.Max(_current.Entered, timestamp));
        _current.LastExited = Math.Max(_current.Entered, timestamp);
        _current.EndReason = reason;
        _current = null;
    }

    /// <summary>Все известные фазы: встреченные — в порядке первого входа, затем невстреченные.</summary>
    internal IReadOnlyList<GameUiPhase> Phases(IEnumerable<string> known)
    {
        List<GameUiPhase> result = [.. _order.Select(id => _phases[id]).Select(p => new GameUiPhase(p.Id, GameUiPhaseStatus.Observed,
            Milliseconds(clock.GetElapsedTime(origin, p.FirstEntered)), Milliseconds(clock.GetElapsedTime(origin, p.LastExited ?? p.Entered)),
            Milliseconds(p.Elapsed), p.Visits, p.EndReason, p.Work.Snapshot()))];
        result.AddRange(known.Distinct(StringComparer.Ordinal).Where(id => !_phases.ContainsKey(id)).Select(GameUiPhase.NotObserved));
        return result;
    }

    /// <summary>
    /// Наблюдение; при deferPhase стоимость кадра относится к фазе, которую откроет его классификация.
    /// </summary>
    internal async Task<ApplicationResult<GameUiObservation>> ObserveAsync(MuMuInstanceId instanceId, AndroidEndpoint endpoint,
        IReadOnlyList<string> assets, CancellationToken cancellationToken, bool deferPhase = false)
    {
        long started = clock.GetTimestamp();
        Flush();
        _pending = deferPhase ? new() : null;
        try
        {
            ApplicationResult<GameUiObservation> result = await host.ObserveAsync(instanceId, endpoint, assets, cancellationToken)
                .ConfigureAwait(false);
            GameObservationCost? cost = result.IsSuccess ? result.Value!.Cost : null;
            Record(a =>
            {
                a.Captures++;
                a.Capture += cost?.Capture ?? TimeSpan.Zero;
                a.CaptureCommand += cost?.CaptureCommand ?? TimeSpan.Zero;
                a.Match += cost?.Match ?? TimeSpan.Zero;
            });
            return result;
        }
        finally
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            Record(a => a.Observe += elapsed);
        }
    }

    internal async Task<ApplicationResult<bool>> ValidateAsync(GameFrameProvenance frame, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        try
        {
            return await host.ValidateInputAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            Record(a => { a.ViewportChecks++; a.Viewport += elapsed; });
        }
    }

    internal async Task<ApplicationResult<AndroidCommandOutcome>> TapAsync(GameTap tap, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        try
        {
            return await host.TapAsync(tap, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            Record(a => { a.Taps++; a.Tap += elapsed; });
        }
    }

    /// <summary>Пауза только на остаток минимального интервала с начала предыдущего захвата.</summary>
    internal async Task PaceAsync(long? previousCapture, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (previousCapture is not long previous)
        {
            return;
        }

        TimeSpan remaining = interval - clock.GetElapsedTime(previous);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        long started = clock.GetTimestamp();
        try
        {
            await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            Record(a => { a.Waits++; a.Wait += elapsed; });
        }
    }

    /// <summary>Начинает отдельный учёт работы одного действия поверх фаз.</summary>
    internal void BeginAction()
    {
        _action = new();
    }

    /// <summary>Завершает учёт работы действия.</summary>
    internal GameUiWork EndAction()
    {
        GameUiWork work = (_action ?? new()).Snapshot();
        _action = null;
        return work;
    }

    internal static double Milliseconds(TimeProvider clock, long from, long to)
    {
        return Milliseconds(clock.GetElapsedTime(from, to));
    }

    private void Record(Action<Accumulator> update)
    {
        update(_total);
        if (_action is not null)
        {
            update(_action);
        }

        if (_pending is not null)
        {
            update(_pending);
        }
        else if (_current is not null)
        {
            update(_current.Work);
        }
    }

    private void Flush()
    {
        if (_pending is not null && _current is not null)
        {
            _current.Work.Add(_pending);
            _pending = null;
        }
    }

    private static double Milliseconds(TimeSpan value)
    {
        return Math.Round(value.TotalMilliseconds, 1);
    }

    private sealed class Accumulator
    {
        internal int Captures;
        internal TimeSpan Observe, Capture, CaptureCommand, Match;
        internal int ViewportChecks;
        internal TimeSpan Viewport;
        internal int Waits;
        internal TimeSpan Wait;
        internal int Taps;
        internal TimeSpan Tap;

        internal void Add(Accumulator other)
        {
            Captures += other.Captures;
            Observe += other.Observe;
            Capture += other.Capture;
            CaptureCommand += other.CaptureCommand;
            Match += other.Match;
            ViewportChecks += other.ViewportChecks;
            Viewport += other.Viewport;
            Waits += other.Waits;
            Wait += other.Wait;
            Taps += other.Taps;
            Tap += other.Tap;
        }

        internal GameUiWork Snapshot()
        {
            return new(Captures, Milliseconds(Observe), Milliseconds(Capture), Milliseconds(CaptureCommand), Milliseconds(Match),
                ViewportChecks, Milliseconds(Viewport), Waits, Milliseconds(Wait), Taps, Milliseconds(Tap));
        }
    }

    private sealed class PhaseAccumulator(string id)
    {
        internal string Id { get; } = id;
        internal long FirstEntered;
        internal long Entered;
        internal long? LastExited;
        internal TimeSpan Elapsed;
        internal int Visits;
        internal string? EndReason;
        internal Accumulator Work { get; } = new();
    }
}
