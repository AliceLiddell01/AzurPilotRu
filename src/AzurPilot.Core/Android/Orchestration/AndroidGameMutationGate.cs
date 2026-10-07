using System.Collections.Concurrent;

namespace AzurPilot.Core.Android.Orchestration;

/// <summary>
/// Process-local координация mutation игры по паре «точный endpoint + идентификатор пакета».
/// </summary>
/// <remarks>
/// <para>
/// Одновременные mutation одного и того же пакета на одном и том же endpoint-е запрещены: аренда,
/// полученная через <see cref="AcquireAsync"/>, удерживается на время начального наблюдения, mutation и
/// bounded polling, поэтому две lifecycle-операции над одной игрой не пересекаются и evidence не
/// становится неоднозначным. Разные endpoint-ы и разные пакеты координируются независимо и не
/// блокируют друг друга.
/// </para>
/// <para>
/// Read-only наблюдения координацию не запрашивают: <see cref="AzurLaneGameStateService"/> и read-only
/// путь готовности Android работают без аренды, потому что они ничего не меняют.
/// </para>
/// <para>
/// Это отдельная координация, а не переиспользование gate MuMu host lifecycle: тот координирует mutation
/// экземпляра MuMu по его identity, а здесь координируется mutation игры внутри Android. Общая
/// координация смешала бы разные предметы и позволила бы одной операции заблокировать другую без причины.
/// </para>
/// <para>
/// Координация ограничена процессом: distributed lock и внешнее state-хранилище не используются. Чтобы
/// гарантия действовала для всего процесса, gate регистрируется в DI как singleton — тогда все
/// потребители одного application host делят одну координацию.
/// </para>
/// <para>
/// Неиспользуемые записи не накапливаются: когда последняя аренда пары освобождена, запись удаляется,
/// поэтому число отслеживаемых пар не растёт вместе с числом встреченных endpoint-ов и пакетов.
/// </para>
/// </remarks>
public sealed class AndroidGameMutationGate
{
    private readonly ConcurrentDictionary<GateKey, Entry> _entries = new();

    /// <summary>Число пар «endpoint + пакет», для которых сейчас удерживается координация.</summary>
    /// <value>
    /// Возвращается к нулю, когда освобождены все аренды: это наблюдаемое доказательство очистки
    /// неиспользуемых записей.
    /// </value>
    public int TrackedTargetCount => _entries.Count;

    /// <summary>Ожидает освобождения координации пары и занимает её.</summary>
    /// <param name="endpoint">Точный endpoint, над которым выполняется mutation.</param>
    /// <param name="package">Идентификатор пакета, над которым выполняется mutation.</param>
    /// <param name="cancellationToken">Запрос отмены ожидания.</param>
    /// <returns>
    /// Аренда координации. Освобождение аренды (ровно один раз) разрешает следующую mutation той же пары.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Ожидание координации отменено: аренда не выдана, запись пары освобождена.
    /// </exception>
    public async ValueTask<IDisposable> AcquireAsync(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        CancellationToken cancellationToken)
    {
        GateKey key = new(endpoint, package);
        Entry entry = Rent(key);
        bool acquired = false;

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return new Lease(this, key, entry);
        }
        finally
        {
            if (!acquired)
            {
                Return(key, entry);
            }
        }
    }

    private Entry Rent(GateKey key)
    {
        while (true)
        {
            Entry entry = _entries.GetOrAdd(key, static _ => new Entry());
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

    private void Return(GateKey key, Entry entry)
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
            _ = _entries.TryRemove(new KeyValuePair<GateKey, Entry>(key, entry));
            entry.Semaphore.Dispose();
        }
    }

    private readonly record struct GateKey(AndroidEndpoint Endpoint, AndroidPackageId Package);

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int RentCount { get; set; }

        internal bool Retired { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly AndroidGameMutationGate _owner;
        private readonly GateKey _key;
        private readonly Entry _entry;
        private int _released;

        internal Lease(AndroidGameMutationGate owner, GateKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _ = _entry.Semaphore.Release();
            _owner.Return(_key, _entry);
        }
    }
}
