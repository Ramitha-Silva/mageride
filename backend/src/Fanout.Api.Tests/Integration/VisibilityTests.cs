using MageRide.Fanout.Tests.Infrastructure;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Messaging;
using MageRide.Shared.Realtime;
using MageRide.TestKit;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Integration;

/// <summary>
/// D6' §5.2's public-map filter, through a real socket: what a passenger watching a cell may and may
/// not be shown.
/// </summary>
[Collection<FanoutCollection>]
public sealed class VisibilityTests(RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task Mode_A_and_idle_Mode_C_reach_the_public_map_and_Mode_B_does_not()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var bus = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var threeWheeler = Guid.NewGuid();

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.ColomboFort).ToArray());

        await harness.Positions.PublishAsync(bus, Samples.ColomboFort, mode: "A", vehicleType: "bus");
        await harness.Positions.PublishAsync(shared, Samples.ColomboFort, mode: "B", vehicleType: "van");
        await harness.Positions.PublishAsync(threeWheeler, Samples.ColomboFort, mode: "C");

        await harness.CellsAsync();
        await FanoutHarness.WaitAsync(() => events.Saw(bus) && events.Saw(threeWheeler), "the public vehicles");

        // D-23. The private one is in the same cell, in the same batch window, and reaches nobody:
        // its watchers are a group of their own, which is what makes the entitlement check a join
        // rather than a per-frame test.
        Assert.False(events.Saw(shared), "a Mode B vehicle must never reach a public geocell group");

        await passenger.StopAsync();
    }

    /// <summary>
    /// DoD: "an engaged Mode C vehicle disappears from public groups and appears only in its ride
    /// group."
    /// </summary>
    [Fact]
    public async Task An_engaged_Mode_C_vehicle_leaves_the_public_map_and_appears_only_in_its_ride()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(events: true);

        var vehicleId = Guid.NewGuid();
        var rideId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        var onlooker = new LiveEvents();
        await using var bystander = harness.Passenger(Guid.NewGuid());
        onlooker.Attach(bystander);
        await bystander.StartAsync();
        await bystander.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.ColomboFort).ToArray());

        // Idle, so the whole cell can see it.
        await harness.Positions.PublishAsync(vehicleId, Samples.ColomboFort);
        await harness.CellsAsync();
        await FanoutHarness.WaitAsync(() => onlooker.Saw(vehicleId), "the idle three-wheeler on the public map");

        // The passenger books it. `ride.accepted` is what makes the vehicle engaged.
        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "ride.accepted",
            Events.Ride(rideId, "ride.accepted", "Accepted", passengerId, Guid.NewGuid(), vehicleId, version: 3));

        await FanoutHarness.WaitAsync(
            () => harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleEngagement(vehicleId)),
            "the engagement mark should be written from ride.accepted");

        var rider = new LiveEvents();
        await using var onRide = harness.Passenger(passengerId);
        rider.Attach(onRide);
        await onRide.StartAsync();
        await onRide.InvokeAsync(Contract.Methods.SubscribeRide, rideId.ToString());

        onlooker.Clear();

        await harness.Positions.PublishAsync(vehicleId, Samples.ColomboFort, seq: 2);
        await harness.PumpAsync();

        // The passenger on the ride keeps seeing it, on the one channel US-7.16 leaves open.
        await FanoutHarness.WaitAsync(
            () => rider.DriverPositions.Any(position => position.RideId == rideId),
            "the assigned ride should still receive DriverPosition");

        // And the bystander is told to drop the marker, once, with the reason the contract names —
        // rather than simply going quiet, which a client cannot distinguish from a stationary car.
        await FanoutHarness.WaitAsync(
            () => onlooker.WasRemoved(vehicleId, VehicleRemovalReasons.Engaged),
            "the public map should be told the vehicle is engaged");

        Assert.False(onlooker.Saw(vehicleId), "an engaged vehicle must not reach a public geocell group");

        await onRide.StopAsync();
        await bystander.StopAsync();
    }

    [Fact]
    public async Task A_completed_ride_puts_the_vehicle_back_on_the_public_map()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(events: true);

        var vehicleId = Guid.NewGuid();
        var rideId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "ride.accepted",
            Events.Ride(rideId, "ride.accepted", "Accepted", passengerId, Guid.NewGuid(), vehicleId));

        await FanoutHarness.WaitAsync(
            () => harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleEngagement(vehicleId)),
            "the vehicle should be engaged first");

        // PaymentPending, not a cancellation: the passenger is out of the car and dispatch-svc has
        // already released the driver, so holding the vehicle off the map until the money settles
        // would hide a driver who is available to be booked.
        await harness.PublishAsync(
            EventTopics.RideEvents,
            rideId,
            "ride.completed",
            Events.Ride(rideId, "ride.completed", "PaymentPending", passengerId, Guid.NewGuid(), vehicleId,
                version: 8));

        await FanoutHarness.WaitAsync(
            () => !harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleEngagement(vehicleId)),
            "the terminal should release the vehicle");

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Dehiwala).ToArray());

        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala, seq: 9);
        await harness.CellsAsync();

        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the released vehicle should be public again");

        await passenger.StopAsync();
    }

    /// <summary>
    /// A vehicle replaying an offline backlog must not walk across the live map (US-7.17, R-17).
    /// </summary>
    /// <remarks>
    /// The backlog travels the same <c>cell:{h3index}</c> stream as live traffic — position-processor-svc
    /// writes both through one path — so the only thing that tells them apart is the sample's own
    /// capture instant. Without the check, an hour of history would arrive as an hour of current
    /// positions, oldest last.
    /// </remarks>
    [Fact]
    public async Task A_sample_older_than_the_freshness_window_is_never_drawn()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(freshness: TimeSpan.FromSeconds(10));

        var vehicleId = Guid.NewGuid();
        var events = new LiveEvents();

        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Moratuwa).ToArray());

        await harness.Positions.PublishAsync(
            vehicleId, Samples.Moratuwa, sampleTs: DateTimeOffset.UtcNow.AddMinutes(-30));

        await harness.CellsAsync();
        await Task.Delay(400);

        Assert.False(events.Saw(vehicleId), "a half-hour-old fix is history, not a current position");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_vehicle_that_stops_reporting_is_removed_from_the_public_map()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        // Six seconds, not two: `FanoutOptions.FreshnessWindow` has a five-second floor, because a
        // window shorter than the 2–8 s batch band would remove every vehicle between ticks.
        await using var harness = await StartAsync(freshness: TimeSpan.FromSeconds(6));

        var vehicleId = Guid.NewGuid();
        var events = new LiveEvents();

        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Kandy).ToArray());

        await harness.Positions.PublishAsync(vehicleId, Samples.Kandy);
        await harness.CellsAsync();
        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the vehicle should reach the map first");

        // Nothing more is published. US-7.17 is detected by absence — a vehicle that has stopped
        // reporting produces no frames at all, so a client that inferred removal from a batch not
        // mentioning it would erase every stationary vehicle every tick.
        await Task.Delay(TimeSpan.FromSeconds(6.5));
        await harness.CellsAsync();

        await FanoutHarness.WaitAsync(
            () => events.WasRemoved(vehicleId, VehicleRemovalReasons.Stale),
            "a vehicle past the freshness window should be removed");

        await passenger.StopAsync();
    }

    /// <summary>
    /// A vehicle driving from one of the passenger's cells into another must not flicker.
    /// </summary>
    /// <remarks>
    /// It stops appearing in the first cell's stream, which is indistinguishable from having stopped
    /// reporting — so a naive sweep would tell the client to erase a marker the very next batch puts
    /// back, once every window, for every moving vehicle on the map.
    /// </remarks>
    [Fact]
    public async Task A_vehicle_that_moves_to_another_cell_is_not_announced_as_stale()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(freshness: TimeSpan.FromSeconds(6));

        var vehicleId = Guid.NewGuid();

        // Both points are inside one 19-cell view and in different res-7 cells, which is the
        // ordinary case: a res-7 hexagon is about 1.2 km on a side and the view is ~3 km across.
        var from = Samples.ColomboFort;
        var to = new Shared.Primitives.GeoPoint(from.Latitude + 0.022, from.Longitude);

        var view = GeoCells.ViewCells(from).ToArray();

        Assert.NotEqual(GeoCells.ViewCell(from), GeoCells.ViewCell(to));
        Assert.Contains(GeoCells.ViewCell(to), view);

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(Contract.Methods.JoinGeocells, view);

        await harness.Positions.PublishAsync(vehicleId, from);
        await harness.CellsAsync();
        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the vehicle should reach the map first");

        // It drives on, and keeps reporting — from the other cell.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var seq = 2L;

        while (DateTime.UtcNow < deadline)
        {
            await harness.Positions.PublishAsync(vehicleId, to, seq: seq++);
            await harness.CellsAsync();
            await Task.Delay(500);
        }

        Assert.DoesNotContain(
            events.Removed,
            entry => entry.VehicleId == vehicleId && entry.Reason == VehicleRemovalReasons.Stale);

        await passenger.StopAsync();
    }

    /// <summary>
    /// A second ride's event must not release a vehicle carrying somebody else's passenger.
    /// </summary>
    /// <remarks>
    /// <c>ride.events</c> is partitioned by <c>rideId</c>, so nothing orders two rides' events
    /// against each other: an offer that expired <em>before</em> an accept can be consumed after it.
    /// An unconditional release there would put an occupied taxi back on the public map for the rest
    /// of the trip, which is the one thing US-7.16 exists to prevent.
    /// </remarks>
    [Fact]
    public async Task Another_rides_event_cannot_release_a_vehicle_that_is_on_hire()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(events: true);

        var vehicleId = Guid.NewGuid();
        var live = Guid.NewGuid();
        var other = Guid.NewGuid();

        await harness.PublishAsync(
            EventTopics.RideEvents,
            live,
            "ride.accepted",
            Events.Ride(live, "ride.accepted", "Accepted", Guid.NewGuid(), Guid.NewGuid(), vehicleId));

        await FanoutHarness.WaitAsync(
            () => harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleEngagement(vehicleId)),
            "the vehicle should be engaged on the live ride");

        // The stale offer for a different ride, naming the same vehicle, arriving late.
        await harness.PublishAsync(
            EventTopics.RideEvents,
            other,
            "offer.expired",
            Events.Ride(other, "offer.expired", "Matching", Guid.NewGuid(), Guid.NewGuid(), vehicleId));

        // Wait for it to be consumed — through a fact of its own, so this is not a sleep.
        await FanoutHarness.WaitAsync(
            () => harness.Services.GetRequiredService<Rides.IRideProjection>()
                .ReadAsync(other, CancellationToken.None).GetAwaiter().GetResult() is not null,
            "the other ride's event should have been consumed");

        Assert.True(
            harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleEngagement(vehicleId)),
            "a different ride's terminal must not release a vehicle that is on hire");

        await FanoutHarness.WaitAsync(
            () => harness.Redis.GetDatabase().StringGet(RedisKeys.VehicleEngagement(vehicleId)) == live.ToString(),
            "and the hire recorded should still be the live one");
    }

    /// <summary>
    /// The <c>offline</c> half of US-7.17: an EMQX last will, against the deployed broker policy.
    /// </summary>
    [Fact]
    public async Task An_EMQX_last_will_takes_a_vehicle_off_the_map_before_the_window_elapses()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(presence: true, freshness: TimeSpan.FromMinutes(10));
        await harness.WaitForPresenceAsync();

        var vehicleId = Guid.NewGuid();
        var events = new LiveEvents();

        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.ColomboFort).ToArray());

        await harness.Positions.PublishAsync(vehicleId, Samples.ColomboFort);
        await harness.CellsAsync();
        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the vehicle should reach the map first");

        // The freshness window is ten minutes here, so nothing but the last will can produce a
        // removal — which is the point: this asserts the LWT path and not the sweep behind it.
        await harness.PublishStatusAsync(vehicleId, "offline");

        await FanoutHarness.WaitAsync(
            () => harness.Redis.GetDatabase().KeyExists(RedisKeys.VehicleOfflineAt(vehicleId)),
            "the last will should be recorded");

        events.Clear();

        await harness.Positions.PublishAsync(vehicleId, Samples.ColomboFort, seq: 2,
            sampleTs: DateTimeOffset.UtcNow.AddSeconds(-5));

        await harness.CellsAsync();

        await FanoutHarness.WaitAsync(
            () => events.WasRemoved(vehicleId, VehicleRemovalReasons.Offline),
            "an offline vehicle should be dropped from public groups");

        Assert.False(events.Saw(vehicleId), "and its position must not be delivered");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_vehicle_that_resumes_publishing_comes_back_without_an_online_message()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var vehicleId = Guid.NewGuid();
        var index = harness.Services.GetRequiredService<IVisibilityIndex>();

        await index.MarkOfflineAsync(vehicleId, DateTimeOffset.UtcNow.AddSeconds(-30), CancellationToken.None);

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Dehiwala).ToArray());

        // A device that crashed and restarted may never send an `online`. The mark holds an instant
        // rather than a flag precisely so a fresher sample is enough to bring it back.
        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala);
        await harness.CellsAsync();

        await FanoutHarness.WaitAsync(
            () => events.Saw(vehicleId), "a fresher sample should override an older last will");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task The_join_seed_is_filtered_exactly_like_a_live_batch()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(seedFrames: 32);

        var shared = Guid.NewGuid();
        var bus = Guid.NewGuid();

        // Both are already in the cell before anybody is watching, which is what the seed replays.
        await harness.Positions.PublishAsync(shared, Samples.Moratuwa, mode: "B", vehicleType: "van");
        await harness.Positions.PublishAsync(bus, Samples.Moratuwa, mode: "A", vehicleType: "bus");

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Moratuwa).ToArray());

        await FanoutHarness.WaitAsync(() => events.Saw(bus), "the seed should carry the public vehicle");

        // A replay that showed a private vehicle would be the D-22 leak with a two-second delay on
        // it — which is exactly the kind of hole a stand-in path is where they hide.
        Assert.False(events.Saw(shared), "the seed must not replay a Mode B vehicle to a stranger");

        await passenger.StopAsync();
    }

    private Task<FanoutHarness> StartAsync(
        bool events = false,
        bool presence = false,
        int seedFrames = 0,
        TimeSpan? freshness = null) =>
        FanoutHarness.StartAsync(redis, redpanda, emqx, new FanoutHarnessOptions
        {
            Pump = false,
            JoinSeedFrames = seedFrames,
            Events = events,
            Presence = presence,
            FreshnessWindow = freshness,
            ControlPlane = events,
        });
}
