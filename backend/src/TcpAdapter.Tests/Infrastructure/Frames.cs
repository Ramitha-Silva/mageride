using System.Globalization;
using MageRide.TcpAdapter.Protocols;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// Frames built at an arbitrary instant, for the tests that need a <i>live</i> sample.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Captures"/>'s frames are fixed so they can be asserted byte for byte, and that makes
/// them permanently older than <c>Adapter:ReplayAge</c> — so they route to <c>pos/replay</c>, which is
/// correct behaviour and the wrong thing to assert the live path with. These are the same layouts with
/// the timestamp moved.
/// </para>
/// <para>
/// Every field except the stamp is identical to the corresponding capture, so a test that uses one of
/// these is still asserting the golden coordinates and speed.
/// </para>
/// </remarks>
internal static class Frames
{
    /// <summary>A GT06 login for an arbitrary IMEI, with the right CRC.</summary>
    public static byte[] Gt06Login(string imei, ushort serial = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        var content = new byte[10];
        Wire.WriteBcd("0" + imei, 8).CopyTo(content.AsSpan());

        // The two-byte model code a Concox unit appends. Not an identity; the adapter ignores it.
        content[8] = 0x36;
        content[9] = 0x08;

        return Gt06Codec.BuildFrame(Gt06Codec.ProtocolLogin, content, serial);
    }

    /// <summary>
    /// A GT06 <c>0x12</c> location frame at <paramref name="capturedAt"/>, carrying
    /// <see cref="Captures"/>'s coordinates.
    /// </summary>
    public static byte[] Gt06Position(DateTimeOffset capturedAt, ushort serial = 2)
    {
        var utc = capturedAt.ToUniversalTime();
        var content = new byte[26];

        content[0] = (byte)(utc.Year % 100);
        content[1] = (byte)utc.Month;
        content[2] = (byte)utc.Day;
        content[3] = (byte)utc.Hour;
        content[4] = (byte)utc.Minute;
        content[5] = (byte)utc.Second;

        // High nibble is the GPS block length, low nibble the satellite count.
        content[6] = 0xC9;

        Wire.WriteUInt32(content, 7, (uint)Math.Round(Captures.Latitude * 1_800_000));
        Wire.WriteUInt32(content, 11, (uint)Math.Round(Captures.Longitude * 1_800_000));

        // 30 km/h, as a whole byte of km/h.
        content[15] = 30;

        // Positioned (bit 12), north (bit 10), east (bit 11 clear), course 90.
        Wire.WriteUInt16(content, 16, (ushort)(0x1000 | 0x0400 | 90));

        // LBS: MCC 413 (Sri Lanka), MNC 2, and a cell nobody reads.
        Captures.Hex("01 9D 02 12 34 00 AB CD").CopyTo(content.AsSpan(18));

        return Gt06Codec.BuildFrame(Gt06Codec.ProtocolLocation, content, serial);
    }

    /// <summary>An H02 <c>V1</c> line at <paramref name="capturedAt"/>, with the ACC line high.</summary>
    public static string H02Position(string imei, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        var utc = capturedAt.ToUniversalTime();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"*HQ,{imei},V1,{utc:HHmmss},A,{DegreesMinutes(Captures.Latitude, 2)},N," +
            $"{DegreesMinutes(Captures.Longitude, 3)},E,016.2,090,{utc:ddMMyy},FFFFFBFF#");
    }

    /// <summary>
    /// A generic-NMEA datagram at <paramref name="capturedAt"/> — RMC and GGA for the same second.
    /// </summary>
    public static string NmeaDatagram(string imei, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imei);

        var utc = capturedAt.ToUniversalTime();
        var latitude = DegreesMinutes(Captures.Latitude, 2);
        var longitude = DegreesMinutes(Captures.Longitude, 3);

        var rmc = Checksum(string.Create(
            CultureInfo.InvariantCulture,
            $"GPRMC,{utc:HHmmss}.00,A,{latitude},N,{longitude},E,016.2,090.0,{utc:ddMMyy},,,A"));

        var gga = Checksum(string.Create(
            CultureInfo.InvariantCulture,
            $"GPGGA,{utc:HHmmss}.00,{latitude},N,{longitude},E,1,09,0.9,5.0,M,,M,,"));

        return $"IMEI:{imei};{rmc}\r\n{gga}";
    }

    /// <summary>The instant a frame's whole-second stamp decodes back to.</summary>
    /// <remarks>
    /// Every one of these protocols stamps to the second, so a test comparing a decoded
    /// <c>sampleTs</c> against the instant it asked for has to compare against the truncation.
    /// </remarks>
    public static DateTimeOffset ToWholeSecond(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();

        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
    }

    /// <summary><c>ddmm.mmmm</c> / <c>dddmm.mmmm</c>, as NMEA and H02 write a coordinate.</summary>
    private static string DegreesMinutes(double value, int degreeWidth)
    {
        var degrees = (int)Math.Abs(value);
        var minutes = (Math.Abs(value) - degrees) * 60;

        return degrees.ToString($"D{degreeWidth}", CultureInfo.InvariantCulture)
               + minutes.ToString("00.0000", CultureInfo.InvariantCulture);
    }

    /// <summary>Wraps a sentence body in <c>$…*hh</c>.</summary>
    private static string Checksum(string body)
    {
        byte checksum = 0;

        foreach (var character in body)
        {
            checksum ^= (byte)character;
        }

        return string.Create(CultureInfo.InvariantCulture, $"${body}*{checksum:X2}");
    }
}
