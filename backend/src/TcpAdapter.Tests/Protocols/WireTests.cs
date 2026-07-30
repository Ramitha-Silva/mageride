using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;

namespace MageRide.TcpAdapter.Tests.Protocols;

/// <summary>
/// The three checksums, BCD, and the coordinate conversion — every place a decoder can be subtly
/// wrong in a way nothing reports.
/// </summary>
[Trait("Category", "Codec")]
public sealed class WireTests
{
    /// <summary>
    /// The one independently attestable fixed point in the whole GT06 format.
    /// </summary>
    /// <remarks>
    /// <c>78 78 05 01 00 01 D9 DC 0D 0A</c> is the login acknowledgement the GT06 documentation prints
    /// verbatim, so it pins both the algorithm (CRC-16/X-25: reflected 0x8408, init 0xFFFF, final XOR
    /// 0xFFFF) and the range it covers (the length byte through the serial number inclusive). The same
    /// polynomial unreflected with a zero initial value is CRC-CCITT and produces a different digest
    /// for these four bytes — a decoder using it would reject every genuine frame, and every frame
    /// this suite constructs would be wrong in the same direction and still agree with it. That is why
    /// the fixed point matters.
    /// </remarks>
    [Fact]
    public void The_documented_GT06_login_acknowledgement_verifies()
    {
        Assert.Equal((ushort)0xD9DC, Wire.Crc16X25(Captures.Hex("05 01 00 01")));

        // And the builder produces exactly those bytes, so every ack the adapter writes is that frame.
        Assert.Equal(Captures.Gt06LoginAck, Gt06Codec.BuildFrame(Gt06Codec.ProtocolLogin, [], 1));
    }

    [Fact]
    public void The_JT808_check_byte_is_an_XOR_over_the_header_and_body()
    {
        // 0x02 ^ 0x00 ^ 0x40 ^ 0x1C = 0x5E, and adding a byte equal to the running XOR clears it.
        Assert.Equal((byte)0x5E, Wire.Xor8(Captures.Hex("02 00 40 1C")));
        Assert.Equal((byte)0x00, Wire.Xor8(Captures.Hex("02 00 40 1C 5E")));
    }

    [Theory]
    [InlineData("$GPRMC,041530.00,A,0656.0640,N,07950.5680,E,016.2,090.0,300726,,,A*56", true)]
    [InlineData("$GPRMC,041530.00,A,0656.0640,N,07950.5680,E,016.2,090.0,300726,,,A*57", false)]
    // A sentence with no `*hh` has no checksum. The standard permits it and cheap hardware sends it,
    // so "nothing to check" is not the same answer as "check failed".
    [InlineData("$GPRMC,041530.00,A,0656.0640,N,07950.5680,E,016.2,090.0,300726,,,A", true)]
    [InlineData("no dollar sign here", false)]
    public void The_NMEA_checksum_is_an_XOR_of_the_characters_between_the_markers(string sentence, bool expected) =>
        Assert.Equal(expected, Wire.VerifyNmeaChecksum(sentence));

    [Fact]
    public void An_IMEI_packs_into_eight_BCD_bytes_with_one_padding_nibble()
    {
        // Eight bytes are sixteen nibbles and an IMEI is fifteen digits, so a GT06 login reads back
        // with a leading zero; the identifier is the significant digits.
        Assert.Equal(
            Captures.Imei, Wire.ReadBcd(Captures.Hex("03 56 93 80 35 64 38 09"), trimLeadingZeros: true));

        Assert.Equal(
            "0" + Captures.Imei, Wire.ReadBcd(Captures.Hex("03 56 93 80 35 64 38 09")));

        // Round-trips through the writer, which is what addresses a JT/T 808 downlink frame.
        Assert.Equal(Captures.Hex("03 56 93 80 35 64 38 09"), Wire.WriteBcd(Captures.Imei, 8));
    }

    [Fact]
    public void A_non_decimal_nibble_is_not_BCD()
    {
        // 0xF padding appears in some JT/T 808 implementations, but only as a trailer; guessing at one
        // in the middle would fabricate an identity out of a corrupt frame that passed its XOR.
        Assert.Null(Wire.ReadBcd(Captures.Hex("03 56 9A 80")));
    }

    /// <summary>
    /// The degrees field is variable width and the split is counted back from the decimal point.
    /// </summary>
    /// <remarks>
    /// A longitude read with a fixed two-digit degree field is out by a factor of ten for everything
    /// east of 100° — which is most of Asia, and none of the test data anybody writes first.
    /// </remarks>
    [Theory]
    [InlineData("0656.0640", 'N', 6.9344)]
    [InlineData("0656.0640", 'S', -6.9344)]
    [InlineData("07950.5680", 'E', 79.8428)]
    [InlineData("07950.5680", 'W', -79.8428)]
    [InlineData("11402.5854", 'E', 114.043_09)]
    public void A_degrees_minutes_coordinate_becomes_signed_degrees(string value, char hemisphere, double expected) =>
        Assert.Equal(expected, Wire.ReadDegreesMinutes(value, hemisphere)!.Value, precision: 5);

    [Fact]
    public void An_unknown_hemisphere_letter_is_refused_rather_than_assumed()
    {
        Assert.Null(Wire.ReadDegreesMinutes("0656.0640", 'X'));
        Assert.Null(Wire.ReadDegreesMinutes(string.Empty, 'N'));
    }

    [Fact]
    public void A_BCD_timestamp_is_read_in_the_devices_own_time_zone()
    {
        // JT/T 808 §8.18 writes the location time as BCD YY-MM-DD-hh-mm-ss in Beijing time.
        Assert.Equal(
            Captures.CapturedAt,
            Wire.ReadBcdTimestamp(Captures.Hex("26 07 30 12 15 30"), TimeSpan.FromHours(8)));

        // The same bytes on a unit re-flashed for another market.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 30, 12, 15, 30, TimeSpan.Zero),
            Wire.ReadBcdTimestamp(Captures.Hex("26 07 30 12 15 30"), TimeSpan.Zero));
    }

    [Fact]
    public void A_stamp_that_is_not_a_real_moment_is_null_rather_than_a_throw()
    {
        // A tracker with a flat backup cell reports 00-00-00 until the time-sync exchange fixes it,
        // and a corrupt frame that passed its checksum can name month 19. Either way the frame may
        // still carry a usable identity, so one bad stamp must not take the socket down.
        Assert.Null(Wire.ReadBinaryTimestamp(Captures.Hex("00 00 00 00 00 00"), TimeSpan.Zero));
        Assert.Null(Wire.ReadBinaryTimestamp(Captures.Hex("1A 13 1E 04 0F 1E"), TimeSpan.Zero));
        Assert.Null(Wire.ReadBinaryTimestamp(Captures.Hex("1A 02 1F 04 0F 1E"), TimeSpan.Zero));

        // A leap second is clamped, not refused: some receivers emit :60 and the second is real.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 30, 4, 15, 59, TimeSpan.Zero),
            Wire.ReadBinaryTimestamp(Captures.Hex("1A 07 1E 04 0F 3C"), TimeSpan.Zero));
    }

    [Fact]
    public void An_identifier_off_an_ASCII_wire_is_digits_or_nothing()
    {
        Assert.Equal(Captures.Imei, Wire.NormaliseIdentity("356938035643809"));
        Assert.Equal(Captures.Imei, Wire.NormaliseIdentity(" 356938035643809 "));
        Assert.Equal(Captures.Imei, Wire.NormaliseIdentity("0356938035643809"));

        // The value becomes a Redis key, so leniency here is not a kindness.
        Assert.Null(Wire.NormaliseIdentity("35693803564380X"));
        Assert.Null(Wire.NormaliseIdentity(string.Empty));
        Assert.Null(Wire.NormaliseIdentity("0000"));
    }
}
