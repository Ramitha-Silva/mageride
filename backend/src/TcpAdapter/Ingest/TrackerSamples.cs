using MageRide.Shared.Telemetry;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Protocols;

// `System.Net.Sockets` has a ProtocolFamily of its own; the spec's word wins by name where both are
// in scope. Kept here too so this file reads the same as the two that use it.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// Turns a decoded fix into the canonical <see cref="PositionSample"/>.
/// </summary>
/// <remarks>
/// <para>
/// One function, used by the TCP sessions and the UDP listener alike, because the mapping is the
/// contract: <c>mqtt-topics.md</c> §2.1's payload is what position-processor-svc decodes and what the
/// hypertable's columns are, and two producers filling it slightly differently would show up as a
/// vehicle whose type appears and disappears depending on which port its tracker uses.
/// </para>
/// <para>
/// <b><c>seq</c> is the capture instant in milliseconds.</b> A tracker frame has no sequence number
/// worth using: GT06's and JT/T 808's information serials are sixteen bits, wrap every few hours, and
/// survive neither a device reboot nor a pod move — all of which R-17's watermark has to survive,
/// because <c>veh:seq:{vehicleId}</c> outlives every one of them. The GNSS instant does. It is
/// monotonic per vehicle (which is what T-07's monotonic-clock check independently requires of
/// hardware), it is <i>identical</i> for a sample sent live and the same sample re-sent from the
/// device's flash ring, and it makes the backlog dedupe fall out of the comparison position-processor
/// already makes. The cost is that two fixes stamped to the same millisecond collide — and for
/// families that stamp to the whole second, two fixes in one second are the same position twice.
/// </para>
/// <para>
/// <b>Two fields are deliberately left null.</b> <c>accuracyM</c>, because none of the four protocols
/// reports a horizontal accuracy and deriving one from HDOP would feed T-07's 200 m gate a number no
/// receiver produced — a tracker that reports HDOP carries it as HDOP. And <c>tripId</c>, because the
/// Mode A/B session is trip-state-svc's and opened by the ignition report; this service is never told
/// its id.
/// </para>
/// </remarks>
public static class TrackerSamples
{
    /// <summary>Builds the sample one fix becomes.</summary>
    /// <param name="fix">The decoded fix.</param>
    /// <param name="vehicleId">The vehicle its binding resolved to.</param>
    /// <param name="family">Which adapter decoded it — becomes <c>source</c>.</param>
    /// <param name="profile">The registry profile, when it could be read.</param>
    /// <param name="receivedAt">The platform's receive clock.</param>
    public static PositionSample From(
        TrackerFix fix, Guid vehicleId, ProtocolFamily family, VehicleProfile? profile, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(fix);

        return new PositionSample(
            vehicleId,
            fix.CapturedAt,
            fix.CapturedAt.ToUnixTimeMilliseconds(),
            fix.Lat,
            fix.Lng,
            ProtocolFamilies.Source(family),
            ReceivedTs: receivedAt,
            SpeedMps: fix.SpeedMps,
            HeadingDeg: fix.HeadingDeg,
            AccuracyM: null,
            Hdop: fix.Hdop,
            SatCount: fix.SatCount,
            Mode: profile?.Mode,
            VehicleType: profile?.VehicleType,
            FleetId: profile?.FleetId,
            TripId: null);
    }

    /// <summary>
    /// Whether a fix goes to <c>pos/replay</c> rather than <c>pos/live</c> (T-05).
    /// </summary>
    /// <remarks>
    /// JT/T 808 says so itself — <c>0x0704</c> is the backlog message by definition. The other three
    /// carry no such bit, so age is the only signal there is, and getting it wrong in the safe
    /// direction (a live sample routed to the backlog) costs it the bridge's 20/s pacing and nothing
    /// else.
    /// </remarks>
    public static bool IsReplay(TrackerFix fix, DateTimeOffset now, TimeSpan replayAge)
    {
        ArgumentNullException.ThrowIfNull(fix);

        return fix.Buffered || now - fix.CapturedAt > replayAge;
    }
}
