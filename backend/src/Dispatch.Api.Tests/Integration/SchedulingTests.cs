using Dapper;
using System.Net;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// Advance bookings — <c>POST /v1/rides/schedule</c> and its cancellation (US-6A.4, AL-36).
/// </summary>
/// <remarks>
/// The Job Board and the T-30 dispatch have a suite of their own
/// (<see cref="JobBoardTests"/>); this one is about the booking itself.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class SchedulingTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// <b>Definition of Done.</b> AL-36 item 2: "select the location to go" is mandatory, and a
    /// booking without one is refused at the service boundary rather than stored half-formed.
    /// </summary>
    [Fact]
    public async Task A_scheduled_ride_without_a_destination_is_rejected()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();

        using var response = await harness.ScheduleRideAsync(
            passengerId, DateTimeOffset.UtcNow.AddHours(2), includeDestination: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(response);

        // The 400 names the two members that were missing, so the client can highlight the field
        // rather than showing "validation failed".
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("destLat", out _));
        Assert.True(errors.TryGetProperty("destLng", out _));

        // And nothing was written: a booking that cannot be dispatched must not sit on the board.
        await using var connection = await harness.OpenAsync();

        var stored = await connection.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM dispatch.scheduled_rides WHERE passenger_id = @Id;",
            new { Id = passengerId });

        Assert.Equal(0, stored);
    }

    [Fact]
    public async Task A_scheduled_ride_is_stored_with_its_destination_and_starts_unmaterialised()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var pickupTime = DateTimeOffset.UtcNow.AddHours(3);

        using var response = await harness.ScheduleRideAsync(passengerId, pickupTime);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await DispatchHarness.ReadJsonAsync(response);

        Assert.Equal(ScheduledRideStatuses.Scheduled, body.GetProperty("status").GetString());
        Assert.Equal("three_wheeler", body.GetProperty("vehicleType").GetString());

        // `cash` unless the passenger said otherwise (Δ C035): the materialised ride's
        // payment_method is NOT NULL and the printed body carried no way to choose.
        Assert.Equal("cash", body.GetProperty("paymentMethod").GetString());

        // `rideId` is absent, not null — the ride does not exist until T-30 min, and that is the
        // one member telling a client which of the two this is.
        Assert.False(body.TryGetProperty("rideId", out _));

        Assert.Equal(
            DispatchHarness.Dropoff.Latitude,
            body.GetProperty("dropoff").GetProperty("lat").GetDouble(),
            precision: 6);
    }

    /// <summary>
    /// A pickup inside the T-30 window would be materialised by the very next sweep, which is an
    /// immediate ride booked through the wrong endpoint.
    /// </summary>
    [Fact]
    public async Task A_pickup_time_inside_the_lead_window_is_refused_with_a_pointer_to_the_right_endpoint()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            // The harness lifts this floor so other tests can book and sweep in one breath; this is
            // the test that puts it back and asserts it.
            ["Dispatch:ScheduledMinimumLead"] = "00:30:00",
        });

        using var response = await harness.ScheduleRideAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(response);
        var message = problem.GetProperty("errors").GetProperty("pickupTime")[0].GetString();

        Assert.Contains("/v1/rides/request", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passenger_may_withdraw_a_booking_that_has_not_been_dispatched()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var scheduledRideId = await harness.ScheduleRideForAsync(passengerId, DateTimeOffset.UtcNow.AddHours(4));

        using var response = await Delete(harness, scheduledRideId, harness.Tokens.Passenger(passengerId));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var stored = await harness.ReadScheduledRideAsync(scheduledRideId);
        Assert.Equal(ScheduledRideStatuses.Cancelled, stored.Status);

        // Cancelling twice is not a fault: the row is already where the caller wants it.
        using var again = await Delete(harness, scheduledRideId, harness.Tokens.Passenger(passengerId));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task Another_passengers_booking_cannot_be_withdrawn()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(4));

        var stranger = await harness.CreatePassengerAsync();

        using var response = await Delete(harness, scheduledRideId, harness.Tokens.Passenger(stranger));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await harness.ReadScheduledRideAsync(scheduledRideId);
        Assert.Equal(ScheduledRideStatuses.Scheduled, stored.Status);
    }

    /// <summary>
    /// Once dispatch has materialised the ride, the cancellation belongs to ride-svc — it is that
    /// endpoint which owns the §11.12 penalty matrix, and this one deliberately carries no penalty.
    /// </summary>
    [Fact]
    public async Task A_dispatched_booking_cannot_be_withdrawn_here()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var scheduledRideId = await harness.ScheduleRideForAsync(passengerId, DateTimeOffset.UtcNow.AddMinutes(20));

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var stored = await harness.ReadScheduledRideAsync(scheduledRideId);
        Assert.Equal(ScheduledRideStatuses.Dispatched, stored.Status);
        Assert.NotNull(stored.RideId);

        using var response = await Delete(harness, scheduledRideId, harness.Tokens.Passenger(passengerId));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// The T-30 sweep creates the ride through ride-svc, which stays the sole writer of
    /// <c>rides.state</c> — and a second sweep finds the same ride rather than booking another.
    /// </summary>
    [Fact]
    public async Task The_T30_sweep_materialises_a_booking_once_however_often_it_runs()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var scheduledRideId = await harness.ScheduleRideForAsync(passengerId, DateTimeOffset.UtcNow.AddMinutes(25));

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var first = await harness.ReadScheduledRideAsync(scheduledRideId);
        Assert.Equal(ScheduledRideStatuses.Dispatched, first.Status);

        var ride = await harness.ReadRideAsync(first.RideId!.Value);
        Assert.Equal("Requested", ride.State);

        // A DISPATCHED row is no longer due, so the second sweep claims nothing at all.
        Assert.Equal(0, await harness.MaterialiseDueScheduledRidesAsync());

        await using var connection = await harness.OpenAsync();

        var rides = await connection.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM rides.rides WHERE passenger_id = @Id;",
            new { Id = passengerId });

        Assert.Equal(1, rides);
    }

    /// <summary>
    /// A booking whose pickup is still hours away is not swept — the whole point of T-30 is that it
    /// is not T-0.
    /// </summary>
    [Fact]
    public async Task A_booking_outside_the_lead_window_is_left_alone()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(6));

        Assert.Equal(0, await harness.MaterialiseDueScheduledRidesAsync());

        var stored = await harness.ReadScheduledRideAsync(scheduledRideId);
        Assert.Equal(ScheduledRideStatuses.Scheduled, stored.Status);
        Assert.Null(stored.RideId);
    }

    /// <summary>
    /// AL-16 on the booking side: a passenger reputation-svc has disabled cannot put a ride on the
    /// board either. The gate is a real gRPC call, like every other reputation read in this suite.
    /// </summary>
    [Fact]
    public async Task A_booking_disabled_passenger_cannot_schedule_a_ride()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        await harness.SetBlockStateAsync(passengerId, "BOOKING_DISABLED", reason: "cancellations_disabled");

        using var response = await harness.ScheduleRideAsync(passengerId, DateTimeOffset.UtcNow.AddHours(2));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(response);
        Assert.EndsWith("booking-disabled", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A driver has no business booking a passenger's ride — deny-by-default, from AL-06.</summary>
    [Fact]
    public async Task A_driver_cannot_schedule_a_ride()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/rides/schedule",
            new
            {
                pickupLat = DispatchHarness.Pickup.Latitude,
                pickupLng = DispatchHarness.Pickup.Longitude,
                destLat = DispatchHarness.Dropoff.Latitude,
                destLng = DispatchHarness.Dropoff.Longitude,
                pickupTime = DateTimeOffset.UtcNow.AddHours(2),
                vehicleType = "three_wheeler",
            },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Delete(DispatchHarness harness, Guid scheduledRideId, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/rides/schedule/{scheduledRideId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        return harness.Client.SendAsync(request);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
