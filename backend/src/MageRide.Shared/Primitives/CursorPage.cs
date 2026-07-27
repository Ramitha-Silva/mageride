using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Shared.Http;

namespace MageRide.Shared.Primitives;

/// <summary>
/// The cursor-pagination envelope every list endpoint returns (D3' §0 "Pagination"):
/// <c>{ "items":[…], "cursor":"opaque|null", "hasMore":bool }</c>.
/// </summary>
/// <param name="Items">The page's rows.</param>
/// <param name="Cursor">
/// Opaque position of the next page, or <see langword="null"/> on the last one. Always present in
/// the payload — D3' §0 spells the field <c>"opaque|null"</c>, and the platform's global
/// <c>WhenWritingNull</c> policy would otherwise drop it and make "last page" indistinguishable
/// from "field missing".
/// </param>
/// <param name="HasMore">Whether another page exists.</param>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Cursor,
    bool HasMore)
{
    public static CursorPage<T> Empty { get; } = new([], null, false);

    /// <summary>
    /// Builds a page from a slab read with <c>LIMIT limit + 1</c> — the extra row is what tells
    /// you whether there is more, without a second count query.
    /// </summary>
    /// <param name="rows">Up to <paramref name="limit"/> + 1 rows.</param>
    /// <param name="limit">Page size that was requested.</param>
    /// <param name="cursorFor">Produces the opaque cursor pointing just past the last returned item.</param>
    public static CursorPage<T> FromOverfetch(IReadOnlyList<T> rows, int limit, Func<T, string> cursorFor)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(cursorFor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.Take(limit).ToArray() : [.. rows];

        return new CursorPage<T>(items, items.Length == 0 ? null : hasMore ? cursorFor(items[^1]) : null, hasMore);
    }

    /// <summary>Projects the items while keeping the cursor and <c>hasMore</c> flag.</summary>
    public CursorPage<TOut> Select<TOut>(Func<T, TOut> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new CursorPage<TOut>([.. Items.Select(selector)], Cursor, HasMore);
    }
}

/// <summary>Parsed <c>?cursor=&amp;limit=</c> query (D3' §0: default 20, max 100).</summary>
public sealed record PageRequest(string? Cursor, int Limit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>Clamps <paramref name="limit"/> into range; a missing or unusable value gives the default.</summary>
    public static PageRequest Create(string? cursor, int? limit) =>
        new(string.IsNullOrWhiteSpace(cursor) ? null : cursor,
            limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit));

    /// <summary>Rows to read so <see cref="CursorPage{T}.FromOverfetch"/> can detect a next page.</summary>
    public int OverfetchLimit => Limit + 1;

    /// <summary>Binds from the query string. Minimal APIs call this via <c>[AsParameters]</c> or directly.</summary>
    public static PageRequest FromQuery(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cursor = request.Query["cursor"].ToString();
        var limit = int.TryParse(request.Query["limit"], out var parsed) ? parsed : (int?)null;

        return Create(cursor, limit);
    }
}

/// <summary>
/// Encodes and decodes the opaque <c>cursor</c> value.
/// </summary>
/// <remarks>
/// <para>
/// The cursor is base64url over a JSON position marker — opaque to clients, but not secret and,
/// on its own, not tamper-evident. Supply a signing key and a modified cursor is
/// rejected instead of being trusted.
/// </para>
/// <para>
/// Signed or not, a decoded cursor is untrusted input: an endpoint must still scope its query by
/// the caller's identity rather than by anything the cursor says.
/// </para>
/// </remarks>
public sealed class CursorCodec(byte[]? signingKey = null)
{
    private const byte UnsignedMarker = (byte)'u';
    private const byte SignedMarker = (byte)'s';
    private const int SignatureLength = 32;

    private readonly byte[]? _signingKey = signingKey is { Length: > 0 } ? signingKey : null;

    /// <summary>An unsigned codec. Adequate when the cursor carries only an ordering position.</summary>
    public static CursorCodec Unsigned { get; } = new();

    /// <summary><see langword="true"/> when cursors are HMAC-signed.</summary>
    public bool IsSigned => _signingKey is not null;

    public string Encode<T>(T position) =>
        EncodeBytes(JsonSerializer.SerializeToUtf8Bytes(position, MageRideJson.Options));

    /// <summary>
    /// Encodes raw payload bytes.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>Encode</c> overload: for a <c>byte[]</c> argument the generic
    /// <see cref="Encode{T}"/> is the better match, so <c>Encode(bytes)</c> would bind to itself
    /// and recurse.
    /// </remarks>
    public string EncodeBytes(ReadOnlySpan<byte> payload)
    {
        if (_signingKey is null)
        {
            Span<byte> unsigned = new byte[payload.Length + 1];
            unsigned[0] = UnsignedMarker;
            payload.CopyTo(unsigned[1..]);
            return Base64Url.EncodeToString(unsigned);
        }

        Span<byte> signed = new byte[payload.Length + 1 + SignatureLength];
        signed[0] = SignedMarker;
        payload.CopyTo(signed[1..]);
        HMACSHA256.HashData(_signingKey, payload, signed[(payload.Length + 1)..]);

        return Base64Url.EncodeToString(signed);
    }

    public bool TryDecodeBytes(string? cursor, out byte[] payload)
    {
        payload = [];

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        byte[] raw;
        try
        {
            raw = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length < 2)
        {
            return false;
        }

        switch (raw[0])
        {
            case UnsignedMarker when _signingKey is null:
                payload = raw[1..];
                return true;

            case SignedMarker when _signingKey is not null:
                if (raw.Length < 1 + SignatureLength)
                {
                    return false;
                }

                var body = raw.AsSpan(1, raw.Length - 1 - SignatureLength);
                Span<byte> expected = stackalloc byte[SignatureLength];
                HMACSHA256.HashData(_signingKey, body, expected);

                if (!CryptographicOperations.FixedTimeEquals(expected, raw.AsSpan(raw.Length - SignatureLength)))
                {
                    return false;
                }

                payload = body.ToArray();
                return true;

            default:
                // Marker does not match this codec's configuration: a cursor minted before signing
                // was turned on (or off), or a forgery. Either way it is not usable.
                return false;
        }
    }

    public bool TryDecode<T>(string? cursor, out T? position)
    {
        position = default;

        if (!TryDecodeBytes(cursor, out var payload))
        {
            return false;
        }

        try
        {
            position = JsonSerializer.Deserialize<T>(payload, MageRideJson.Options);
            return position is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Convenience for the common "cursor is an opaque string" case.</summary>
    public string EncodeString(string value) => EncodeBytes(Encoding.UTF8.GetBytes(value));

    /// <inheritdoc cref="EncodeString"/>
    public bool TryDecodeString(string? cursor, out string value)
    {
        if (TryDecodeBytes(cursor, out var payload))
        {
            value = Encoding.UTF8.GetString(payload);
            return true;
        }

        value = string.Empty;
        return false;
    }
}
