using System.Buffers.Binary;

namespace MageRide.Ride.Domain;

/// <summary>
/// Reads the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").
/// </summary>
/// <remarks>
/// <c>rides.rides.client_request_id</c> is a Postgres <c>UUID</c>, but ADD §11.13 has the mobile
/// apps generate **ULIDs** ("Idempotency-Key = ULID (local, monotonic)") and the contract types
/// <c>clientRequestId</c> as <c>Ulid</c>. A ULID is 128 bits in Crockford base32 — the same value
/// as a UUID in a different alphabet — so decoding it here is what stops a correct client from
/// getting a 400 on every booking. Nothing converts back: the value is only ever compared, and
/// <c>ux_rides_idem</c> compares the decoded bits.
/// </remarks>
public static class Ulids
{
    private const int EncodedLength = 26;

    /// <summary>Crockford base32 without I, L, O and U (they collide with 1, 1, 0 and V).</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// 26 base32 characters carry 130 bits, so the leading character holds three significant bits
    /// and two of padding. A value above 7 there has overflowed 128 bits and is not a ULID.
    /// </summary>
    private const int MaxLeadingSymbol = 7;

    /// <summary>Accepts a canonical UUID or a 26-character ULID and yields the 128-bit value.</summary>
    public static bool TryParse(string? value, out Guid parsed)
    {
        parsed = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        return Guid.TryParse(trimmed, out parsed) || TryParseUlid(trimmed, out parsed);
    }

    private static bool TryParseUlid(string value, out Guid parsed)
    {
        parsed = Guid.Empty;

        if (value.Length != EncodedLength)
        {
            return false;
        }

        UInt128 accumulator = 0;

        for (var i = 0; i < value.Length; i++)
        {
            var symbol = Alphabet.IndexOf(char.ToUpperInvariant(value[i]), StringComparison.Ordinal);
            if (symbol < 0 || (i == 0 && symbol > MaxLeadingSymbol))
            {
                return false;
            }

            accumulator = (accumulator << 5) | (uint)symbol;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, accumulator);

        parsed = new Guid(bytes, bigEndian: true);
        return true;
    }
}
