using System.Net;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <c>POST /v1/internal/rides/{rideId}/offer/expire</c> — the third command dispatch-svc drives
/// (Δ C023).
/// </summary>
/// <remarks>
/// ADD §11.11's durable-backstop paragraph has the R-04 job "transition the ride back to
/// <c>Matching</c>"; §11.12 makes ride-svc the sole writer of <c>rides.state</c>, so the job asks
/// rather than writes. The rules that matter are all about <em>refusing</em>: the deadline decides,
/// not the caller, and a backstop that raced the driver's answer has to lose.
/// </remarks>
[Collection<RideCollection>]
public sealed class InternalOfferExpiryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_expired_offer_returns_the_ride_to_Matching_with_nothing_left_behind()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 1);

        await ElapseOfferWindowAsync(harness, rideId);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = offer.OfferId.ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RideHarness.ReadJsonAsync(response);
        Assert.Equal("Matching", body.GetProperty("state").GetString());
        Assert.Equal(offer.Version + 1, body.GetProperty("version").GetInt64());

        await using var connection = await harness.OpenAsync();
        var row = await connection.QuerySingleAsync<ExpiredRide>(
            """
            SELECT state AS State, current_offer_id AS CurrentOfferId,
                   offered_driver_id AS OfferedDriverId, offered_vehicle_id AS OfferedVehicleId,
                   offer_expires_at AS OfferExpiresAt
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });

        // Cleared exactly as on a decline. Leaving current_offer_id set would make ADD §11.11's
        // second accept origin reachable and the accept's from_state='Offered' audit row lie.
        Assert.Equal("Matching", row.State);
        Assert.Null(row.CurrentOfferId);
        Assert.Null(row.OfferedDriverId);
        Assert.Null(row.OfferedVehicleId);
        Assert.Null(row.OfferExpiresAt);

        // ADD Appendix B.2 invariant 4: the move is audited, with a reason.
        var reason = await connection.ExecuteScalarAsync<string>(
            """
            SELECT reason_code FROM rides.transitions
             WHERE ride_id = @RideId AND from_state = 'Offered' AND to_state = 'Matching'
             ORDER BY ts DESC LIMIT 1;
            """,
            new { RideId = rideId });

        Assert.Equal("OFFER_EXPIRED", reason);

        // D5' §6's Offered row names offer.expired; it is dispatch-svc's cue to re-offer.
        var eventType = await connection.ExecuteScalarAsync<string>(
            "SELECT event_type FROM rides.outbox WHERE aggregate_id = @RideId ORDER BY id DESC LIMIT 1;",
            new { RideId = rideId });

        Assert.Equal("offer.expired", eventType);
    }

    /// <summary>
    /// The predicate that makes the whole backstop safe: it is Postgres that compares
    /// <c>offer_expires_at</c> to <c>now()</c>, so a sweeping node whose clock ran ahead cannot
    /// cancel an offer the driver is still inside the window to accept.
    /// </summary>
    [Fact]
    public async Task An_offer_that_has_not_expired_yet_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = offer.OfferId.ToString() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "conflict");

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            "Offered",
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
    }

    [Fact]
    public async Task A_backstop_that_lost_to_the_drivers_accept_is_answered_410()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using (var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await ElapseOfferWindowAsync(harness, rideId);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = offer.OfferId.ToString() });

        // Gone, not Conflict: the offer is simply no longer the ride's live one. dispatch-svc
        // treats that as "the ride moved on without me" and settles its own row without guessing
        // at the driver's state.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.Gone, "offer-expired");

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            "Accepted",
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
    }

    [Fact]
    public async Task Expiring_a_different_offer_than_the_ride_holds_does_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        await harness.OfferAsync(rideId, driver, ttlSeconds: 1);

        await ElapseOfferWindowAsync(harness, rideId);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = Guid.NewGuid().ToString() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Gone, "offer-expired");
    }

    [Fact]
    public async Task Expiring_twice_is_answered_410_the_second_time()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 1);

        await ElapseOfferWindowAsync(harness, rideId);

        // The durable sweep and the Redis keyspace hint both fire on purpose (R-04 + D-07), so the
        // loser of that race must be an ordinary answer rather than a fault.
        using (var first = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = offer.OfferId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer/expire", new { offerId = offer.OfferId.ToString() });

        await ProblemDocument.AssertAsync(second, HttpStatusCode.Gone, "offer-expired");

        await using var connection = await harness.OpenAsync();
        var transitions = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM rides.transitions
             WHERE ride_id = @RideId AND reason_code = 'OFFER_EXPIRED';
            """,
            new { RideId = rideId });

        Assert.Equal(1, transitions);
    }

    [Fact]
    public async Task An_unknown_ride_is_a_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{Guid.NewGuid()}/offer/expire", new { offerId = Guid.NewGuid().ToString() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task A_missing_offerId_is_a_400()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{Guid.NewGuid()}/offer/expire", new { });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task The_route_is_unreachable_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{Guid.NewGuid()}/offer/expire",
            new { offerId = Guid.NewGuid().ToString() },
            apiKey: null);

        // 404, matching what the gateway returns for the same prefix: a caller who is not entitled
        // to the internal plane should not be able to map it.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>
    /// Winds the ride's deadline into the past so the backstop's
    /// <c>offer_expires_at &lt;= now()</c> predicate is satisfied without the test sleeping through
    /// a real window. The predicate itself is what the previous test asserts.
    /// </summary>
    private static async Task ElapseOfferWindowAsync(RideHarness harness, Guid rideId)
    {
        await using var connection = await harness.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE rides.rides SET offer_expires_at = now() - interval '1 second' WHERE id = @RideId;",
            new { RideId = rideId });
    }

    private sealed record ExpiredRide(
        string State,
        Guid? CurrentOfferId,
        Guid? OfferedDriverId,
        Guid? OfferedVehicleId,
        DateTimeOffset? OfferExpiresAt);
}
