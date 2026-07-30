using MageRide.Fanout.Rides;
using MageRide.Fanout.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.TestKit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Integration;

/// <summary>
/// <c>SubscribeRide</c>, <c>SubscribeLocRequest</c> and the three events that reach those groups
/// (US-6A.12, P-13, US-20.7).
/// </summary>
[Collection<FanoutCollection>]
public sealed class RideSubscriptionTests(RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task A_participant_may_subscribe_and_a_stranger_may_not()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var rideId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        await PublishRideAsync(harness, rideId, "ride.accepted", "Accepted", passengerId, driverId);

        await using var passenger = harness.Passenger(passengerId);
        await passenger.StartAsync();
        await passenger.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        // The driver is on the ride too, and the group is where their own state changes arrive.
        await using var driver = harness.Driver(driverId);
        await driver.StartAsync();
        await driver.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        await using var stranger = harness.Passenger(Guid.NewGuid());
        await stranger.StartAsync();

        // Without the check this would be a working subscription to somebody else's journey,
        // showing their driver's live position — and from the client it would look exactly like the
        // finished feature.
        await Assert.ThrowsAsync<HubException>(
            () => stranger.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString()));

        await passenger.StopAsync();
        await driver.StopAsync();
        await stranger.StopAsync();
    }

    [Fact]
    public async Task A_proxy_bookers_ride_admits_the_booker_and_the_rider()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var rideId = Guid.NewGuid();
        var bookerId = Guid.NewGuid();
        var riderId = Guid.NewGuid();

        // P-01: the account that booked and paid is the booker; the person in the car is the rider.
        // `signalr-hub.md` §2.1 names the booker and omits the rider, who needs the live driver
        // position rather more — raised in the C041 handoff.
        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "ride.accepted",
            Events.Ride(rideId, "ride.accepted", "Accepted", bookerId, Guid.NewGuid(), Guid.NewGuid(),
                bookerId: bookerId, riderId: riderId));

        await WaitForProjectionAsync(harness, rideId);

        await using var booker = harness.Passenger(bookerId);
        await booker.StartAsync();
        await booker.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        await using var rider = harness.Passenger(riderId);
        await rider.StartAsync();
        await rider.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        await booker.StopAsync();
        await rider.StopAsync();
    }

    [Fact]
    public async Task A_ride_this_service_has_never_seen_is_refused()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(events: false);

        await using var passenger = harness.Passenger(Guid.NewGuid());
        await passenger.StartAsync();

        // A gap in the projection means fanout-svc does not know who the parties are — which is not
        // the same as knowing the caller is one of them.
        var error = await Assert.ThrowsAsync<HubException>(
            () => passenger.InvokeAsync(Contract.Methods.SubscribeRide, Guid.NewGuid().ToString()));

        Assert.Contains("may subscribe to", error.Message, StringComparison.Ordinal);

        await passenger.StopAsync();
    }

    [Fact]
    public async Task Every_ride_transition_reaches_the_ride_group_with_its_version()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var rideId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        await PublishRideAsync(harness, rideId, "ride.accepted", "Accepted", passengerId, driverId);

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(passengerId);
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "ride.driver_arrived",
            Events.Ride(rideId, "ride.driver_arrived", "DriverArrived", passengerId, driverId, Guid.NewGuid(),
                version: 4));

        await FanoutHarness.WaitAsync(
            () => events.RideStates.Any(state => state.GetProperty("state").GetString() == "DriverArrived"),
            "the transition should reach the ride group");

        var arrived = events.RideStates.First(state => state.GetProperty("state").GetString() == "DriverArrived");

        Assert.Equal(rideId, arrived.GetProperty("rideId").GetGuid());

        // The same optimistic-concurrency counter the REST responses carry, so a client holding both
        // can tell which is newer — socket delivery is at-least-once and unordered across reconnects.
        Assert.Equal(4, arrived.GetProperty("version").GetInt64());
        Assert.Equal(driverId, arrived.GetProperty("driver").GetProperty("driverId").GetGuid());

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_package_handoff_reaches_the_ride_group()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var rideId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        await PublishRideAsync(harness, rideId, "ride.started", "InProgress", passengerId, Guid.NewGuid());

        var events = new LiveEvents();
        await using var sender = harness.Passenger(passengerId);
        events.Attach(sender);
        await sender.StartAsync();
        await sender.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "package.picked_up",
            Events.Package(rideId, "package.picked_up", "PickedUp", passengerId));

        await FanoutHarness.WaitAsync(
            () => events.Packages.Any(package => package.RideId == rideId && package.Status == "PickedUp"),
            "US-20.7's handoff progress should reach the sender");

        await sender.StopAsync();
    }

    /// <summary>P-13: the booker subscribes before the ride exists, and hears the rider confirm.</summary>
    [Fact]
    public async Task The_proxy_round_trip_resolves_on_the_bookers_own_group()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var bookerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var events = new LiveEvents();
        await using var booker = harness.Passenger(bookerId);
        events.Attach(booker);
        await booker.StartAsync();

        // No ride exists yet — that is the point of the round-trip, and why the group is named by
        // the booker and the request rather than by a ride.
        await booker.InvokeAsync(Contract.Methods.SubscribeLocRequest, requestId.ToString());

        await harness.PublishAsync(
            EventTopics.RideEvents,
            requestId,
            "location.request.confirmed",
            Events.LocationRequest(requestId, bookerId, "location.request.confirmed", 6.9271, 79.8612));

        await FanoutHarness.WaitAsync(
            () => events.LocationRequests.Count > 0, "the confirmation should reach the booker");

        var resolved = events.LocationRequests[0];

        Assert.Equal(requestId, resolved.GetProperty("requestId").GetGuid());
        Assert.Equal("Confirmed", resolved.GetProperty("state").GetString());
        Assert.Equal(6.9271, resolved.GetProperty("geo").GetProperty("lat").GetDouble(), precision: 4);

        await booker.StopAsync();
    }

    [Fact]
    public async Task A_declined_location_request_transmits_no_position()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var bookerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var events = new LiveEvents();
        await using var booker = harness.Passenger(bookerId);
        events.Attach(booker);
        await booker.StartAsync();
        await booker.InvokeAsync(Contract.Methods.SubscribeLocRequest, requestId.ToString());

        // P-02's fence. The payload here even carries a position, and the decline branch has no way
        // to put one on the wire — the rule is in the code path, not in the producer's discipline.
        await harness.PublishAsync(
            EventTopics.RideEvents,
            requestId,
            "location.request.declined",
            Events.LocationRequest(requestId, bookerId, "location.request.declined", 6.9271, 79.8612));

        await FanoutHarness.WaitAsync(() => events.LocationRequests.Count > 0, "the decline should reach the booker");

        var resolved = events.LocationRequests[0];

        Assert.Equal("Declined", resolved.GetProperty("state").GetString());
        Assert.False(
            resolved.TryGetProperty("geo", out var geo) && geo.ValueKind is not System.Text.Json.JsonValueKind.Null,
            "a decline must carry no position (P-02)");

        await booker.StopAsync();
    }

    private static async Task PublishRideAsync(
        FanoutHarness harness, Guid rideId, string eventType, string state, Guid passengerId, Guid driverId)
    {
        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            eventType,
            Events.Ride(rideId, eventType, state, passengerId, driverId, Guid.NewGuid()));

        await WaitForProjectionAsync(harness, rideId);
    }

    private static Task WaitForProjectionAsync(FanoutHarness harness, Guid rideId)
    {
        var projection = harness.Services.GetRequiredService<IRideProjection>();

        return FanoutHarness.WaitAsync(
            () => projection.ReadAsync(rideId, CancellationToken.None).GetAwaiter().GetResult() is not null,
            $"the projection of ride {rideId} should be written");
    }

    private Task<FanoutHarness> StartAsync(bool events = true) =>
        FanoutHarness.StartAsync(redis, redpanda, emqx, new FanoutHarnessOptions
        {
            Pump = false,
            JoinSeedFrames = 0,
            Events = events,
        });
}
