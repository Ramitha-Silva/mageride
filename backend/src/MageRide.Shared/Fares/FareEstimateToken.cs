using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Fares;

/// <summary>
/// What a <c>fareEstimateToken</c> binds: the quote fare-svc gave, and the trip it was given for.
/// </summary>
/// <param name="VehicleType">The tier the quote is for. A token issued for a motorbike cannot book a van.</param>
/// <param name="Kind">`passenger` or `package` (D3' <c>GET /v1/fare/estimate</c>'s <c>kind</c>).</param>
/// <param name="AmountMinor">Total payable, LKR minor units (D5' §1.3).</param>
/// <param name="SurchargeMinor">Peak + night portion of <paramref name="AmountMinor"/>, for receipts.</param>
/// <param name="DistanceKm">Distance the quote was priced on (D5' §1.2).</param>
/// <param name="PickupLat">Quoted pickup, carried for audit and for the receipt trail.</param>
public sealed record FareEstimateClaims(
    [property: JsonPropertyName("vt")] string VehicleType,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("amt")] long AmountMinor,
    [property: JsonPropertyName("sur")] long SurchargeMinor,
    [property: JsonPropertyName("km")] double DistanceKm,
    [property: JsonPropertyName("flat")] double PickupLat,
    [property: JsonPropertyName("flng")] double PickupLng,
    [property: JsonPropertyName("tlat")] double DropoffLat,
    [property: JsonPropertyName("tlng")] double DropoffLng,
    [property: JsonPropertyName("iat")] long IssuedAtUnix,
    [property: JsonPropertyName("exp")] long ExpiresAtUnix)
{
    /// <summary>LKR is the only currency the platform transacts in (D3' <c>Currency</c>).</summary>
    public const string Currency = "LKR";

    public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(IssuedAtUnix);

    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);
}

/// <summary>Why a token was refused. Every value maps to <c>400 invalid-fare-token</c>.</summary>
public enum FareEstimateTokenFailure
{
    None,

    /// <summary>Absent, wrong prefix, wrong segment count, or not decodable.</summary>
    Malformed,

    /// <summary>Decoded, but the HMAC does not match — forged or issued under another key.</summary>
    BadSignature,

    /// <summary>Past <c>exp</c>. The passenger must re-quote (D5' §1.4).</summary>
    Expired,
}

/// <summary>
/// Issues and verifies the opaque <c>fareEstimateToken</c> that binds a quoted price.
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>mrf1.&lt;base64url(claims json)&gt;.&lt;base64url(hmac-sha256)&gt;</c>. The MAC covers
/// the prefix and the encoded claims, so neither the version marker nor the payload can be
/// swapped. It is deliberately not a JWT: this token is never presented as a credential, carries
/// no subject and is verified by exactly one service, so a JOSE header would add parsing surface
/// and nothing else.
/// </para>
/// <para>
/// **This lives in the kernel because two services share it** — fare-svc mints, ride-svc verifies
/// (backend/CLAUDE.md: "cross-cutting code goes there, not into a service"). C049/C050 replace the
/// C022 fare *stub*, not this format; if they change it, both sides change together.
/// </para>
/// </remarks>
public sealed class FareEstimateTokenCodec
{
    /// <summary>Version marker. A future format bumps it so an old token fails closed.</summary>
    public const string Prefix = "mrf1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // The MAC is computed over the exact bytes, so nothing here may vary between processes.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly byte[] _key;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;

    public FareEstimateTokenCodec(IOptions<FareEstimateTokenOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.EstimateTokenKey))
        {
            throw new InvalidOperationException(
                $"{FareEstimateTokenOptions.SectionName}:{nameof(FareEstimateTokenOptions.EstimateTokenKey)} is required — " +
                "without it every fare quote is forgeable.");
        }

        _key = Encoding.UTF8.GetBytes(value.EstimateTokenKey);
        _ttl = value.EstimateTokenTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How long a freshly issued token stays valid.</summary>
    public TimeSpan Ttl => _ttl;

    /// <summary>Stamps <paramref name="claims"/> with the configured TTL and signs them.</summary>
    public string Issue(
        string vehicleType,
        string kind,
        long amountMinor,
        long surchargeMinor,
        double distanceKm,
        Primitives.GeoPoint pickup,
        Primitives.GeoPoint dropoff)
    {
        var now = _timeProvider.GetUtcNow();

        return Issue(new FareEstimateClaims(
            VehicleType: vehicleType,
            Kind: kind,
            AmountMinor: amountMinor,
            SurchargeMinor: surchargeMinor,
            DistanceKm: distanceKm,
            PickupLat: pickup.Latitude,
            PickupLng: pickup.Longitude,
            DropoffLat: dropoff.Latitude,
            DropoffLng: dropoff.Longitude,
            IssuedAtUnix: now.ToUnixTimeSeconds(),
            ExpiresAtUnix: now.Add(_ttl).ToUnixTimeSeconds()));
    }

    /// <inheritdoc cref="Issue(string, string, long, long, double, Primitives.GeoPoint, Primitives.GeoPoint)"/>
    public string Issue(FareEstimateClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, SerializerOptions));
        var signed = $"{Prefix}.{payload}";

        return $"{signed}.{Base64UrlEncode(Sign(signed))}";
    }

    /// <summary>
    /// Verifies signature and expiry and returns the claims. The signature is checked before the
    /// claims are trusted for anything, including the expiry.
    /// </summary>
    public bool TryRead(
        string? token,
        [NotNullWhen(true)] out FareEstimateClaims? claims,
        out FareEstimateTokenFailure failure)
    {
        claims = null;
        failure = FareEstimateTokenFailure.Malformed;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64UrlDecode(parts[1]);
            signature = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Sign($"{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            failure = FareEstimateTokenFailure.BadSignature;
            return false;
        }

        FareEstimateClaims? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FareEstimateClaims>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null)
        {
            return false;
        }

        if (parsed.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            failure = FareEstimateTokenFailure.Expired;
            return false;
        }

        claims = parsed;
        failure = FareEstimateTokenFailure.None;
        return true;
    }

    private byte[] Sign(string value) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
