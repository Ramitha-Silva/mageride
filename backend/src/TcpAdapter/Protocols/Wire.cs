using System.Globalization;
using System.Text;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// The wire primitives the four codecs share: three different checksums, BCD, and the unit
/// conversions that turn a protocol's numbers into the canonical sample's.
/// </summary>
/// <remarks>
/// They live together because every one of them is a place a decoder can be subtly wrong in a way no
/// exception reports — a reflected CRC table, a knot read as a km/h, a BCD nibble pair swapped — and
/// the golden frames in the test suite are asserted against these directly as well as through the
/// codecs.
/// </remarks>
public static class Wire
{
    /// <summary>Knots to metres per second. NMEA and H02 both report speed in knots.</summary>
    public const double MetresPerSecondPerKnot = 0.514_444_444_444;

    /// <summary>Kilometres per hour to metres per second — GT06's speed byte.</summary>
    public const double MetresPerSecondPerKph = 1000.0 / 3600.0;

    /// <summary>
    /// CRC-16/X-25, which the GT06 documentation calls "CRC-ITU".
    /// </summary>
    /// <remarks>
    /// Reflected polynomial 0x8408 (0x1021 reversed), initial value 0xFFFF, final XOR 0xFFFF. Every
    /// part of that is load-bearing: the same 0x1021 polynomial unreflected with a zero init is
    /// CRC-CCITT and produces a different digest for the same bytes, so a decoder using it rejects
    /// every genuine frame and a well-formed forgery is no harder to make than before.
    /// </remarks>
    public static ushort Crc16X25(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
            }
        }

        return (ushort)(~crc & 0xFFFF);
    }

    /// <summary>JT/T 808's check code: a plain XOR of every byte from the message id to the body end.</summary>
    public static byte Xor8(ReadOnlySpan<byte> data)
    {
        byte check = 0;

        foreach (var value in data)
        {
            check ^= value;
        }

        return check;
    }

    /// <summary>
    /// NMEA 0183's checksum: XOR of the characters between <c>$</c> and <c>*</c>, printed as two
    /// upper-case hex digits.
    /// </summary>
    /// <remarks>
    /// <paramref name="sentence"/> is the whole sentence including the leading <c>$</c> and the
    /// <c>*hh</c> suffix; both are excluded from the digest. A sentence with no <c>*</c> has no
    /// checksum, which the standard permits — <see cref="VerifyNmeaChecksum"/> reports that as
    /// "nothing to check" rather than as a failure.
    /// </remarks>
    public static bool VerifyNmeaChecksum(ReadOnlySpan<char> sentence)
    {
        var start = sentence.IndexOfAny('$', '!');

        if (start < 0)
        {
            return false;
        }

        var star = sentence.LastIndexOf('*');

        if (star < 0)
        {
            // Unchecksummed sentence. Permitted by the standard and common on cheap hardware.
            return true;
        }

        if (star + 3 > sentence.Length
            || !byte.TryParse(sentence[(star + 1)..(star + 3)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        byte actual = 0;

        for (var index = start + 1; index < star; index++)
        {
            actual ^= (byte)sentence[index];
        }

        return actual == expected;
    }

    /// <summary>
    /// Reads packed BCD as its digit string — two digits per byte, high nibble first.
    /// </summary>
    /// <param name="data">The BCD bytes.</param>
    /// <param name="trimLeadingZeros">
    /// Strip leading zeros. A 15-digit IMEI packs into 8 bytes with one padding nibble, so a GT06
    /// login's terminal id reads back as 16 digits with a leading <c>0</c>; the identifier is the
    /// significant digits.
    /// </param>
    /// <returns>The digits, or <see langword="null"/> when a nibble is not a decimal digit.</returns>
    public static string? ReadBcd(ReadOnlySpan<byte> data, bool trimLeadingZeros = false)
    {
        var digits = new char[data.Length * 2];

        for (var index = 0; index < data.Length; index++)
        {
            var high = data[index] >> 4;
            var low = data[index] & 0x0F;

            if (high > 9 || low > 9)
            {
                // 0xF padding is legal in some JT/T 808 implementations, but only as a trailing
                // filler; a non-decimal nibble anywhere else means this is not BCD and guessing
                // would fabricate an identity.
                return null;
            }

            digits[(index * 2) + 0] = (char)('0' + high);
            digits[(index * 2) + 1] = (char)('0' + low);
        }

        var text = new string(digits);

        if (!trimLeadingZeros)
        {
            return text;
        }

        var trimmed = text.TrimStart('0');

        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>Writes a digit string as packed BCD, right-aligned in <paramref name="length"/> bytes.</summary>
    /// <remarks>
    /// Refuses digits that do not fit rather than truncating them. A silently shortened identity is a
    /// frame addressed to a different device — which is exactly what a 15-digit IMEI written into
    /// JT/T 808-2013's six-byte terminal-number field would be.
    /// </remarks>
    public static byte[] WriteBcd(string digits, int length)
    {
        ArgumentException.ThrowIfNullOrEmpty(digits);

        if (digits.Length > length * 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(digits), $"'{digits}' does not fit in {length} BCD bytes ({length * 2} digits).");
        }

        var padded = digits.PadLeft(length * 2, '0');
        var bytes = new byte[length];

        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(((padded[index * 2] - '0') << 4) | (padded[(index * 2) + 1] - '0'));
        }

        return bytes;
    }

    /// <summary>
    /// Reads a six-byte BCD <c>YYMMDDhhmmss</c> stamp as an instant in
    /// <paramref name="deviceOffset"/>.
    /// </summary>
    /// <remarks>
    /// The century is not on the wire. 2000 is added, which is what every implementation of both
    /// protocols does and what keeps a 2026 frame in 2026; the alternative — a sliding window — would
    /// make the same bytes decode differently depending on when they were read, and a replayed
    /// backlog is exactly the case where that matters.
    /// </remarks>
    public static DateTimeOffset? ReadBcdTimestamp(ReadOnlySpan<byte> data, TimeSpan deviceOffset)
    {
        if (data.Length < 6)
        {
            return null;
        }

        var digits = ReadBcd(data[..6]);

        return digits is null ? null : ReadTimestamp(digits, deviceOffset);
    }

    /// <summary>
    /// Reads six binary bytes as <c>YY MM DD hh mm ss</c> — GT06's date/time field.
    /// </summary>
    public static DateTimeOffset? ReadBinaryTimestamp(ReadOnlySpan<byte> data, TimeSpan deviceOffset)
    {
        if (data.Length < 6)
        {
            return null;
        }

        return Compose(
            2000 + data[0], data[1], data[2], data[3], data[4], data[5], deviceOffset);
    }

    /// <summary>Reads a <c>YYMMDDhhmmss</c> digit string.</summary>
    public static DateTimeOffset? ReadTimestamp(string digits, TimeSpan deviceOffset)
    {
        if (digits.Length < 12)
        {
            return null;
        }

        static int Two(string text, int at) =>
            int.Parse(text.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture);

        return Compose(
            2000 + Two(digits, 0), Two(digits, 2), Two(digits, 4),
            Two(digits, 6), Two(digits, 8), Two(digits, 10), deviceOffset);
    }

    /// <summary>
    /// Composes a UTC instant, or <see langword="null"/> when the parts are not a real moment.
    /// </summary>
    /// <remarks>
    /// A tracker with a flat backup cell reports 00-00-00, and a corrupt frame that passed its
    /// checksum can report month 19. Both must be a null rather than an exception: the frame may still
    /// carry a usable identity, and one bad stamp must not take the socket down.
    /// </remarks>
    public static DateTimeOffset? Compose(
        int year, int month, int day, int hour, int minute, int second, TimeSpan deviceOffset)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60)
        {
            return null;
        }

        try
        {
            // A leap second (:60) is clamped rather than refused — some receivers emit it and the
            // second it names is a real one.
            var normalised = second == 60 ? 59 : second;

            return new DateTimeOffset(year, month, day, hour, minute, normalised, deviceOffset)
                .ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            // 31 February and friends.
            return null;
        }
    }

    /// <summary>
    /// Converts an NMEA/H02 <c>ddmm.mmmm</c> coordinate plus its hemisphere letter into signed
    /// degrees.
    /// </summary>
    /// <remarks>
    /// The degrees field is variable width — two digits for a latitude, up to three for a longitude —
    /// so the split is counted back from the decimal point rather than forward from the start. A
    /// longitude read with a fixed two-digit degree field is out by a factor of ten for every point
    /// east of 100°, which is most of Asia and none of the test data anybody writes first.
    /// </remarks>
    public static double? ReadDegreesMinutes(ReadOnlySpan<char> value, char hemisphere)
    {
        if (value.IsEmpty)
        {
            return null;
        }

        var dot = value.IndexOf('.');
        var minutesStart = (dot < 0 ? value.Length : dot) - 2;

        if (minutesStart < 1)
        {
            return null;
        }

        if (!int.TryParse(value[..minutesStart], NumberStyles.None, CultureInfo.InvariantCulture, out var degrees)
            || !double.TryParse(value[minutesStart..], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var signed = degrees + (minutes / 60.0);

        return hemisphere switch
        {
            'S' or 's' or 'W' or 'w' => -signed,
            'N' or 'n' or 'E' or 'e' => signed,
            _ => null,
        };
    }

    /// <summary>Reads a big-endian unsigned 16-bit value.</summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    /// <summary>Reads a big-endian unsigned 32-bit value.</summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    /// <summary>Writes a big-endian unsigned 16-bit value.</summary>
    public static void WriteUInt16(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>Writes a big-endian unsigned 32-bit value.</summary>
    public static void WriteUInt32(Span<byte> destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)((value >> 16) & 0xFF);
        destination[offset + 2] = (byte)((value >> 8) & 0xFF);
        destination[offset + 3] = (byte)(value & 0xFF);
    }

    /// <summary>Digits only, leading zeros stripped — how an identifier off any of the four wires is normalised.</summary>
    public static string? NormaliseIdentity(ReadOnlySpan<char> raw)
    {
        var builder = new StringBuilder(raw.Length);

        foreach (var character in raw)
        {
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
            else if (character is ' ' or '\t' or '\r' or '\n' or ':' or '-')
            {
                continue;
            }
            else
            {
                // Anything else means this is not an identifier — an ASCII protocol's field is not
                // a place to be lenient about, because the value becomes a Redis key.
                return null;
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var digits = builder.ToString().TrimStart('0');

        return digits.Length == 0 ? null : digits;
    }
}
