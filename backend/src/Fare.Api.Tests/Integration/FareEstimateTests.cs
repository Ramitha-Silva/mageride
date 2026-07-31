using System.Net;
using MageRide.Fare.Endpoints;
using MageRide.Fare.Tests.Infrastructure;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Tests.Integration;

/// <summary>
/// <c>GET /v1/fare/estimate</c> against the real rate card: the tariff resolved by
/// <c>effective_from</c>, the surcharge windows evaluated in Asia/Colombo, and the token that binds
/// the quote.
/// </summary>
[Collection<FareCollection>]
public sealed class FareEstimateTests(PostgresFixture postgres)
{
    /// <summary>Colombo Fort to Bambalapitiya — about 3 km apart in a straight line.</summary>
    private const string Trip = "fromLat=6.9271&fromLng=79.8612&toLat=6.9010&toLng=79.8740";

    [Fact]
    public async Task An_estimate_is_priced_from_the_seeded_tariff_and_carries_its_token()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");

        var quote = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=three_wheeler", harness.Tokens.Passenger(passenger));

        Assert.Equal("LKR", quote.Currency);
        Assert.Equal(10_000, quote.Breakdown.FirstKmMinor);
        Assert.Equal(8_000, quote.Breakdown.PerKmMinor);
        Assert.True(quote.AmountMinor > 10_000, "a 3 km trip costs more than the first-km charge");
        Assert.StartsWith("mrf1.", quote.FareEstimateToken, StringComparison.Ordinal);

        // 14:30 Colombo is outside every seeded window.
        Assert.Equal(0, quote.Breakdown.PeakSurchargePct);
        Assert.Equal(0, quote.Breakdown.NightSurchargePct);
    }

    /// <summary>
    /// The definition of done: an estimate token cannot be reused for a different pickup/dropoff
    /// pair. The claims carry both endpoints, so the binding is checkable by whoever verifies it.
    /// </summary>
    [Fact]
    public async Task The_token_binds_the_trip_it_was_quoted_for()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");
        var bearer = harness.Tokens.Passenger(passenger);

        var quote = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=three_wheeler", bearer);

        var codec = new FareEstimateTokenCodec(
            Options.Create(new FareEstimateTokenOptions { EstimateTokenKey = FareHarness.EstimateTokenKey }),
            harness.Clock);

        Assert.True(codec.TryRead(quote.FareEstimateToken, out var claims, out _));

        // The quoted endpoints are inside the signed claims, to the coordinate.
        Assert.Equal(6.9271, claims.PickupLat, 4);
        Assert.Equal(79.8612, claims.PickupLng, 4);
        Assert.Equal(6.9010, claims.DropoffLat, 4);
        Assert.Equal(79.8740, claims.DropoffLng, 4);
        Assert.Equal("three_wheeler", claims.VehicleType);
        Assert.Equal(quote.AmountMinor, claims.AmountMinor);

        // A quote for a different trip is a different token with different claims — presenting the
        // first for the second is what ride-svc's RequireFareEstimate refuses.
        var elsewhere = await harness.GetAsync<FareEstimateResponse>(
            "/v1/fare/estimate?fromLat=7.2906&fromLng=80.6337&toLat=7.2500&toLng=80.6000"
            + "&vehicleType=three_wheeler",
            bearer);

        Assert.NotEqual(quote.FareEstimateToken, elsewhere.FareEstimateToken);

        Assert.True(codec.TryRead(elsewhere.FareEstimateToken, out var other, out _));
        Assert.NotEqual(claims.PickupLat, other.PickupLat);

        // And the signature covers them: flipping a coordinate in the payload invalidates the token
        // rather than repricing it.
        var tampered = Tamper(quote.FareEstimateToken);
        Assert.False(codec.TryRead(tampered, out _, out var failure));
        Assert.Equal(FareEstimateTokenFailure.BadSignature, failure);
    }

    /// <summary>
    /// The night window wraps midnight, so a 23:00 Colombo quote is surcharged and a 14:30 one is
    /// not. This is the seeded 22:00–05:00 row, read through the service.
    /// </summary>
    [Fact]
    public async Task A_quote_inside_the_wrapping_night_window_is_surcharged()
    {
        // 18:00 UTC is 23:30 in Colombo — inside the night window, on the far side of midnight UTC.
        await using var harness = await FareHarness.StartAsync(
            postgres, now: new DateTimeOffset(2026, 7, 30, 18, 0, 0, TimeSpan.Zero));

        var passenger = await harness.Seed.UserAsync("passenger");

        var quote = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=three_wheeler", harness.Tokens.Passenger(passenger));

        Assert.Equal(15, quote.Breakdown.NightSurchargePct);
        Assert.Equal(0, quote.Breakdown.PeakSurchargePct);
    }

    /// <summary>The morning peak, from the other seeded window.</summary>
    [Fact]
    public async Task A_quote_inside_the_morning_peak_is_surcharged()
    {
        // 02:30 UTC is 08:00 in Colombo.
        await using var harness = await FareHarness.StartAsync(
            postgres, now: new DateTimeOffset(2026, 7, 30, 2, 30, 0, TimeSpan.Zero));

        var passenger = await harness.Seed.UserAsync("passenger");

        var quote = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=sedan", harness.Tokens.Passenger(passenger));

        Assert.Equal(20, quote.Breakdown.PeakSurchargePct);
        Assert.Equal(0, quote.Breakdown.NightSurchargePct);
    }

    /// <summary>
    /// §20 seeds no rate for the package-delivery types on purpose: a delivery vehicle cannot be
    /// priced until Finance publishes one, and the answer is a 422 an admin can fix rather than an
    /// invented number.
    /// </summary>
    [Fact]
    public async Task A_vehicle_type_with_no_configured_tariff_is_refused_rather_than_guessed()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");

        using var response = await harness.GetAsync(
            $"/v1/fare/estimate?{Trip}&vehicleType=truck&kind=package", harness.Tokens.Passenger(passenger));

        var (code, _) = await FareHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("route-unavailable", code);
    }

    /// <summary>Mode A carries no fare at all, so its types are not a tier this endpoint prices.</summary>
    [Theory]
    [InlineData("bus")]
    [InlineData("train")]
    public async Task Mode_A_vehicle_types_have_no_fare(string vehicleType)
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");

        using var response = await harness.GetAsync(
            $"/v1/fare/estimate?{Trip}&vehicleType={vehicleType}", harness.Tokens.Passenger(passenger));

        var (code, _) = await FareHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", code);
    }

    /// <summary>MageRide operates in Sri Lanka and nowhere else.</summary>
    [Fact]
    public async Task A_trip_outside_the_operating_area_is_unserviceable()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");

        using var response = await harness.GetAsync(
            "/v1/fare/estimate?fromLat=48.8566&fromLng=2.3522&toLat=48.86&toLng=2.36&vehicleType=sedan",
            harness.Tokens.Passenger(passenger));

        var (code, _) = await FareHarness.ProblemAsync(response);

        Assert.Equal("unserviceable-area", code);
    }

    /// <summary>An estimate is a passenger-facing route and needs a bearer.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_gets_nothing()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        using var response = await harness.GetAsync($"/v1/fare/estimate?{Trip}&vehicleType=sedan");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Migration 1001 versions the tariff by <c>effective_from</c> so a completed ride stays
    /// reconcilable against the rate that priced it. A rate published now changes the next quote and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task A_published_rate_applies_from_its_effective_instant_and_not_before()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var passenger = await harness.Seed.UserAsync("passenger");
        var bearer = harness.Tokens.Passenger(passenger);

        var before = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=sedan", bearer);

        // Finance doubles the sedan rate, effective an hour ago.
        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                INSERT INTO fares.tariffs
                  (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct, night_surcharge_pct, effective_from)
                VALUES ('sedan', 30000, 20000, 20, 15, @EffectiveFrom);
                """,
                new { EffectiveFrom = FareHarness.DefaultNow.AddHours(-1) });
        }

        var after = await harness.GetAsync<FareEstimateResponse>(
            $"/v1/fare/estimate?{Trip}&vehicleType=sedan", bearer);

        Assert.Equal(15_000, before.Breakdown.FirstKmMinor);
        Assert.Equal(30_000, after.Breakdown.FirstKmMinor);
        Assert.True(after.AmountMinor > before.AmountMinor);

        // The old row is still there and still resolvable — that is what makes a past ride
        // reconcilable against the rate that priced it.
        await using var reader = await harness.OpenAsync();
        var versions = await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            reader, "SELECT count(*)::int FROM fares.tariffs WHERE vehicle_type = 'sedan';");

        Assert.Equal(2, versions);
    }

    /// <summary>Flips one character of the signed payload, keeping the shape valid.</summary>
    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var payload = parts[1].ToCharArray();

        payload[5] = payload[5] == 'A' ? 'B' : 'A';

        return string.Join('.', parts[0], new string(payload), parts[2]);
    }
}
