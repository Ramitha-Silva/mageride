using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// One frame from each of D6' §4.1's four protocol families, and the fix every one of them carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>All four describe the same vehicle at the same instant in the same place</b> — Colombo Fort,
/// 6.9344 N 79.8428 E, 2026-07-30T04:15:30Z, heading 90°, 30 km/h — so a decoder that is wrong about
/// a unit, a hemisphere bit or a time zone produces a number that visibly disagrees with the other
/// three rather than one that merely looks odd on its own.
/// </para>
/// <para>
/// <b>They are constructed to the published frame layouts with real checksums, not captured off a
/// device.</b> There is no tracker on this build host, and a "capture" pasted from a vendor forum is
/// a frame nobody can check: what makes these worth asserting against is that every field is derived
/// from the format specification and every checksum is computed by the algorithm the format names —
/// so the one fixed point that <i>is</i> independently attestable, the GT06 documentation's login
/// acknowledgement <c>78 78 05 01 00 01 D9 DC 0D 0A</c>, verifies against the same CRC these use
/// (<c>WireTests</c>).
/// </para>
/// </remarks>
internal static class Captures
{
    /// <summary>The IMEI all four frames present.</summary>
    public const string Imei = "356938035643809";

    /// <summary>An IMEI no binding exists for, for the refusal tests.</summary>
    public const string UnboundImei = "356938035643001";

    /// <summary>Colombo Fort — the latitude every frame encodes.</summary>
    public const double Latitude = 6.9344;

    /// <summary>Colombo Fort — the longitude every frame encodes.</summary>
    public const double Longitude = 79.8428;

    /// <summary>The GNSS instant every frame stamps, in UTC.</summary>
    public static readonly DateTimeOffset CapturedAt =
        new(2026, 7, 30, 4, 15, 30, TimeSpan.Zero);

    /// <summary>30 km/h, which is what GT06 and JT/T 808 encode and what 16.2 knots is to a decimal.</summary>
    public const double SpeedMps = 30 * 1000.0 / 3600.0;

    /// <summary>Course over ground.</summary>
    public const int HeadingDeg = 90;

    /// <summary>
    /// GT06 login, protocol <c>0x01</c>: eight BCD bytes of terminal id plus the two-byte model code.
    /// </summary>
    public static byte[] Gt06Login => Hex("78 78 0F 01 03 56 93 80 35 64 38 09 36 08 00 01 EB 35 0D 0A");

    /// <summary>
    /// The acknowledgement the adapter must write back for <see cref="Gt06Login"/>. A device whose
    /// login goes unanswered re-sends it and eventually reboots its modem.
    /// </summary>
    public static byte[] Gt06LoginAck => Hex("78 78 05 01 00 01 D9 DC 0D 0A");

    /// <summary>
    /// GT06 location, protocol <c>0x12</c>: <c>datetime(6) | gps(1) | lat(4) | lng(4) | speed(1) |
    /// course+status(2) | LBS(8)</c>. Status word <c>0x145A</c> — positioned, north, east, course 90.
    /// </summary>
    public static byte[] Gt06Position => Hex(
        "78 78 1F 12 1A 07 1E 04 0F 1E C9 00 BE 75 80 08 90 F2 B0 1E 14 5A 01 9D 02 12 34 00 AB CD 00 02 52 0D 0D 0A");

    /// <summary>
    /// GT06 status, protocol <c>0x13</c>. Terminal information byte <c>0x02</c> — bit 1 is the ACC
    /// line, so this is ignition on (AL-32).
    /// </summary>
    public static byte[] Gt06IgnitionOn => Hex("78 78 0A 13 02 06 04 00 01 00 03 EE 69 0D 0A");

    /// <summary>The same status frame with ACC low.</summary>
    public static byte[] Gt06IgnitionOff => Gt06Status(terminalInformation: 0x00, serial: 4);

    /// <summary>
    /// JT/T 808 location report <c>0x0200</c> in the <b>2019</b> header shape — properties
    /// <c>0x401C</c> sets the version flag, so a one-byte version and a ten-byte BCD terminal number
    /// follow. Status <c>0x00000003</c> is ACC on and positioned; the BCD stamp is Beijing time.
    /// </summary>
    public static byte[] Jt808Position => Hex(
        "7E 02 00 40 1C 01 00 00 03 56 93 80 35 64 38 09 00 07 00 00 00 00 00 00 00 03 00 69 CF 80 " +
        "04 C2 4D F0 00 05 01 2C 00 5A 26 07 30 12 15 30 74 7E");

    /// <summary>
    /// The same report in the <b>2013</b> header shape, whose six-byte BCD terminal number holds
    /// twelve digits — which cannot be a fifteen-digit IMEI. Decodes fine and authenticates never.
    /// </summary>
    public static byte[] Jt808Position2013 => Hex(
        "7E 02 00 00 1C 93 80 35 64 38 09 00 07 00 00 00 00 00 00 00 03 00 69 CF 80 04 C2 4D F0 " +
        "00 05 01 2C 00 5A 26 07 30 12 15 30 60 7E");

    /// <summary>
    /// JT/T 808 terminal authentication <c>0x0102</c> — the one frame in the four families with a
    /// field a credential fits in.
    /// </summary>
    public static byte[] Jt808Authenticate => Hex(
        "7E 01 02 40 10 01 00 00 03 56 93 80 35 64 38 09 00 06 6D 72 70 31 2E 41 42 43 2E 39 39 39 " +
        "2E 73 2E 67 41 7E");

    /// <summary>
    /// H02 position, message type <c>V1</c>. Speed is in knots — 16.2 kn is 30 km/h. The status word
    /// <c>FFFFFBFF</c> has bit 10 clear, which for this family's active-low flags is ignition on.
    /// </summary>
    public const string H02Position =
        "*HQ,356938035643809,V1,041530,A,0656.0640,N,07950.5680,E,016.2,090,300726,FFFFFBFF#";

    /// <summary>The same line with the pipe separator D6' §4.1 describes the family as using.</summary>
    public static string H02PositionPipeDelimited => H02Position.Replace(',', '|');

    /// <summary>
    /// A generic-NMEA datagram: the <c>IMEI:</c> prefix, then RMC and GGA for the same second. RMC
    /// carries the date and the speed; GGA carries the satellite count and the HDOP.
    /// </summary>
    public const string NmeaDatagram =
        "IMEI:356938035643809;" +
        "$GPRMC,041530.00,A,0656.0640,N,07950.5680,E,016.2,090.0,300726,,,A*56\r\n" +
        "$GPGGA,041530.00,0656.0640,N,07950.5680,E,1,09,0.9,5.0,M,,M,,*73";

    /// <summary>Bytes for an ASCII frame.</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>Parses a whitespace-separated hex string.</summary>
    public static byte[] Hex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        var fields = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[fields.Length];

        for (var index = 0; index < fields.Length; index++)
        {
            bytes[index] = byte.Parse(fields[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>Renders bytes as the hex this file writes them in, for an assertion message.</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Builds a GT06 status frame with an arbitrary terminal information byte.</summary>
    public static byte[] Gt06Status(byte terminalInformation, ushort serial) =>
        MageRide.TcpAdapter.Protocols.Gt06Codec.BuildFrame(
            MageRide.TcpAdapter.Protocols.Gt06Codec.ProtocolStatus,
            [terminalInformation, 0x06, 0x04, 0x00, 0x01],
            serial);
}
