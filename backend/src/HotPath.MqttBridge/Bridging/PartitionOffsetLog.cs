using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// The highest <c>telemetry.raw</c> offset this replica has written, per partition.
/// </summary>
/// <remarks>
/// <para>
/// ADD §7.3 says the bridge "commit[s] Redpanda (Kafka-API) offsets per partition". <b>A producer
/// has no offsets to commit</b> — committing is what a consumer group does with the offsets it has
/// read, and the bridge only writes. The half of that sentence that is real, and the half that
/// carries the guarantee behind it, is that the bridge must know <i>where the broker put a record</i>
/// before it acknowledges the MQTT message: an acknowledged payload with no confirmed offset is a
/// position EMQX has stopped holding and Redpanda never took. So the offset is read off the
/// delivery report, recorded here, and acknowledged only then. Raised as a spec finding in the C038
/// handoff.
/// </para>
/// <para>
/// The gauge is registered once per process and reports every live bridge in it, tagged by
/// <c>bridge</c> and <c>partition</c> — the test harness runs two replicas in one process, and a
/// per-instance instrument would give the exporter two instruments with one name.
/// </para>
/// </remarks>
internal sealed class PartitionOffsetLog(string bridgeId) : IDisposable
{
    private static readonly ConcurrentDictionary<string, PartitionOffsetLog> Live = new(StringComparer.Ordinal);

    private static readonly ObservableGauge<long> Gauge = MageRideDiagnostics.Meter.CreateObservableGauge(
        MageRideDiagnostics.MqttBridgePartitionOffsetGauge,
        Observe,
        "{offset}",
        "Highest telemetry.raw offset an mqtt-bridge replica has written, per partition.");

    private readonly ConcurrentDictionary<int, long> _offsets = new();

    /// <summary>The partitions this replica has written to, and the last offset on each.</summary>
    public IReadOnlyDictionary<int, long> Offsets => _offsets;

    /// <summary>Starts reporting this replica's offsets on the process gauge.</summary>
    public PartitionOffsetLog Register()
    {
        Live[bridgeId] = this;
        return this;
    }

    /// <summary>Records where the broker put a record. Monotonic per partition.</summary>
    public void Record(PublishReceipt receipt)
    {
        if (receipt.Partition < 0)
        {
            return;
        }

        // AddOrUpdate rather than an assignment: several forwards are in flight at once and their
        // delivery reports do not arrive in produce order, so a plain write could walk the
        // high-water mark backwards.
        _offsets.AddOrUpdate(receipt.Partition, receipt.Offset, (_, current) => Math.Max(current, receipt.Offset));
    }

    public void Dispose() => Live.TryRemove(bridgeId, out _);

    private static IEnumerable<Measurement<long>> Observe()
    {
        foreach (var (bridge, log) in Live)
        {
            foreach (var (partition, offset) in log._offsets)
            {
                yield return new Measurement<long>(
                    offset,
                    new KeyValuePair<string, object?>("bridge", bridge),
                    new KeyValuePair<string, object?>("partition", partition));
            }
        }
    }

    static PartitionOffsetLog()
    {
        // Referencing the field keeps the static initialiser — and therefore the registration —
        // from being elided.
        _ = Gauge;
    }
}
