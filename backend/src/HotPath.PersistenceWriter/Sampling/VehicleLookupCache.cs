using System.Collections.Concurrent;

namespace MageRide.HotPath.PersistenceWriter.Sampling;

/// <summary>
/// A bounded, TTL'd map from vehicle to some fact about it — a fleet, a tracking session.
/// </summary>
/// <remarks>
/// <para>
/// <b>A miss is cached too, and that is the whole reason this exists.</b> Every Mode C vehicle on the
/// platform publishes to <c>telemetry.normalized</c> and none of them has a Mode A/B tracking session
/// (R-01); most vehicles belong to no fleet. Without negative caching the write path would issue a
/// query per vehicle per batch for an answer that is reliably "nothing", which is exactly the
/// per-row database work ADD §9.5 batches through <c>COPY</c> to avoid.
/// </para>
/// <para>
/// Not <c>IMemoryCache</c>: this needs to answer "which of these vehicle ids do you not know about"
/// for a whole batch at once, so the resolver can issue one query for the misses instead of N. That
/// is the operation the write path is built around and it is not on that interface.
/// </para>
/// <para>
/// Eviction is crude on purpose — when the map exceeds its capacity, entries whose TTL has already
/// lapsed are dropped, and if that is not enough an arbitrary tenth goes. It is a cache in front of
/// two indexed lookups, so a wrong eviction costs one query. An LRU would cost a touch per read on
/// the hottest path this service has, to protect against a memory ceiling an operator sets.
/// </para>
/// </remarks>
/// <typeparam name="T">The fact being cached. Nullable to represent a cached miss.</typeparam>
internal sealed class VehicleLookupCache<T>(TimeSpan ttl, int capacity, TimeProvider clock)
    where T : class
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>The cached fact, or <see langword="false"/> when this vehicle is not known.</summary>
    public bool TryGet(Guid vehicleId, out T? value)
    {
        value = null;

        if (!_entries.TryGetValue(vehicleId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= clock.GetUtcNow())
        {
            _entries.TryRemove(vehicleId, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>Records a fact — or, with <paramref name="value"/> null, records that there is none.</summary>
    public void Set(Guid vehicleId, T? value)
    {
        _entries[vehicleId] = new Entry(value, clock.GetUtcNow() + ttl);

        if (_entries.Count > capacity)
        {
            Trim();
        }
    }

    /// <summary>Drops a vehicle, so the next batch resolves it again.</summary>
    public void Invalidate(Guid vehicleId) => _entries.TryRemove(vehicleId, out _);

    /// <summary>Entries currently held, expired ones included. Diagnostics and tests.</summary>
    public int Count => _entries.Count;

    private void Trim()
    {
        var now = clock.GetUtcNow();

        foreach (var (vehicleId, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(vehicleId, out _);
            }
        }

        if (_entries.Count <= capacity)
        {
            return;
        }

        // Still over. Nothing here has expired, so there is no better candidate than an arbitrary
        // one — and holding more than the operator asked for is the failure this is preventing.
        foreach (var vehicleId in _entries.Keys.Take(Math.Max(1, capacity / 10)))
        {
            _entries.TryRemove(vehicleId, out _);
        }
    }

    private readonly record struct Entry(T? Value, DateTimeOffset ExpiresAt);
}
