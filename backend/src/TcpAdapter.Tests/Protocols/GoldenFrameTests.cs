using MageRide.Shared.Telemetry;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;

namespace MageRide.TcpAdapter.Tests.Protocols;

/// <summary>
/// The DoD's first line: <b>a captured frame from each of the four protocol families decodes to the
/// expected PositionSample</b>.
/// </summary>
/// <remarks>
/// <para>
/// Asserted through <see cref="TrackerSamples.From"/> — the production mapping both the TCP sessions
/// and the UDP listener use — rather than against the intermediate <see cref="TrackerFix"/> alone. A
/// test that stopped at the fix would pass while <c>source</c>, <c>seq</c> or the denormalised
/// <c>mode</c> were wrong, and those three are what every downstream consumer keys on.
/// </para>
/// <para>
/// The four frames describe one vehicle at one instant in one place (see <see cref="Captures"/>), so
/// each assertion is also a cross-check on the other three: a hemisphere bit read backwards, a knot
/// read as a km/h or a Beijing timestamp left in local time all show up as a disagreement with the
/// same three numbers.
/// </para>
/// </remarks>
[Trait("Category", "Codec")]
public sealed class GoldenFrameTests
{
    private static readonly Guid Vehicle = Guid.Parse("00000000-0000-4000-8000-00000000c001");

    private static readonly VehicleProfile Profile =
        new(Vehicle, "A", "bus", Guid.Parse("00000000-0000-4000-8000-0000000f1001"));

    private static readonly DateTimeOffset ReceivedAt = Captures.CapturedAt.AddSeconds(2);

    [Fact]
    public void A_GT06_location_frame_decodes_to_the_expected_sample()
    {
        var sample = Decode(ProtocolFamily.Gt06, Captures.Gt06Position);

        AssertColomboFort(sample, PositionSource.Gt06);

        // GT06's speed byte is km/h and its GPS-information byte's low nibble is the satellite count.
        Assert.Equal(Captures.SpeedMps, sample.SpeedMps!.Value, precision: 6);
        Assert.Equal(9, sample.SatCount);

        // Nothing in the frame carries an accuracy or an HDOP, and neither is invented.
        Assert.Null(sample.AccuracyM);
        Assert.Null(sample.Hdop);
    }

    [Fact]
    public void A_JT808_location_report_decodes_to_the_expected_sample()
    {
        var sample = Decode(ProtocolFamily.Jt808, Captures.Jt808Position);

        AssertColomboFort(sample, PositionSource.Jt808);

        // Speed is deci-km/h: 300 is 30 km/h. The BCD stamp is Beijing time and the assertion above
        // is in UTC, so this frame also proves the eight-hour conversion.
        Assert.Equal(Captures.SpeedMps, sample.SpeedMps!.Value, precision: 6);

        // No additional-item 0x31 in this frame, so no satellite count is claimed.
        Assert.Null(sample.SatCount);
    }

    [Fact]
    public void An_H02_position_line_decodes_to_the_expected_sample()
    {
        var sample = Decode(ProtocolFamily.H02, Captures.Ascii(Captures.H02Position));

        AssertColomboFort(sample, PositionSource.H02);

        // 16.2 knots is 30.0 km/h to within a thousandth of a metre per second. Read as km/h it would
        // be 4.5 m/s — inside every ADD §12.6 plausibility threshold, so nothing downstream would
        // ever catch it.
        Assert.Equal(Captures.SpeedMps, sample.SpeedMps!.Value, tolerance: 0.001);
    }

    [Fact]
    public void A_generic_NMEA_datagram_decodes_to_the_expected_sample()
    {
        var sample = Decode(ProtocolFamily.NmeaUdp, Captures.Ascii(Captures.NmeaDatagram));

        AssertColomboFort(sample, PositionSource.NmeaMqtt);

        Assert.Equal(Captures.SpeedMps, sample.SpeedMps!.Value, tolerance: 0.001);

        // RMC carries the position and the date; the GGA for the same second is folded in for these.
        Assert.Equal(9, sample.SatCount);
        Assert.Equal(0.9, sample.Hdop);
    }

    [Fact]
    public void Every_family_presents_the_same_IMEI()
    {
        Assert.Equal(Captures.Imei, Identity(ProtocolFamily.Gt06, Captures.Gt06Login));
        Assert.Equal(Captures.Imei, Identity(ProtocolFamily.Jt808, Captures.Jt808Position));
        Assert.Equal(Captures.Imei, Identity(ProtocolFamily.H02, Captures.Ascii(Captures.H02Position)));
        Assert.Equal(
            Captures.Imei, Identity(ProtocolFamily.NmeaUdp, Captures.Ascii(Captures.NmeaDatagram)));
    }

    /// <summary>
    /// The listener order is the <c>Adapter:Ports</c> contract, and 5023-5026 in D6' §4.1's order.
    /// </summary>
    /// <remarks>
    /// Asserted because reordering the enum silently re-points every deployment's listeners: GT06
    /// devices would arrive at the JT/T 808 decoder, fail every checksum, and be counted as malformed
    /// rather than as misrouted.
    /// </remarks>
    [Fact]
    public void The_family_order_is_the_port_order()
    {
        Assert.Equal(
            [ProtocolFamily.Gt06, ProtocolFamily.Jt808, ProtocolFamily.H02, ProtocolFamily.NmeaUdp],
            ProtocolFamilies.All);

        Assert.Equal("adapter-gt06", ProtocolFamilies.Name(ProtocolFamily.Gt06));
        Assert.Equal("adapter-jt808", ProtocolFamilies.Name(ProtocolFamily.Jt808));
        Assert.Equal("adapter-h02", ProtocolFamilies.Name(ProtocolFamily.H02));
        Assert.Equal("adapter-nmea-udp", ProtocolFamilies.Name(ProtocolFamily.NmeaUdp));

        Assert.Equal(ProtocolTransport.Udp, ProtocolFamilies.Transport(ProtocolFamily.NmeaUdp));
        Assert.Equal(ProtocolTransport.Tcp, ProtocolFamilies.Transport(ProtocolFamily.Gt06));
    }

    private static void AssertColomboFort(PositionSample sample, PositionSource source)
    {
        Assert.Equal(Vehicle, sample.VehicleId);
        Assert.Equal(Captures.Latitude, sample.Lat, precision: 6);
        Assert.Equal(Captures.Longitude, sample.Lng, precision: 6);
        Assert.Equal(Captures.CapturedAt, sample.SampleTs);
        Assert.Equal(Captures.HeadingDeg, sample.HeadingDeg);
        Assert.Equal(source, sample.Source);

        // The seq is the capture instant in milliseconds — the replay dedupe key (R-17/T-05).
        Assert.Equal(Captures.CapturedAt.ToUnixTimeMilliseconds(), sample.Seq);

        // Denormalised from the registry so a consumer needs no lookup (mqtt-topics.md §2.1).
        Assert.Equal(Profile.Mode, sample.Mode);
        Assert.Equal(Profile.VehicleType, sample.VehicleType);
        Assert.Equal(Profile.FleetId, sample.FleetId);

        // A Mode A/B session is trip-state-svc's and its id never reaches this service.
        Assert.Null(sample.TripId);

        Assert.Equal(ReceivedAt, sample.ReceivedTs);
        Assert.True(sample.IsWellFormed, "the sink's CHECK domains must accept a decoded tracker sample");
    }

    private static PositionSample Decode(ProtocolFamily family, byte[] frame)
    {
        var decoded = Read(family, frame);

        var fix = Assert.Single(decoded.Positions);
        Assert.True(fix.IsPublishable, "a golden frame's fix must be publishable");

        return TrackerSamples.From(fix, Vehicle, family, Profile, ReceivedAt);
    }

    private static string? Identity(ProtocolFamily family, byte[] frame) => Read(family, frame).Identity;

    private static TrackerFrame Read(ProtocolFamily family, byte[] frame)
    {
        var codec = Codecs.For(family);

        Assert.True(
            codec.TryDecode(frame, out var decoded, out var consumed),
            $"{ProtocolFamilies.Name(family)} did not frame {Captures.ToHex(frame)}");

        Assert.NotNull(decoded);
        Assert.True(consumed > 0, "a decoded frame must consume bytes");

        return decoded!;
    }
}
