using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 3: "replaying POST /rides/request with the same clientRequestId returns the existing
/// ride (R-18)" — plus the header half of the same guarantee (R-14, ADD §11.13).
/// </summary>
/// <remarks>
/// The two keys are independent and D5' §6.2 requires both. The header replays the stored response
/// byte for byte out of <c>rides.command_log</c>; <c>(passengerId, clientRequestId)</c> is enforced
/// by <c>ux_rides_idem</c> and survives a client that regenerated its header key — which is exactly
/// what a reinstalled app does.
/// </remarks>
[Collection<RideCollection>]
public sealed class IdempotencyTests(PostgresFixture postgres)
{
    /// <summary>R-18: a different header key, the same clientRequestId, one ride.</summary>
    [Fact]
    public async Task A_retry_under_a_new_header_key_returns_the_ride_the_first_call_booked()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var clientRequestId = Guid.NewGuid().ToString();

        var first = await harness.RequestRideAsync(passenger, clientRequestId);
        var second = await harness.RequestRideAsync(passenger, clientRequestId);

        Assert.Equal(first.GetProperty("rideId").GetGuid(), second.GetProperty("rideId").GetGuid());
        Assert.Equal(first.GetProperty("version").GetInt64(), second.GetProperty("version").GetInt64());
        Assert.Equal(
            first.GetProperty("estimatedFare").GetProperty("amountMinor").GetInt64(),
            second.GetProperty("estimatedFare").GetProperty("amountMinor").GetInt64());

        await using var connection = await harness.OpenAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.rides WHERE passenger_id = @PassengerId;", new { PassengerId = passengerId }));

        // One booking, one event. A second ride.requested would have dispatch build a candidate
        // set for a ride that does not exist.
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'ride.requested';",
            new { RideId = first.GetProperty("rideId").GetGuid() }));

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.transitions WHERE ride_id = @RideId;",
            new { RideId = first.GetProperty("rideId").GetGuid() }));
    }

    /// <summary>
    /// A ULID clientRequestId is what the mobile apps actually send (ADD §11.13), and it has to
    /// key the same way a UUID does.
    /// </summary>
    [Fact]
    public async Task A_ulid_client_request_id_keys_the_same_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        const string ClientRequestId = "01JQ9F8Z6N5R7T2V4X6Y8A0B2C";

        var first = await harness.RequestRideAsync(passenger, ClientRequestId);
        var second = await harness.RequestRideAsync(passenger, ClientRequestId.ToLowerInvariant());

        Assert.Equal(first.GetProperty("rideId").GetGuid(), second.GetProperty("rideId").GetGuid());
    }

    /// <summary>R-14: the same header key replays the original response verbatim.</summary>
    [Fact]
    public async Task The_same_header_key_replays_the_stored_response_byte_for_byte()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var key = Guid.NewGuid().ToString();
        var body = BookingBody(harness);

        var first = await harness.PostAsync("/v1/rides/request", body, passenger, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync();

        var replay = await harness.PostAsync("/v1/rides/request", body, passenger, key);

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").Single());
        Assert.Equal(firstText, await replay.Content.ReadAsStringAsync());
    }

    /// <summary>The same key against a different payload is a client bug, not a retry.</summary>
    [Fact]
    public async Task Reusing_a_key_for_a_different_booking_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var key = Guid.NewGuid().ToString();

        var first = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var reused = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger, key);

        await ProblemDocument.AssertAsync(reused, HttpStatusCode.Conflict, "idempotency-key-reuse");
    }

    [Fact]
    public async Task A_mutation_without_an_idempotency_key_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rides/request")
        {
            Content = JsonContent.Create(BookingBody(harness)),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", passenger);

        using var response = await harness.Client.SendAsync(request);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "idempotency-key-required");
    }

    /// <summary>
    /// Invariant 1 (ADD Appendix B.2): a rider has at most one non-terminal ride. A *different*
    /// clientRequestId is a second booking, not a retry, and <c>ux_rides_open_passenger</c> is
    /// what refuses it.
    /// </summary>
    [Fact]
    public async Task A_second_booking_while_one_is_live_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        await harness.RequestRideAsync(passenger);

        var second = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger);

        await ProblemDocument.AssertAsync(second, HttpStatusCode.Conflict, "active-ride-exists");
    }

    private static object BookingBody(RideHarness harness) => new
    {
        clientRequestId = Guid.NewGuid().ToString(),
        pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
        dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
        vehicleType = "three_wheeler",
        fareEstimateToken = harness.IssueFareToken(),
        paymentMethod = "cash",
    };
}
