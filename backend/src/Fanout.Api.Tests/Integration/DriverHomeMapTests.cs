using MageRide.Fanout.Realtime;
using MageRide.Fanout.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.TestKit;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Integration;

/// <summary>
/// AL-31: the driver home map renders the driver's <b>own active vehicle only</b>.
/// </summary>
/// <remarks>
/// DoD: "a driver's home map subscription receives only their own vehicle." The fence is enforced by
/// what the server joins, not by what the client asks for — there is no hub method that takes a
/// vehicle id, so no request a driver app could make would put another driver's vehicle on this
/// stream.
/// </remarks>
[Collection<FanoutCollection>]
public sealed class DriverHomeMapTests(RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task A_driver_receives_their_own_vehicle_and_no_other()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var driverId = Guid.NewGuid();
        var ownVehicle = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();

        // registry-svc's published go-live selection is what decides which vehicle is "own" (D-03).
        await harness.Positions.SelectLiveVehicleAsync(driverId, ownVehicle);

        var events = new LiveEvents();
        await using var driver = harness.Driver(driverId);
        events.Attach(driver);
        await driver.StartAsync();

        var connections = harness.Services.GetRequiredService<IHubConnections>();

        await FanoutHarness.WaitAsync(
            () => connections.WatchedVehicles.Contains(ownVehicle),
            "the driver should be joined to their own vehicle's stream on connect");

        // Both vehicles are parked in the same place and both are publishing.
        await harness.Positions.PublishAsync(ownVehicle, Samples.ColomboFort);
        await harness.Positions.PublishAsync(someoneElse, Samples.ColomboFort);

        await harness.VehiclesAsync();

        await FanoutHarness.WaitAsync(() => events.Saw(ownVehicle), "the driver should see their own vehicle");

        Assert.False(
            events.Saw(someoneElse), "another driver's active vehicle must never reach the driver home map");

        Assert.DoesNotContain(someoneElse, connections.WatchedVehicles);

        await driver.StopAsync();
    }

    [Fact]
    public async Task A_driver_who_has_selected_nothing_is_joined_to_nothing()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var driverId = Guid.NewGuid();

        await using var driver = harness.Driver(driverId);
        await driver.StartAsync();
        await Task.Delay(300);

        var connections = harness.Services.GetRequiredService<IHubConnections>();
        var connectionId = Assert.Single(connections.ConnectionsOf(driverId));

        // Before go-live there is no vehicle to draw, which is what the screen shows anyway.
        Assert.Equal(0, connections.VehicleCountOf(connectionId));

        await driver.StopAsync();
    }

    [Fact]
    public async Task A_passenger_is_never_joined_to_a_vehicle_they_merely_happen_to_own_the_id_of()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        // The same user id, with a passenger token instead of a driver one. The own-vehicle lookup
        // is gated on the `driver` role: a passenger's id can never name a live vehicle, and asking
        // Redis about it on every connect would put a wasted round trip on every passenger's
        // handshake.
        var userId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();

        await harness.Positions.SelectLiveVehicleAsync(userId, vehicleId);

        await using var passenger = harness.Passenger(userId);
        await passenger.StartAsync();
        await Task.Delay(300);

        var connections = harness.Services.GetRequiredService<IHubConnections>();
        var connectionId = Assert.Single(connections.ConnectionsOf(userId));

        Assert.Equal(0, connections.VehicleCountOf(connectionId));

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_drivers_own_vehicle_stays_visible_to_them_while_it_is_on_hire()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();

        await harness.Positions.SelectLiveVehicleAsync(driverId, vehicleId);

        // US-7.16 takes an engaged Mode C vehicle off the *public* map. AL-31's home map is the one
        // place it should still be drawn — the driver is in it.
        await harness.Services.GetRequiredService<Visibility.IVisibilityIndex>()
            .EngageAsync(vehicleId, Guid.NewGuid(), CancellationToken.None);

        var events = new LiveEvents();
        await using var driver = harness.Driver(driverId);
        events.Attach(driver);
        await driver.StartAsync();

        await FanoutHarness.WaitAsync(
            () => harness.Services.GetRequiredService<IHubConnections>().WatchedVehicles.Contains(vehicleId),
            "the driver should hold their own vehicle's stream");

        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala);
        await harness.VehiclesAsync();

        await FanoutHarness.WaitAsync(
            () => events.Saw(vehicleId), "a driver on a hire should still see their own vehicle");

        // And the public map does not.
        var onlooker = new LiveEvents();
        await using var passenger = harness.Passenger(Guid.NewGuid());
        onlooker.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Dehiwala).ToArray());

        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala, seq: 2);
        await harness.CellsAsync();
        await Task.Delay(300);

        Assert.False(onlooker.Saw(vehicleId), "US-7.16 still holds for everybody else");

        await driver.StopAsync();
        await passenger.StopAsync();
    }

    private Task<FanoutHarness> StartAsync() =>
        FanoutHarness.StartAsync(redis, redpanda, emqx, new FanoutHarnessOptions
        {
            Pump = false,

            // No broker: AL-31 is decided from `lock:driver:{driverId}` and nothing else.
            Events = false,
            ControlPlane = false,
        });
}
