using System.Collections.Concurrent;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Process-local координация lifecycle-mutation по identity экземпляра MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Одновременные mutation одного и того же экземпляра запрещены: аренда, полученная через
/// <see cref="AcquireAsync"/>, удерживается на время начального наблюдения, mutation и bounded polling,
/// поэтому две lifecycle-операции над одним экземпляром не пересекаются. Разные экземпляры
/// координируются независимо и не блокируют друг друга.
/// </para>
/// <para>
/// Координация ограничена процессом: distributed lock и внешнее state-хранилище не используются. Чтобы
/// гарантия действовала для всего процесса, gate регистрируется в DI как singleton — тогда все
/// потребители одного application host делят одну координацию.
/// </para>
/// <para>
/// Неиспользуемые записи не накапливаются: когда последняя аренда экземпляра освобождена, запись
/// удаляется, поэтому число отслеживаемых identity не растёт вместе с числом встреченных экземпляров.
/// </para>
/// </remarks>
public sealed class MuMuInstanceMutationGate
{
    private readonly ConcurrentDictionary<MuMuInstanceId, Entry> _entries = new();

    /// <summary>Число instance id, для которых сейчас удерживается координация.</summary>
    /// <value>
    /// Возвращается к нулю, когда освобождены все аренды: это наблюдаемое доказательство очистки
    /// неиспользуемых записей.
    /// </value>
    public int TrackedInstanceCount => _entries.Count;

    /// <summary>Ожидает освобождения координации экземпляра и занимает её.</summary>
    /// <param name="instanceId">Identity экземпляра.</param>
    /// <param name="cancellationToken">Запрос отмены ожидания.</param>
    /// <returns>
    /// Аренда координации. Освобождение аренды (ровно один раз) разрешает следующую mutation того же
    /// экземпляра.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Ожидание координации отменено: аренда не выдана, запись экземпляра освобождена.
    /// </exception>
    public async ValueTask<IDisposable> AcquireAsync(MuMuInstanceId instanceId, CancellationToken cancellationToken)
    {
        Entry entry = Rent(instanceId);
        bool acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return new Lease(this, instanceId, entry);
        }
        finally
        {
            if (!acquired)
            {
                Return(instanceId, entry);
            }
        }
    }

    private Entry Rent(MuMuInstanceId instanceId)
    {
        while (true)
        {
            Entry entry = _entries.GetOrAdd(instanceId, static _ => new Entry());
            lock (entry)
            {
                if (entry.Retired)
                {
                    // Запись уже освобождается другой арендой: берём актуальную запись заново, иначе
                    // новая аренда могла бы остаться на освобождаемом семафоре.
                    continue;
                }

                entry.RentCount++;
                return entry;
            }
        }
    }

    private void Return(MuMuInstanceId instanceId, Entry entry)
    {
        lock (entry)
        {
            entry.RentCount--;
            if (entry.RentCount > 0)
            {
                return;
            }

            // Последняя аренда: остальные аренды уже освободили семафор (они делают это до возврата
            // записи), поэтому ожидающих на нём нет и запись можно снять.
            entry.Retired = true;
            _ = _entries.TryRemove(new KeyValuePair<MuMuInstanceId, Entry>(instanceId, entry));
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int RentCount { get; set; }

        internal bool Retired { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly MuMuInstanceMutationGate _owner;
        private readonly MuMuInstanceId _instanceId;
        private readonly Entry _entry;
        private int _released;

        internal Lease(MuMuInstanceMutationGate owner, MuMuInstanceId instanceId, Entry entry)
        {
            _owner = owner;
            _instanceId = instanceId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _ = _entry.Semaphore.Release();
            _owner.Return(_instanceId, _entry);
        }
    }
}
