using System.Text;

namespace MageRide.TcpAdapter.Identity;

/// <summary>
/// The sticky-by-IMEI-hash scaling model, and the pod's own check that the load balancer agrees with
/// it (ADD §7.7.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why stickiness matters at all.</b> §7.7.6 sizes the plane at "3 pods × 10k sockets each per
/// protocol family (sticky-hash by IMEI)", and the reason is not load: it is that four things this
/// service holds are per device and live in process memory —
/// </para>
/// <list type="bullet">
/// <item>the open socket, which the downlink writes a command frame onto (§7.7.5);</item>
/// <item>the T-08 duplicate-socket detection, which is "one IMEI, two live sockets" and can only
/// be seen by a pod that holds both;</item>
/// <item>the T-04 presence pair, where an <c>online</c> from a new connect and an <c>offline</c> from
/// the old one racing across two pods can leave the retained value wrong;</item>
/// <item>the JT/T 808 session state — the header version and terminal id a reply has to echo.</item>
/// </list>
/// <para>
/// None of those is recoverable from another pod, which is why stickiness is a property of the
/// deployment rather than an optimisation. <b>It is enforced by the L4 balancer, not here.</b>
/// HAProxy's tracker frontends are TCP-mode and a device holds one connection for hours; the sticky
/// unit is therefore the connection, and a <c>stick-table</c> keyed on the source address is what
/// keeps a reconnecting device on the pod that still remembers it. On DOKS the same job is the
/// service's <c>sessionAffinity: ClientIP</c>.
/// </para>
/// <para>
/// <b>What this class does is check, not route.</b> With <c>Adapter:ShardCount</c> set, a session logs
/// once when a device's IMEI hashes to another shard — a balancer misconfiguration that is otherwise
/// invisible, because everything still works and only the four facts above quietly stop being
/// reliable. The device is <b>served anyway</b>: refusing it would turn a misconfiguration into an
/// outage, and the adapter is not the component that can fix the routing.
/// </para>
/// </remarks>
public static class ImeiShards
{
    /// <summary>
    /// FNV-1a over the IMEI's ASCII digits.
    /// </summary>
    /// <remarks>
    /// A stable, documented hash rather than <see cref="string.GetHashCode()"/>, which is randomised
    /// per process in .NET — every pod would compute a different shard for the same device and the
    /// check would fire constantly. FNV-1a is what a load-balancer configuration can be made to agree
    /// with, which is the whole point of writing it down.
    /// </remarks>
    public static uint Hash(string imei)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        const uint offsetBasis = 2_166_136_261;
        const uint prime = 16_777_619;

        var hash = offsetBasis;

        foreach (var value in Encoding.ASCII.GetBytes(imei))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    /// <summary>Which of <paramref name="shardCount"/> pods should hold this device.</summary>
    public static int ShardFor(string imei, int shardCount) =>
        shardCount <= 1 ? 0 : (int)(Hash(imei) % (uint)shardCount);
}
