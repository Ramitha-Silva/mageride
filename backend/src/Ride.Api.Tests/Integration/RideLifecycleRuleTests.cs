using System.Net;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// The rules around the happy path: what a booking must carry, and what the aggregate refuses.
/// </summary>
[Collection<RideCollection>]
public sealed class RideLifecycleRuleTests(PostgresFixture postgres)
{
    // -------------------------------------------------------------------------------------------
    // Booking
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The quote is what stops a client naming its own price, so an absent, forged or
    /// wrong-tier token is all the same answer: <c>400 invalid-fare-token</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mrf1.eyJ2dCI6InNlZGFuIn0.not-a-signature")]
    public async Task A_booking_without_a_usable_quote_is_refused(string? token)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await Book(harness, passenger, fareEstimateToken: token);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-fare-token");
    }

    [Fact]
    public async Task A_quote_for_another_tier_cannot_book_this_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await Book(
            harness, passenger, vehicleType: "van", fareEstimateToken: harness.IssueFareToken("motorbike"));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-fare-token");
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("train")]
    [InlineData("car")]
    public async Task A_vehicle_type_outside_the_mode_c_set_is_refused(string vehicleType)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await Book(harness, passenger, vehicleType: vehicleType);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>D3': "cod=package only" — and package itself is C037.</summary>
    [Fact]
    public async Task Cash_on_delivery_is_not_a_passenger_payment_method()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await Book(harness, passenger, paymentMethod: "cod");

        await ProblemDocument.AssertAsync(response, HttpStatusCode.PaymentRequired, "payment-method-invalid");
    }

    /// <summary>The fences: proxy is C032, package is C037, scheduling is C035.</summary>
    [Theory]
    [InlineData("kind", "proxy")]
    [InlineData("kind", "package")]
    [InlineData("scheduledAt", "2026-08-01T09:00:00Z")]
    public async Task Out_of_scope_bookings_are_refused_rather_than_downgraded(string field, string value)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clientRequestId"] = Guid.NewGuid().ToString(),
            ["pickup"] = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
            ["dropoff"] = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
            ["vehicleType"] = "three_wheeler",
            ["fareEstimateToken"] = harness.IssueFareToken(),
            ["paymentMethod"] = "cash",
            [field] = value,
        };

        using var response = await harness.PostAsync("/v1/rides/request", body, passenger);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task A_booking_without_coordinates_names_the_missing_fields()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "three_wheeler",
                fareEstimateToken = harness.IssueFareToken(),
                paymentMethod = "cash",
            },
            passenger);

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var errors = problem.Root.GetProperty("errors");

        Assert.True(errors.TryGetProperty("pickup.lat", out _));
        Assert.True(errors.TryGetProperty("pickup.lng", out _));
    }

    // -------------------------------------------------------------------------------------------
    // The aggregate
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_ride_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            $"/v1/rides/{Guid.NewGuid()}/offer/{driver.DriverId}/accept",
            new { offerId = Guid.NewGuid().ToString(), version = 1 },
            driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>An expired offer is <c>410 Gone</c>, not a 409 — the driver app shows the next one.</summary>
    [Fact]
    public async Task An_offer_accepted_past_its_ttl_is_gone()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 1);

        // The deadline is Postgres's `offer_expires_at > now()`, so waiting it out is the only
        // honest way to cross it.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        using var response = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Gone, "offer-expired");
    }

    /// <summary>§11.12: declining costs nothing and puts the ride back in the pool.</summary>
    [Fact]
    public async Task Declining_returns_the_ride_to_matching_and_releases_the_driver()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var first = await harness.CreateDriverAsync();
        var second = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, first, ttlSeconds: 30);

        using var declined = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{first.DriverId}/decline",
            new { offerId = offer.OfferId.ToString() },
            first.Bearer);

        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        Assert.Equal("Matching", (await RideHarness.ReadJsonAsync(declined)).GetProperty("state").GetString());

        await using var connection = await harness.OpenAsync();

        // The offer columns are cleared together (ck_rides_offer_pair) so the next candidate can
        // be reserved cleanly.
        var cleared = await connection.QuerySingleAsync<(Guid? OfferId, Guid? DriverId, DateTimeOffset? ExpiresAt)>(
            "SELECT current_offer_id, offered_driver_id, offer_expires_at FROM rides.rides WHERE id = @RideId;",
            new { RideId = rideId });

        Assert.Null(cleared.OfferId);
        Assert.Null(cleared.DriverId);
        Assert.Null(cleared.ExpiresAt);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'offer.declined';",
            new { RideId = rideId }));

        // The declining driver is no longer a participant, but the next one can be offered it.
        using var afterDecline = await harness.GetAsync($"/v1/rides/{rideId}", first.Bearer);
        await ProblemDocument.AssertAsync(afterDecline, HttpStatusCode.Forbidden, "not-ride-participant");

        var reoffer = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer",
            new
            {
                offerId = Guid.NewGuid().ToString(),
                driverId = second.DriverId.ToString(),
                vehicleId = second.VehicleId.ToString(),
                ttlSeconds = 30,
            });

        Assert.Equal(HttpStatusCode.OK, reoffer.StatusCode);
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_not_an_overwrite()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The client echoes the version it saw *before* the accept.
        using var stale = await harness.PostAsync(
            $"/v1/rides/{rideId}/arrive", new { version = offer.Version }, driver.Bearer);

        await ProblemDocument.AssertAsync(stale, HttpStatusCode.Conflict, "version-conflict");
    }

    [Fact]
    public async Task A_move_the_machine_does_not_draw_is_an_illegal_transition()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        // Accepted → Completed skips the trip.
        using var response = await harness.PostAsync(
            $"/v1/rides/{rideId}/complete", new { version }, driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");
    }

    /// <summary>
    /// The contract's `start` allows <c>Accepted → InProgress</c> without a geofence arrival
    /// (C022 handoff, gap (e)). A driver who reached the rider must still be able to start.
    /// </summary>
    [Fact]
    public async Task A_ride_can_start_without_a_recorded_arrival()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        using var started = await harness.PostAsync(
            $"/v1/rides/{rideId}/start", new { version }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("InProgress", (await RideHarness.ReadJsonAsync(started)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Only_the_accepted_driver_can_move_the_ride_on()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();
        var other = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        using var response = await harness.PostAsync(
            $"/v1/rides/{rideId}/arrive", new { version }, other.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-ride-participant");

        // …and the failed attempt changed nothing.
        await using var connection = await harness.OpenAsync();
        Assert.Equal("Accepted", await connection.ExecuteScalarAsync<string>(
            "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
    }

    [Fact]
    public async Task A_mutation_without_a_version_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();

        using var response = await harness.PostAsync($"/v1/rides/{rideId}/arrive", new { }, driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    private static Task<HttpResponseMessage> Book(
        RideHarness harness,
        string passengerBearer,
        string vehicleType = "three_wheeler",
        string? fareEstimateToken = "default",
        string paymentMethod = "cash") =>
        harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType,
                fareEstimateToken = fareEstimateToken == "default" ? harness.IssueFareToken(vehicleType) : fareEstimateToken,
                paymentMethod,
            },
            passengerBearer);
}
