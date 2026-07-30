namespace MageRide.TcpAdapter.Protocols;

/// <summary>What a decoded frame is.</summary>
public enum FrameKind
{
    /// <summary>A frame that parsed but carries nothing this service acts on.</summary>
    Ignored,

    /// <summary>The device announcing its identity — GT06 <c>0x01</c>, JT/T 808 <c>0x0100</c>/<c>0x0102</c>.</summary>
    Login,

    /// <summary>One or more GNSS fixes.</summary>
    Position,

    /// <summary>Keep-alive, with or without a status byte.</summary>
    Heartbeat,

    /// <summary>An alarm, which on GT06 and JT/T 808 carries a fix beside the alarm code.</summary>
    Alarm,
}

/// <summary>
/// One GNSS fix as a protocol reported it — before it is bound to a vehicle.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="Shared.Telemetry.PositionSample"/>: a fix off the wire has no
/// <c>vehicleId</c>, no <c>mode</c> and no <c>seq</c>, and those three come from the binding, the
/// registry and the clock respectively. Building the canonical sample is
/// <see cref="Ingest.TrackerSession"/>'s job, once, in one place — a codec that built one would have
/// to invent all three.
/// </remarks>
/// <param name="CapturedAt">The GNSS instant, in UTC. <b>Not</b> the receive time.</param>
/// <param name="Lat">Degrees north, signed.</param>
/// <param name="Lng">Degrees east, signed.</param>
/// <param name="Valid">Whether the device flagged the fix as positioned. An invalid fix is dropped.</param>
/// <param name="SpeedMps">Ground speed in metres per second, converted from whatever the wire used.</param>
/// <param name="HeadingDeg">Course over ground, 0…359.</param>
/// <param name="SatCount">Satellites in the fix, when the frame says.</param>
/// <param name="Hdop">Horizontal dilution of precision, when the frame says.</param>
/// <param name="Buffered">
/// True when the protocol itself said this is backlog rather than live — JT/T 808's
/// <c>0x0704</c> bulk upload. GT06, H02 and NMEA have no such bit, so for those the age of
/// <paramref name="CapturedAt"/> is the only signal and the session applies it.
/// </param>
public sealed record TrackerFix(
    DateTimeOffset CapturedAt,
    double Lat,
    double Lng,
    bool Valid,
    double? SpeedMps = null,
    int? HeadingDeg = null,
    int? SatCount = null,
    double? Hdop = null,
    bool Buffered = false)
{
    /// <summary>
    /// Whether the numbers are inside the domains <c>telemetry.positions</c> constrains.
    /// </summary>
    /// <remarks>
    /// <c>mqtt-topics.md</c> §2.1: "a cheap tracker reporting 0/999 degrees is a bug". The zero
    /// coordinate is refused here rather than downstream because it is *this* layer's artefact — a
    /// GT06 with no fix sends a location packet full of zeroes, and publishing it would put every
    /// unpositioned tracker on the platform in the Gulf of Guinea.
    /// </remarks>
    public bool IsPublishable =>
        Valid
        && !double.IsNaN(Lat) && Lat is >= -90 and <= 90
        && !double.IsNaN(Lng) && Lng is >= -180 and <= 180
        && (Math.Abs(Lat) > 1e-7 || Math.Abs(Lng) > 1e-7);
}

/// <summary>
/// One frame off the wire: what kind it was, what it identified itself as, what it carried, and what
/// has to be written straight back.
/// </summary>
/// <param name="Kind">What the frame is.</param>
/// <param name="Identity">
/// The device identifier the frame presented, digits only and leading zeros stripped — a GT06 login's
/// 8 BCD bytes, a JT/T 808 header's terminal phone number, an H02 line's device id, the IMEI prefix
/// on an NMEA datagram. Whether it is a well-formed IMEI is the authenticator's question, not the
/// codec's.
/// </param>
/// <param name="Credential">
/// A credential the frame carried, when the protocol has a field for one — JT/T 808's <c>0x0102</c>
/// authentication code. Null on GT06, H02 and NMEA, none of which do.
/// </param>
/// <param name="Fixes">The fixes it carried, in wire order. Empty for a login or a bare heartbeat.</param>
/// <param name="Reply">
/// Bytes to write back immediately, if the protocol requires an acknowledgement. A GT06 that is not
/// acknowledged re-sends its login until it gives up and reboots, so this is not optional.
/// </param>
/// <param name="Ignition">
/// The ACC line's state when the frame reports it — GT06's status byte, JT/T 808's status word, H02's
/// status flags. Drives the AL-32 auto-session (US-3.22/3.23); null means the frame did not say.
/// </param>
/// <param name="Detail">Free text for the log line — an alarm code, a message id.</param>
public sealed record TrackerFrame(
    FrameKind Kind,
    string? Identity = null,
    string? Credential = null,
    IReadOnlyList<TrackerFix>? Fixes = null,
    byte[]? Reply = null,
    bool? Ignition = null,
    string? Detail = null)
{
    /// <summary>A frame that parsed and means nothing to this service.</summary>
    public static readonly TrackerFrame Ignored = new(FrameKind.Ignored);

    /// <summary>The fixes, never null.</summary>
    public IReadOnlyList<TrackerFix> Positions => Fixes ?? [];
}

/// <summary>
/// A protocol decoder: turns a byte stream into <see cref="TrackerFrame"/>s and an outbound command
/// envelope into the protocol's native command frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length-prefix framing is the decoder's, not the transport's.</b> TCP delivers a stream, and
/// every one of these protocols has its own idea of where a message ends: GT06 has start and stop
/// bytes with a length in between, JT/T 808 has <c>0x7E</c> delimiters and byte escaping, H02 has
/// line terminators. <see cref="TryDecode"/> therefore reports how many bytes it consumed rather
/// than assuming a datagram, and returns <c>false</c> with <c>consumed = 0</c> when it needs more.
/// </para>
/// <para>
/// <b>A codec is pure.</b> It reads no cache, resolves no vehicle and reaches no network — which is
/// what lets the golden tests assert a captured frame against an expected fix with nothing running.
/// </para>
/// </remarks>
public interface IProtocolCodec
{
    /// <summary>Which family this decodes.</summary>
    ProtocolFamily Family { get; }

    /// <summary>
    /// Reads the next frame out of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Everything received and not yet consumed.</param>
    /// <param name="frame">The decoded frame, when one was complete.</param>
    /// <param name="consumed">
    /// Bytes to drop from the head of the buffer. Non-zero even when
    /// <paramref name="frame"/> is null — that is how a codec discards leading garbage or a frame
    /// whose checksum failed, without stalling the stream on it.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the buffer does not yet hold a whole frame. A malformed frame
    /// returns <see langword="true"/> with a null <paramref name="frame"/> and a non-zero
    /// <paramref name="consumed"/>.
    /// </returns>
    bool TryDecode(ReadOnlySpan<byte> buffer, out TrackerFrame? frame, out int consumed);

    /// <summary>
    /// Translates a downlink envelope into a command frame for this protocol, or
    /// <see langword="null"/> when the protocol cannot express it (§7.7.5).
    /// </summary>
    /// <param name="command">The canonical command name — <c>setPosRate</c> and friends.</param>
    /// <param name="arguments">Its arguments, as the envelope carried them.</param>
    /// <param name="identity">The device's identifier; some protocols echo it in the command.</param>
    /// <param name="serial">A per-session counter the frame stamps, so a reply can be correlated.</param>
    byte[]? TryBuildCommand(
        string command, IReadOnlyDictionary<string, string> arguments, string identity, ushort serial);
}
