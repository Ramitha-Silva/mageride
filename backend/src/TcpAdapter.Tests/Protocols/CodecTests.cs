using System.Text;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.TcpAdapter.Tests.Protocols;

/// <summary>
/// How each decoder behaves on the stream it actually gets: split reads, garbage in front, a failed
/// checksum, an escape sequence, and the frames it has to write back.
/// </summary>
/// <remarks>
/// These are the cases a golden frame cannot cover. A device does not arrive as one buffer containing
/// exactly one message — it arrives as whatever the network hands over, after a reconnect that may
/// have cut a previous frame in half, and the decoder's job is to make progress without ever
/// deadlocking on bytes it cannot parse.
/// </remarks>
[Trait("Category", "Codec")]
public sealed class CodecTests
{
    [Fact]
    public void A_GT06_frame_split_across_two_reads_is_decoded_once_it_is_whole()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);
        var frame = Captures.Gt06Position;
        var half = frame.Length / 2;

        // Nothing yet, and nothing consumed: a partial frame must stay in the buffer.
        Assert.False(codec.TryDecode(frame.AsSpan(0, half), out var partial, out var consumedPartial));
        Assert.Null(partial);
        Assert.Equal(0, consumedPartial);

        Assert.True(codec.TryDecode(frame, out var whole, out var consumed));
        Assert.NotNull(whole);
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(FrameKind.Position, whole!.Kind);
    }

    [Fact]
    public void Garbage_in_front_of_a_GT06_frame_is_consumed_without_losing_the_frame()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);
        var noise = Captures.Hex("AA BB CC");
        var buffer = new byte[noise.Length + Captures.Gt06Position.Length];

        noise.CopyTo(buffer.AsSpan());
        Captures.Gt06Position.CopyTo(buffer.AsSpan(noise.Length));

        // First pass: the leading bytes are dropped and nothing is decoded.
        Assert.True(codec.TryDecode(buffer, out var first, out var skipped));
        Assert.Null(first);
        Assert.Equal(noise.Length, skipped);

        // Second pass, over what is left: the frame.
        Assert.True(codec.TryDecode(buffer.AsSpan(skipped), out var second, out _));
        Assert.Equal(FrameKind.Position, second!.Kind);
    }

    [Fact]
    public void A_GT06_frame_with_a_broken_CRC_is_discarded_and_the_stream_moves_on()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);
        var frame = (byte[])Captures.Gt06Position.Clone();

        frame[^3] ^= 0xFF;

        // Reported as progress with no frame: the bytes are gone and the caller counts a rejection.
        // Leaving them would deadlock the stream on a message that can never be parsed.
        Assert.True(codec.TryDecode(frame, out var decoded, out var consumed));
        Assert.Null(decoded);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void A_GT06_login_is_answered_and_a_location_frame_is_not()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);

        Assert.True(codec.TryDecode(Captures.Gt06Login, out var login, out _));
        Assert.Equal(FrameKind.Login, login!.Kind);
        Assert.Equal(Captures.Imei, login.Identity);
        Assert.Equal(Captures.Gt06LoginAck, login.Reply);

        // The protocol does not ask for one, and firmware that receives an unexpected reply on that
        // number logs an error — on some builds it drops the session.
        Assert.True(codec.TryDecode(Captures.Gt06Position, out var position, out _));
        Assert.Null(position!.Reply);
    }

    [Fact]
    public void A_GT06_status_frame_reports_the_ACC_line_and_is_answered()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);

        Assert.True(codec.TryDecode(Captures.Gt06IgnitionOn, out var on, out _));
        Assert.Equal(FrameKind.Heartbeat, on!.Kind);
        Assert.True(on.Ignition);
        Assert.NotNull(on.Reply);

        Assert.True(codec.TryDecode(Captures.Gt06IgnitionOff, out var off, out _));
        Assert.False(off!.Ignition);
    }

    [Fact]
    public void A_GT06_time_request_is_answered_with_the_platforms_clock()
    {
        // The device is asking because its own RTC has no battery, and every fix it sends afterwards
        // is stamped from the answer — so this reply is load-bearing, not a courtesy.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 30, 4, 15, 30, TimeSpan.Zero));
        var codec = Codecs.For(ProtocolFamily.Gt06, clock);

        var request = Gt06Codec.BuildFrame(Gt06Codec.ProtocolTime, [], 9);

        Assert.True(codec.TryDecode(request, out var frame, out _));
        Assert.NotNull(frame!.Reply);

        // 26-07-30 04:15:30 as six binary bytes, inside a frame on the same protocol number.
        Assert.Equal(Captures.Hex("1A 07 1E 04 0F 1E"), frame.Reply!.AsSpan(4, 6).ToArray());
    }

    [Fact]
    public void An_unpositioned_GT06_fix_is_not_publishable()
    {
        // A GT06 with no satellites sends a location frame full of zeroes with the positioned bit
        // clear. Publishing it would put every unfixed tracker on the platform in the Gulf of Guinea.
        var content = new byte[26];
        Captures.Hex("1A 07 1E 04 0F 1E").CopyTo(content.AsSpan());
        content[6] = 0xC0;

        var codec = Codecs.For(ProtocolFamily.Gt06);
        var frame = Gt06Codec.BuildFrame(Gt06Codec.ProtocolLocation, content, 5);

        Assert.True(codec.TryDecode(frame, out var decoded, out _));

        var fix = Assert.Single(decoded!.Positions);
        Assert.False(fix.Valid);
        Assert.False(fix.IsPublishable);
    }

    [Fact]
    public void A_JT808_frame_is_unescaped_before_its_checksum_is_verified()
    {
        // 0x7D and 0x7E are escaped inside the delimiters as `7D 01` and `7D 02`, so the delimiter
        // cannot occur in a payload — and a decoder that verified the XOR over the escaped bytes would
        // reject every frame whose body happens to contain either value.
        var codec = Codecs.For(ProtocolFamily.Jt808);
        var body = new byte[Captures.Jt808Position.Length];

        Captures.Jt808Position.CopyTo(body.AsSpan());

        Assert.True(codec.TryDecode(body, out var plain, out _));
        Assert.Equal(FrameKind.Position, plain!.Kind);

        // A location report whose longitude byte is 0x7E, escaped on the wire.
        var escaped = Captures.Hex(
            "7E 02 00 40 1C 01 00 00 03 56 93 80 35 64 38 09 00 08 00 00 00 00 00 00 00 03 00 69 CF 80 " +
            "04 C2 4D 7D 02 00 05 01 2C 00 5A 26 07 30 12 15 30 F5 7E");

        var fresh = Codecs.For(ProtocolFamily.Jt808);

        Assert.True(fresh.TryDecode(escaped, out var unescaped, out var consumed));
        Assert.Equal(escaped.Length, consumed);
        Assert.NotNull(unescaped);
        Assert.Equal(FrameKind.Position, unescaped!.Kind);

        // 0x04C24D7E = 79 842 686 millionths.
        Assert.Equal(79.842_686, Assert.Single(unescaped.Positions).Lng, precision: 6);
    }

    [Fact]
    public void A_JT808_2013_header_presents_twelve_digits_and_decodes_the_same_fix()
    {
        var codec = Codecs.For(ProtocolFamily.Jt808);

        Assert.True(codec.TryDecode(Captures.Jt808Position2013, out var frame, out _));
        Assert.NotNull(frame);

        // The fix is fine. The identity is the problem, and it is a provisioning gap rather than a
        // decode bug: six BCD bytes are twelve digits and provisioning.yaml binds a 15-digit IMEI.
        Assert.Equal(Captures.Latitude, Assert.Single(frame!.Positions).Lat, precision: 6);
        Assert.Equal("938035643809", frame.Identity);
        Assert.Equal(12, frame.Identity!.Length);
    }

    [Fact]
    public void A_JT808_authentication_frame_carries_a_credential()
    {
        var codec = Codecs.For(ProtocolFamily.Jt808);

        Assert.True(codec.TryDecode(Captures.Jt808Authenticate, out var frame, out _));

        Assert.Equal(FrameKind.Login, frame!.Kind);
        Assert.Equal(Captures.Imei, frame.Identity);
        Assert.Equal("mrp1.ABC.999.s.g", frame.Credential);

        // Every JT/T 808 message the platform receives is answered with a general response, or the
        // device retries it until it gives up.
        Assert.NotNull(frame.Reply);
    }

    [Fact]
    public void A_JT808_bulk_upload_is_backlog_whatever_its_age()
    {
        // §8.36's 0x0704 is the message a device sends after a coverage gap — the backlog T-05 exists
        // for. It is routed to pos/replay by definition rather than by the age heuristic the other
        // three families need.
        var single = Captures.Hex(
            "00 00 00 00 00 00 00 03 00 69 CF 80 04 C2 4D F0 00 05 01 2C 00 5A 26 07 30 12 15 30");

        var body = new byte[3 + 2 + single.Length];
        body[0] = 0;
        body[1] = 1;
        body[2] = 1;
        body[3] = (byte)(single.Length >> 8);
        body[4] = (byte)(single.Length & 0xFF);
        single.CopyTo(body.AsSpan(5));

        var codec = (Jt808Codec)Codecs.For(ProtocolFamily.Jt808);

        // A platform message is addressed in whichever header shape the device used, and a fifteen-digit
        // IMEI does not fit the 2013 shape's six BCD bytes — so the device is seen first.
        Assert.True(codec.TryDecode(Captures.Jt808Position, out _, out _));

        var frame = codec.BuildFrame(Jt808Codec.MessageLocationBatch, body, Captures.Imei, 11);

        var reader = Codecs.For(ProtocolFamily.Jt808);

        Assert.True(reader.TryDecode(frame, out var decoded, out _));

        var fix = Assert.Single(decoded!.Positions);
        Assert.True(fix.Buffered, "every fix in a 0x0704 is a device's own history arriving late");
        Assert.Equal(Captures.CapturedAt, fix.CapturedAt);
    }

    [Fact]
    public void An_H02_line_accepts_either_separator()
    {
        // D6' §4.1 calls the family "ASCII pipe-delimited" and every device in it uses commas. Both
        // are accepted; the mismatch is a spec finding, not a reason to refuse a bus.
        var codec = Codecs.For(ProtocolFamily.H02);

        Assert.True(codec.TryDecode(Captures.Ascii(Captures.H02Position), out var comma, out _));
        Assert.True(codec.TryDecode(Captures.Ascii(Captures.H02PositionPipeDelimited), out var pipe, out _));

        Assert.Equal(Captures.Imei, comma!.Identity);
        Assert.Equal(Captures.Imei, pipe!.Identity);
        Assert.Equal(
            Assert.Single(comma.Positions).Lat, Assert.Single(pipe.Positions).Lat, precision: 9);
    }

    [Fact]
    public void An_H02_status_word_reports_the_ACC_line_inverted()
    {
        var codec = Codecs.For(ProtocolFamily.H02);

        // FFFFFBFF has bit 10 clear. The word is a set of active-low flags, so that is ignition on —
        // the reading the field-tested decoders for this family use, recorded as a C043 finding.
        Assert.True(codec.TryDecode(Captures.Ascii(Captures.H02Position), out var on, out _));
        Assert.True(on!.Ignition);

        var offLine = Captures.H02Position.Replace("FFFFFBFF", "FFFFFFFF");

        Assert.True(codec.TryDecode(Captures.Ascii(offLine), out var off, out _));
        Assert.False(off!.Ignition);
    }

    [Fact]
    public void An_H02_heartbeat_carries_an_identity_and_no_fix()
    {
        var codec = Codecs.For(ProtocolFamily.H02);

        Assert.True(codec.TryDecode(Captures.Ascii($"*HQ,{Captures.Imei},HTBT,96#"), out var frame, out _));

        Assert.Equal(FrameKind.Heartbeat, frame!.Kind);
        Assert.Equal(Captures.Imei, frame.Identity);
        Assert.Empty(frame.Positions);
    }

    [Fact]
    public void Two_H02_lines_in_one_read_are_decoded_one_at_a_time()
    {
        var codec = Codecs.For(ProtocolFamily.H02);
        var buffer = Captures.Ascii(Captures.H02Position + Captures.H02Position);

        Assert.True(codec.TryDecode(buffer, out var first, out var consumed));
        Assert.Equal(FrameKind.Position, first!.Kind);
        Assert.Equal(Captures.H02Position.Length, consumed);

        Assert.True(codec.TryDecode(buffer.AsSpan(consumed), out var second, out _));
        Assert.Equal(FrameKind.Position, second!.Kind);
    }

    [Theory]
    // The three framings this adapter accepts, stated in NmeaCodec's remarks because no spec gives one.
    [InlineData("IMEI:356938035643809;")]
    [InlineData("imei:356938035643809,")]
    [InlineData("#356938035643809#")]
    [InlineData("356938035643809,")]
    public void An_NMEA_datagram_identifies_its_device_from_the_prefix(string prefix)
    {
        var codec = Codecs.For(ProtocolFamily.NmeaUdp);
        var datagram = prefix + Captures.NmeaDatagram[(Captures.NmeaDatagram.IndexOf('$', StringComparison.Ordinal))..];

        Assert.True(codec.TryDecode(Captures.Ascii(datagram), out var frame, out _));
        Assert.Equal(Captures.Imei, frame!.Identity);
    }

    [Fact]
    public void An_NMEA_datagram_with_no_identity_prefix_is_unusable()
    {
        var codec = Codecs.For(ProtocolFamily.NmeaUdp);
        var sentences = Captures.NmeaDatagram[Captures.NmeaDatagram.IndexOf('$', StringComparison.Ordinal)..];

        Assert.True(codec.TryDecode(Captures.Ascii(sentences), out var frame, out _));

        // The fixes decode; there is simply nothing to bind them to.
        Assert.Null(frame!.Identity);
        Assert.NotEmpty(frame.Positions);
    }

    [Fact]
    public void A_corrupt_NMEA_sentence_is_dropped_and_the_rest_of_the_datagram_survives()
    {
        var codec = Codecs.For(ProtocolFamily.NmeaUdp);

        // The RMC's checksum is broken; the GGA's is not. UDP delivers a whole datagram or none, so
        // damage here is the device's buffer and the good sentence is still worth having.
        var datagram = Captures.NmeaDatagram.Replace(",,,A*56", ",,,A*57");

        Assert.True(codec.TryDecode(Captures.Ascii(datagram), out var frame, out _));

        var fix = Assert.Single(frame!.Positions);
        Assert.Equal(Captures.Latitude, fix.Lat, precision: 6);

        // GGA has no date, so this one is stamped from the receive clock's UTC day at the sentence's
        // time — which is why an RMC is preferred whenever there is one.
        Assert.Equal(9, fix.SatCount);
    }

    [Fact]
    public void A_GGA_only_datagram_takes_its_date_from_the_receive_clock()
    {
        var clock = new FakeTimeProvider(Captures.CapturedAt.AddMinutes(1));
        var codec = Codecs.For(ProtocolFamily.NmeaUdp, clock);

        var datagram = $"IMEI:{Captures.Imei};$GPGGA,041530.00,0656.0640,N,07950.5680,E,1,09,0.9,5.0,M,,M,,*73";

        Assert.True(codec.TryDecode(Captures.Ascii(datagram), out var frame, out _));

        var fix = Assert.Single(frame!.Positions);
        Assert.Equal(Captures.CapturedAt, fix.CapturedAt);
    }

    [Fact]
    public void A_GGA_only_datagram_across_midnight_is_corrected_by_a_day()
    {
        // Captured at 23:59:50, received at 00:00:05 the next day. Naively stamping "today at the
        // sentence's time" would put the fix almost a day in the future, which T-07's clock-skew gate
        // would then refuse.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 0, 0, 5, TimeSpan.Zero));
        var codec = Codecs.For(ProtocolFamily.NmeaUdp, clock);

        var datagram = $"IMEI:{Captures.Imei};" + Nmea("GPGGA,235950.00,0656.0640,N,07950.5680,E,1,09,0.9,5.0,M,,M,,");

        Assert.True(codec.TryDecode(Captures.Ascii(datagram), out var frame, out _));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 30, 23, 59, 50, TimeSpan.Zero),
            Assert.Single(frame!.Positions).CapturedAt);
    }

    [Fact]
    public void The_command_table_says_no_rather_than_guessing()
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["seconds"] = "30" };

        // GT06 carries an opaque ASCII command string; the Concox spellings are the ones the target
        // population implements.
        var gt06 = Codecs.For(ProtocolFamily.Gt06)
            .TryBuildCommand(TrackerCommands.SetPosRate, arguments, Captures.Imei, 1);

        Assert.NotNull(gt06);
        Assert.Contains("TIMER,30#", Encoding.ASCII.GetString(gt06!), StringComparison.Ordinal);

        // JT/T 808 sets the reporting interval as a typed parameter (§8.4's 0x0029), so there is
        // nothing to spell.
        var jt808 = Codecs.For(ProtocolFamily.Jt808)
            .TryBuildCommand(TrackerCommands.SetPosRate, arguments, Captures.Imei, 1);

        Assert.NotNull(jt808);

        // H02's command set is published per device family, not with the protocol. One command has a
        // consistent meaning across the population; the rest answer null and are counted as
        // unsupported, because an ASCII command a device does not recognise is discarded silently.
        var h02 = Codecs.For(ProtocolFamily.H02);
        Assert.NotNull(h02.TryBuildCommand(TrackerCommands.SetPosRate, arguments, Captures.Imei, 1));
        Assert.Null(h02.TryBuildCommand(TrackerCommands.Reboot, arguments, Captures.Imei, 1));

        // Generic NMEA has no command grammar at all, and UDP has no session to write one back on.
        var nmea = Codecs.For(ProtocolFamily.NmeaUdp);
        foreach (var command in TrackerCommands.All)
        {
            Assert.Null(nmea.TryBuildCommand(command, arguments, Captures.Imei, 1));
        }
    }

    [Fact]
    public void A_setPosRate_with_an_unusable_argument_builds_nothing()
    {
        var codec = Codecs.For(ProtocolFamily.Gt06);

        Assert.Null(codec.TryBuildCommand(
            TrackerCommands.SetPosRate, new Dictionary<string, string>(StringComparer.Ordinal), Captures.Imei, 1));

        Assert.Null(codec.TryBuildCommand(
            TrackerCommands.SetPosRate,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["seconds"] = "not a number" },
            Captures.Imei,
            1));

        Assert.Null(codec.TryBuildCommand(
            TrackerCommands.SetPosRate,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["seconds"] = "0" },
            Captures.Imei,
            1));
    }

    [Fact]
    public void A_JT808_geofence_command_is_a_circular_area_message()
    {
        var codec = Codecs.For(ProtocolFamily.Jt808);

        // The device has to have been seen first: a platform message is addressed to the terminal
        // number out of its header.
        Assert.True(codec.TryDecode(Captures.Jt808Position, out _, out _));

        var frame = codec.TryBuildCommand(
            TrackerCommands.SetGeofence,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lat"] = "6.9344",
                ["lng"] = "79.8428",
                ["radiusM"] = "250",
            },
            Captures.Imei,
            3);

        Assert.NotNull(frame);
        Assert.Equal(0x7E, frame![0]);
        Assert.Equal(0x7E, frame[^1]);

        // Message id 0x8600 in the first two bytes after the delimiter.
        Assert.Equal(0x86, frame[1]);
        Assert.Equal(0x00, frame[2]);
    }

    private static string Nmea(string body)
    {
        byte checksum = 0;

        foreach (var character in body)
        {
            checksum ^= (byte)character;
        }

        return $"${body}*{checksum:X2}";
    }
}
