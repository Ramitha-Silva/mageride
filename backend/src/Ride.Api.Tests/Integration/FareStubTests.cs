using System.Net;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// The two services meet on one value — the <c>fareEstimateToken</c> — so that crossing is
/// asserted with both of them actually running.
/// </summary>
/// <remarks>
/// Everything else about the fare stub is deliberately thin (C049/C050 replace it), but "the
/// price fare-svc quoted is the price the ride records" is a property the walking skeleton would
/// be worthless without, and nothing else in either suite covers it.
/// </remarks>
[Collection<RideCollection>]
public sealed class FareStubTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_price_fare_svc_quotes_is_the_price_the_ride_carries_to_payment()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var rides = await RideHarness.StartAsync(postgres);
        await using var fares = await FareHarness.StartAsync(rides.Tokens, postgres);

        var passengerId = await rides.CreatePassengerAsync();
        var passenger = rides.Tokens.Passenger(passengerId);
        var driver = await rides.CreateDriverAsync();

        using var quoted = await fares.EstimateAsync(passenger);
        Assert.Equal(HttpStatusCode.OK, quoted.StatusCode);

        var estimate = await FareHarness.ReadJsonAsync(quoted);
        var amountMinor = estimate.GetProperty("amountMinor").GetInt64();
        var token = estimate.GetProperty("fareEstimateToken").GetString();

        // D5' §1.1: first-km charge plus per-km over the remainder, now read from the seeded
        // `fares.tariffs` rate card rather than a hard-coded table (Δ C049).
        Assert.Equal(10_000, estimate.GetProperty("breakdown").GetProperty("firstKmMinor").GetInt64());
        Assert.Equal(8_000, estimate.GetProperty("breakdown").GetProperty("perKmMinor").GetInt64());

        // Colombo Fort → Dehiwala is about 9.5 km straight-line. **Δ C049: the quote is now priced
        // on the road distance**, approximated as that line × `Fare:RouteDetourFactor` (1.3) until
        // OSRM lands in Phase 3 — so ~12.4 km. The C022 range asserted the straight line, which was
        // the stub under-quoting every ride by the detour.
        Assert.InRange(estimate.GetProperty("breakdown").GetProperty("distanceKm").GetDouble(), 11.0, 14.0);
        Assert.Equal("LKR", estimate.GetProperty("currency").GetString());

        // The band is wide because this harness runs fare-svc on the **real** clock, so whether the
        // seeded peak (07:00–09:00, 17:00–19:00) or night (22:00–05:00) window applies depends on
        // when the suite runs. Asserting a particular surcharge here would be a test that fails
        // twice a day; the windows themselves are pinned against a fake clock in Fare.Api.Tests.
        // ~11–14 km on the three-wheeler tariff is Rs 900–1 140 base, up to +35% surcharged.
        Assert.InRange(amountMinor, 85_000, 160_000);

        // …and ride-svc accepts it, on the strength of the shared key alone.
        var booked = await rides.RequestRideAsync(passenger, fareEstimateToken: token);
        var rideId = booked.GetProperty("rideId").GetGuid();

        Assert.Equal(amountMinor, booked.GetProperty("estimatedFare").GetProperty("amountMinor").GetInt64());

        // The quote survives the whole ride and is what `complete` hands to settlement.
        var offer = await rides.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var accepted = await rides.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        using var started = await rides.PostAsync($"/v1/rides/{rideId}/start", new { version }, driver.Bearer);
        version = (await RideHarness.ReadJsonAsync(started)).GetProperty("version").GetInt64();

        using var completed = await rides.PostAsync($"/v1/rides/{rideId}/complete", new { version }, driver.Bearer);
        var fare = (await RideHarness.ReadJsonAsync(completed)).GetProperty("fare");

        Assert.Equal(amountMinor, fare.GetProperty("amountMinor").GetInt64());
        Assert.Equal("LKR", fare.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Each_tier_is_priced_from_its_own_row()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var rides = await RideHarness.StartAsync(postgres);
        await using var fares = await FareHarness.StartAsync(rides.Tokens, postgres);

        var passenger = rides.Tokens.Passenger(await rides.CreatePassengerAsync());

        using var motorbike = await fares.EstimateAsync(passenger, vehicleType: "motorbike");
        using var van = await fares.EstimateAsync(passenger, vehicleType: "van");

        var cheap = (await FareHarness.ReadJsonAsync(motorbike)).GetProperty("amountMinor").GetInt64();
        var dear = (await FareHarness.ReadJsonAsync(van)).GetProperty("amountMinor").GetInt64();

        Assert.True(dear > cheap, $"A van ({dear}) must not be cheaper than a motorbike ({cheap}).");
    }

    /// <summary>The one place the stub says no: a coordinate MageRide does not operate at.</summary>
    [Fact]
    public async Task A_trip_outside_sri_lanka_is_unserviceable()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var rides = await RideHarness.StartAsync(postgres);
        await using var fares = await FareHarness.StartAsync(rides.Tokens, postgres);

        var passenger = rides.Tokens.Passenger(await rides.CreatePassengerAsync());

        // Chennai.
        using var response = await fares.EstimateAsync(passenger, fromLat: 13.0827, fromLng: 80.2707);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "unserviceable-area");
    }

    [Fact]
    public async Task An_unauthenticated_estimate_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var rides = await RideHarness.StartAsync(postgres);
        await using var fares = await FareHarness.StartAsync(rides.Tokens, postgres);

        using var response = await fares.Client.GetAsync(
            "/v1/fare/estimate?fromLat=6.9&fromLng=79.8&toLat=6.8&toLng=79.9&vehicleType=sedan");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("car")]
    public async Task A_vehicle_type_with_no_tariff_is_refused(string vehicleType)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var rides = await RideHarness.StartAsync(postgres);
        await using var fares = await FareHarness.StartAsync(rides.Tokens, postgres);

        var passenger = rides.Tokens.Passenger(await rides.CreatePassengerAsync());

        using var response = await fares.EstimateAsync(passenger, vehicleType: vehicleType);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }
}
