using MageRide.FleetHealth.Domain;

namespace MageRide.FleetHealth.Ingest;

/// <summary>
/// Collapses a flush interval's worth of <c>telemetry.normalized</c> samples to one row per vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole reason the health plane can sit on the hot path.</b> T-10 sizes ingest at
/// 20k msg/s and this service sees all of it, but "when did this device last ping" changes meaning
/// only at the five-minute grain US-3.13 defines — so a hundred samples from one bus in five seconds
/// carry exactly one fact, and writing a row per sample would be four orders of magnitude more
/// database work than the question needs.
/// </para>
/// <para>
/// <b>Not thread-safe, deliberately.</b> One consume loop owns it, adds on the loop thread and drains
/// on the same thread; a <c>ConcurrentDictionary</c> would buy nothing and hide the fact that the
/// drain has to be atomic with respect to the offsets the loop is about to commit.
/// </para>
/// </remarks>
internal sealed class PingAccumulator(int capacity)
{
    private readonly Dictionary<Guid, DeviceHealthPing> _pending = [];

    /// <summary>Devices awaiting a flush.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Whether the accumulator is full and the loop should stop consuming.
    /// </summary>
    /// <remarks>
    /// The same fence C040 applies to its row buffer, and for the same reason: an unbounded map turns
    /// a database outage into an OOM kill, while a stalled consumer turns it into a growing broker
    /// backlog that recovers on its own. Nothing is dropped — the offsets are uncommitted, so a
    /// restart re-reads them.
    /// </remarks>
    public bool IsFull => _pending.Count >= capacity;

    /// <summary>Merges one sample's liveness into the pending row for its vehicle.</summary>
    public void Add(DeviceHealthPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        _pending[ping.VehicleId] = _pending.TryGetValue(ping.VehicleId, out var existing)
            ? existing.Merge(ping)
            : ping;
    }

    /// <summary>Takes everything pending, leaving the accumulator empty.</summary>
    public List<DeviceHealthPing> Drain()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var drained = new List<DeviceHealthPing>(_pending.Values);
        _pending.Clear();

        return drained;
    }

    /// <summary>
    /// Puts a failed flush's rows back, without overwriting anything that arrived since.
    /// </summary>
    /// <remarks>
    /// Newer wins, which is what <see cref="DeviceHealthPing.Merge"/> decides field by field — a
    /// retry must not be able to move a device's clock backwards.
    /// </remarks>
    public void Restore(IReadOnlyCollection<DeviceHealthPing> pings)
    {
        ArgumentNullException.ThrowIfNull(pings);

        foreach (var ping in pings)
        {
            Add(ping);
        }
    }
}
