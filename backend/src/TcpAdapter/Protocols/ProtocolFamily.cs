using MageRide.Shared.Telemetry;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>The four protocol families this service terminates (D6' §4.1, ADD §7.7.1).</summary>
/// <remarks>
/// NMEA-over-MQTT is deliberately absent. Those devices speak MQTT natively and connect straight to
/// EMQX on 8883 with the same per-device credential and the same ACL — <b>they never reach this
/// service</b>, which is one of this component's fences.
/// </remarks>
public enum ProtocolFamily
{
    /// <summary>Concox GT06 / GT06N, TK103, ST-901 clones — TCP, binary framed.</summary>
    Gt06,

    /// <summary>JT/T 808, the Chinese national standard — TCP, binary framed, escaped.</summary>
    Jt808,

    /// <summary>H02 / H02X — TCP, ASCII, delimited lines.</summary>
    H02,

    /// <summary>Generic NMEA 0183 over UDP — low-cost asset trackers.</summary>
    NmeaUdp,
}

/// <summary>How a family reaches the adapter.</summary>
public enum ProtocolTransport
{
    /// <summary>A long-lived stream socket; the adapter frames it.</summary>
    Tcp,

    /// <summary>Connectionless datagrams; one datagram is one or more sentences.</summary>
    Udp,
}

/// <summary>Per-family constants: the listener order, the wire name, the sink's <c>source</c> code.</summary>
public static class ProtocolFamilies
{
    /// <summary>
    /// The families in the order <c>Adapter:Ports</c> lists them — GT06, JT/T 808, H02, NMEA/UDP.
    /// </summary>
    /// <remarks>
    /// This order <b>is</b> the configuration contract (see <see cref="Configuration.AdapterOptions.Ports"/>)
    /// and it is D6' §4.1's table read top to bottom, which is also 5023, 5024, 5025, 5026 ascending.
    /// Reordering it silently re-points every deployment's listeners, so the test suite asserts it.
    /// </remarks>
    public static readonly IReadOnlyList<ProtocolFamily> All =
        [ProtocolFamily.Gt06, ProtocolFamily.Jt808, ProtocolFamily.H02, ProtocolFamily.NmeaUdp];

    /// <summary>The adapter name D6' §4.1 gives the family — what a log line and a metric label say.</summary>
    public static string Name(ProtocolFamily family) => family switch
    {
        ProtocolFamily.Gt06 => "adapter-gt06",
        ProtocolFamily.Jt808 => "adapter-jt808",
        ProtocolFamily.H02 => "adapter-h02",
        ProtocolFamily.NmeaUdp => "adapter-nmea-udp",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>Which transport the family arrives on.</summary>
    public static ProtocolTransport Transport(ProtocolFamily family) =>
        family == ProtocolFamily.NmeaUdp ? ProtocolTransport.Udp : ProtocolTransport.Tcp;

    /// <summary>
    /// The <c>telemetry.positions.source</c> code a sample from this family carries.
    /// </summary>
    /// <remarks>
    /// Generic UDP-NMEA maps to <see cref="PositionSource.NmeaMqtt"/>, which is <c>4</c> and named for
    /// the MQTT-native devices. <b>That is the enum the schema constrains</b> —
    /// <c>ck_positions_source</c> allows <c>0…4</c> and D6' §4.1 lists five families for five codes,
    /// with generic UDP-NMEA and NMEA-over-MQTT sharing the one that says "this is NMEA". Coining a
    /// fifth would need a migration to widen the CHECK for a distinction no consumer reads: what a
    /// reader wants from <c>source</c> is which decoder produced the numbers, and for these two it is
    /// the same sentence grammar. Raised as a finding in the C043 handoff.
    /// </remarks>
    public static PositionSource Source(ProtocolFamily family) => family switch
    {
        ProtocolFamily.Gt06 => PositionSource.Gt06,
        ProtocolFamily.Jt808 => PositionSource.Jt808,
        ProtocolFamily.H02 => PositionSource.H02,
        ProtocolFamily.NmeaUdp => PositionSource.NmeaMqtt,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
