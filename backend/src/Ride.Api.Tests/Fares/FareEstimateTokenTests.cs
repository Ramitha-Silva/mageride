using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Ride.Tests.Fares;

/// <summary>
/// The <c>fareEstimateToken</c> is the whole of what stops a client naming its own price, so its
/// three refusals — forged, tampered, stale — are asserted rather than assumed.
/// </summary>
public sealed class FareEstimateTokenTests
{
    private const string Key = "mageride-c022-test-fare-estimate-key";

    private static readonly GeoPoint Pickup = new(6.9344, 79.8428);
    private static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    private static FareEstimateTokenCodec Codec(
        string key = Key, TimeSpan? ttl = null, TimeProvider? timeProvider = null) =>
        new(
            Options.Create(new FareEstimateTokenOptions
            {
                EstimateTokenKey = key,
                EstimateTokenTtl = ttl ?? TimeSpan.FromMinutes(15),
            }),
            timeProvider);

    [Fact]
    public void A_quote_round_trips_through_its_token()
    {
        var codec = Codec();

        var token = codec.Issue("three_wheeler", "passenger", 78_480, 0, 9.56, Pickup, Dropoff);

        Assert.True(codec.TryRead(token, out var claims, out var failure));
        Assert.Equal(FareEstimateTokenFailure.None, failure);
        Assert.Equal("three_wheeler", claims.VehicleType);
        Assert.Equal("passenger", claims.Kind);
        Assert.Equal(78_480, claims.AmountMinor);
        Assert.Equal(0, claims.SurchargeMinor);
        Assert.Equal(9.56, claims.DistanceKm, 6);
        Assert.Equal(Pickup.Latitude, claims.PickupLat, 6);
        Assert.Equal(Dropoff.Longitude, claims.DropoffLng, 6);
        Assert.Equal("LKR", FareEstimateClaims.Currency);
    }

    [Fact]
    public void The_token_is_the_documented_three_part_form()
    {
        var parts = Codec().Issue("sedan", "passenger", 10_000, 0, 1.0, Pickup, Dropoff).Split('.');

        Assert.Equal(3, parts.Length);
        Assert.Equal(FareEstimateTokenCodec.Prefix, parts[0]);
        // base64url: no padding, no '+' and no '/'.
        Assert.All(parts[1..], part => Assert.DoesNotContain('=', part));
        Assert.All(parts[1..], part => Assert.DoesNotContain('+', part));
        Assert.All(parts[1..], part => Assert.DoesNotContain('/', part));
    }

    /// <summary>The attack the token exists for: re-price a van ride at a motorbike's fare.</summary>
    [Fact]
    public void A_rewritten_amount_does_not_verify()
    {
        var codec = Codec();
        var token = codec.Issue("van", "passenger", 200_000, 0, 12.0, Pickup, Dropoff);

        var parts = token.Split('.');
        var forgedClaims = codec.Issue("van", "passenger", 1_000, 0, 12.0, Pickup, Dropoff).Split('.')[1];
        var forged = $"{parts[0]}.{forgedClaims}.{parts[2]}";

        Assert.False(codec.TryRead(forged, out _, out var failure));
        Assert.Equal(FareEstimateTokenFailure.BadSignature, failure);
    }

    [Fact]
    public void A_token_from_another_key_does_not_verify()
    {
        var token = Codec("a-completely-different-signing-key-32").Issue(
            "sedan", "passenger", 50_000, 0, 5.0, Pickup, Dropoff);

        Assert.False(Codec().TryRead(token, out _, out var failure));
        Assert.Equal(FareEstimateTokenFailure.BadSignature, failure);
    }

    [Fact]
    public void An_expired_quote_is_refused()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));
        var codec = Codec(ttl: TimeSpan.FromMinutes(15), timeProvider: clock);

        var token = codec.Issue("flex", "passenger", 60_000, 0, 6.0, Pickup, Dropoff);
        Assert.True(codec.TryRead(token, out _, out _));

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.False(codec.TryRead(token, out _, out var failure));
        Assert.Equal(FareEstimateTokenFailure.Expired, failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    // Right shape, wrong version marker: a future format must fail closed, not be read as this one.
    [InlineData("mrf2.eyJ2dCI6InNlZGFuIn0.c2ln")]
    [InlineData("mrf1.only-two-parts")]
    [InlineData("mrf1.!!!not-base64!!!.c2ln")]
    public void A_malformed_token_is_refused(string? token)
    {
        Assert.False(Codec().TryRead(token, out _, out var failure));
        Assert.Equal(FareEstimateTokenFailure.Malformed, failure);
    }

    [Fact]
    public void A_codec_without_a_key_refuses_to_start()
    {
        var options = Options.Create(new FareEstimateTokenOptions { EstimateTokenKey = "  " });

        Assert.Throws<InvalidOperationException>(() => new FareEstimateTokenCodec(options));
    }
}
