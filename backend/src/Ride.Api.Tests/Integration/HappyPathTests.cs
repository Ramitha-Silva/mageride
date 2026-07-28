using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 1: "one booked ride walks the full happy path and lands in PaymentPending".
/// </summary>
/// <remarks>
/// Requested → Matching → Offered → Accepted → DriverArrived → InProgress → Completed →
/// PaymentPending, driven entirely through the HTTP surface, against a real Postgres. The audit
/// trail and the outbox are asserted alongside the states because ADD Appendix B.2 invariant 4
/// makes them part of the transition, not a side effect of it.
/// </remarks>
[Collection<RideCollection>]
public sealed class HappyPathTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_booked_ride_walks_the_whole_machine_and_lands_in_payment_pending()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        // ---- Requested -------------------------------------------------------------------
        var booked = await harness.RequestRideAsync(passenger);
        var rideId = booked.GetProperty("rideId").GetGuid();

        Assert.Equal("Requested", booked.GetProperty("state").GetString());
        Assert.Equal(1, booked.GetProperty("version").GetInt64());
        Assert.Equal(74_000, booked.GetProperty("estimatedFare").GetProperty("amountMinor").GetInt64());
        Assert.Equal("LKR", booked.GetProperty("estimatedFare").GetProperty("currency").GetString());

        // ---- Matching → Offered ----------------------------------------------------------
        var offer = await harness.OfferAsync(rideId, driver);

        var offered = await ReadStateAsync(harness, rideId, passenger);
        Assert.Equal("Offered", offered.GetProperty("state").GetString());
        Assert.Equal(3, offered.GetProperty("version").GetInt64());
        Assert.True(offered.TryGetProperty("offerExpiresAt", out var expiresAt));
        Assert.True(expiresAt.GetDateTimeOffset() > DateTimeOffset.UtcNow);

        // ---- Accepted --------------------------------------------------------------------
        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptBody = await RideHarness.ReadJsonAsync(accepted);
        Assert.Equal("Accepted", acceptBody.GetProperty("state").GetString());

        // The 200 carries the whole aggregate, which is what the driver app renders next.
        var detail = acceptBody.GetProperty("ride");
        Assert.Equal("passenger", detail.GetProperty("kind").GetString());
        Assert.Equal("cash", detail.GetProperty("paymentMethod").GetString());
        Assert.Equal(driver.DriverId, detail.GetProperty("driver").GetProperty("driverId").GetGuid());
        Assert.Equal(driver.Name, detail.GetProperty("driver").GetProperty("name").GetString());
        Assert.Equal(driver.Plate, detail.GetProperty("driver").GetProperty("registrationNumber").GetString());

        var version = acceptBody.GetProperty("version").GetInt64();

        // ---- DriverArrived → InProgress → Completed → PaymentPending ---------------------
        version = await AdvanceAsync(harness, rideId, "arrive", driver.Bearer, version, "DriverArrived");
        version = await AdvanceAsync(harness, rideId, "start", driver.Bearer, version, "InProgress");

        var completed = await harness.PostAsync(
            $"/v1/rides/{rideId}/complete", new { version }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var completeBody = await RideHarness.ReadJsonAsync(completed);

        Assert.Equal("PaymentPending", completeBody.GetProperty("state").GetString());
        Assert.Equal(74_000, completeBody.GetProperty("fare").GetProperty("amountMinor").GetInt64());

        // ---- The row, the audit and the outbox ------------------------------------------
        await using var connection = await harness.OpenAsync();

        Assert.Equal("PaymentPending", await connection.ExecuteScalarAsync<string>(
            "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
        Assert.Equal(driver.DriverId, await connection.ExecuteScalarAsync<Guid>(
            "SELECT accepted_driver_id FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
        // ADD §11.11 sets accepted_vehicle_id from the offer, so the ride records the vehicle the
        // offer was actually made for.
        Assert.Equal(driver.VehicleId, await connection.ExecuteScalarAsync<Guid>(
            "SELECT accepted_vehicle_id FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));

        // Invariant 4: every move is audited, including the automatic Completed → PaymentPending.
        var trail = (await connection.QueryAsync<(string? FromState, string ToState, string ActorType)>(
            "SELECT from_state, to_state, actor_type FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts, id;",
            new { RideId = rideId })).ToArray();

        Assert.Equal(
            [
                (null, "Requested", "rider"),
                ("Requested", "Matching", "system"),
                ("Matching", "Offered", "system"),
                ("Offered", "Accepted", "driver"),
                ("Accepted", "DriverArrived", "driver"),
                ("DriverArrived", "InProgress", "driver"),
                ("InProgress", "Completed", "driver"),
                ("Completed", "PaymentPending", "system"),
            ],
            trail);

        // Six events, in order. Requested → Matching emits nothing: dispatch-svc drove that move.
        var events = (await connection.QueryAsync<string>(
            "SELECT event_type FROM rides.outbox WHERE aggregate_id = @RideId ORDER BY id;",
            new { RideId = rideId })).ToArray();

        Assert.Equal(
            ["ride.requested", "offer.created", "ride.accepted", "ride.driver_arrived", "ride.started", "ride.completed"],
            events);
    }

    /// <summary>
    /// The passenger's cold-start recovery read (R-18) finds the live ride, and the driver's
    /// mirror finds the same one.
    /// </summary>
    [Fact]
    public async Task Both_sides_can_recover_the_live_ride_after_a_cold_start()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        // Nothing running yet: an explicit null body, not a 404.
        var empty = await harness.GetAsync($"/v1/rides/passenger/{passengerId}/active", passenger);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await RideHarness.ReadJsonAsync(empty)).ValueKind);

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver);

        // The offered driver can already see the ride — that is the offer card, before any accept.
        var offeredToDriver = await harness.GetAsync($"/v1/rides/driver/{driver.DriverId}/active", driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, offeredToDriver.StatusCode);
        Assert.Equal(rideId, (await RideHarness.ReadJsonAsync(offeredToDriver)).GetProperty("rideId").GetGuid());

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var live = await harness.GetAsync($"/v1/rides/passenger/{passengerId}/active", passenger);
        var body = await RideHarness.ReadJsonAsync(live);

        Assert.Equal(rideId, body.GetProperty("rideId").GetGuid());
        Assert.Equal("Accepted", body.GetProperty("state").GetString());
        Assert.Equal(driver.Name, body.GetProperty("driver").GetProperty("name").GetString());
    }

    private static async Task<JsonElement> ReadStateAsync(RideHarness harness, Guid rideId, string bearer)
    {
        var response = await harness.GetAsync($"/v1/rides/{rideId}/state", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RideHarness.ReadJsonAsync(response);
    }

    private static async Task<long> AdvanceAsync(
        RideHarness harness, Guid rideId, string command, string bearer, long version, string expectedState)
    {
        var response = await harness.PostAsync($"/v1/rides/{rideId}/{command}", new { version }, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RideHarness.ReadJsonAsync(response);
        Assert.Equal(expectedState, body.GetProperty("state").GetString());

        return body.GetProperty("version").GetInt64();
    }
}
