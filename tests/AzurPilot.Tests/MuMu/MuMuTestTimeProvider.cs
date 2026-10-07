namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Минимальный управляемый <see cref="TimeProvider"/> для проверок MuMu lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Время не идёт само: часы продвигаются ровно на запрошенную задержку в момент создания таймера,
/// поэтому bounded polling действительно доходит до deadline, но тест не ждёт wall-clock и не использует
/// fixed sleeps.
/// </para>
/// <para>
/// Поддерживается форма таймера, которую создаёт
/// <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>: однократная задержка с бесконечным
/// периодом. Периодический таймер повторяется только при положительном периоде.
/// </para>
/// </remarks>
internal sealed class MuMuTestTimeProvider : TimeProvider
{
    private readonly Lock _sync = new();

    private DateTimeOffset _utcNow = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Частота timestamp: один тик часов на тик <see cref="DateTimeOffset"/>.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Текущее управляемое время.</summary>
    /// <returns>Время, продвинутое созданными таймерами.</returns>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    /// <summary>Текущий управляемый timestamp.</summary>
    /// <returns>Тики управляемого времени.</returns>
    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _utcNow.Ticks;
        }
    }

    /// <summary>Создаёт таймер, который продвигает управляемое время на свою задержку.</summary>
    /// <param name="callback">Callback таймера.</param>
    /// <param name="state">Состояние callback-а.</param>
    /// <param name="dueTime">Задержка до срабатывания.</param>
    /// <param name="period">Период повторения; <see cref="Timeout.InfiniteTimeSpan"/> — без повторов.</param>
    /// <returns>Созданный таймер.</returns>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        MuMuTestTimer timer = new(this, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    private void Advance(TimeSpan delta)
    {
        lock (_sync)
        {
            _utcNow += delta;
        }
    }

    private sealed class MuMuTestTimer : ITimer
    {
        private readonly MuMuTestTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        internal MuMuTestTimer(MuMuTestTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                if (dueTime < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(dueTime),
                        dueTime,
                        "Задержка таймера не может быть отрицательной.");
                }

                // Управляемое время продвигается на задержку сразу: именно это делает deadline
                // достижимым без wall-clock ожидания.
                _owner.Advance(dueTime);
            }

            _ = ThreadPool.QueueUserWorkItem(static state => ((MuMuTestTimer)state!).Fire(), this);
            return true;
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _disposed = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Fire()
        {
            TimeSpan period;
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return;
                }

                period = _period;
            }

            _callback(_state);

            if (period > TimeSpan.Zero)
            {
                _ = Change(period, period);
            }
        }
    }
}
