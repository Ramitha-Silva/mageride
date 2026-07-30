using System.Diagnostics;
using MageRide.Fanout.Realtime;
using MageRide.Fanout.Tests.Infrastructure;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Geo;
using MageRide.Shared.Messaging;
using MageRide.TestKit;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Integration;

/// <summary>
/// D-23's entitlement cache and D-22's directed revocation, end to end over a real broker.
/// </summary>
[Collection<FanoutCollection>]
public sealed class EntitlementTests(RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task An_entitled_passenger_sees_the_shared_vehicle_and_a_stranger_does_not()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var vehicleId = Guid.NewGuid();
        var entitled = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await GrantAsync(harness, vehicleId, entitled);

        var seen = new LiveEvents();
        var unseen = new LiveEvents();

        await using var watcher = harness.Passenger(entitled);
        seen.Attach(watcher);
        await watcher.StartAsync();

        await using var outsider = harness.Passenger(stranger);
        unseen.Attach(outsider);
        await outsider.StartAsync();

        // Both are watching the same map square. The difference is the grant, and nothing else.
        var view = GeoCells.ViewCells(Samples.ColomboFort).ToArray();
        await watcher.InvokeAsync(Contract.Methods.JoinGeocells, view);
        await outsider.InvokeAsync(Contract.Methods.JoinGeocells, view);

        await harness.Positions.PublishAsync(vehicleId, Samples.ColomboFort, mode: "B", vehicleType: "van");
        await harness.PumpAsync();

        await FanoutHarness.WaitAsync(() => seen.Saw(vehicleId), "the entitled passenger should see the van");

        Assert.False(unseen.Saw(vehicleId), "an unentitled passenger in the same cell must see nothing");

        await watcher.StopAsync();
        await outsider.StopAsync();
    }

    /// <summary>
    /// DoD: "revoking a Mode B share removes the passenger from the vehicle's stream immediately,
    /// without waiting for a cell crossing."
    /// </summary>
    /// <remarks>
    /// The second half of that sentence is the load-bearing one, and it is what the test is built
    /// around: after the revocation the passenger calls nothing, joins nothing and crosses no
    /// boundary. The only thing that happens is the event.
    /// </remarks>
    [Fact]
    public async Task Revoking_a_share_removes_the_passenger_from_the_vehicles_stream_at_once()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var vehicleId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        await GrantAsync(harness, vehicleId, passengerId);

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(passengerId);
        events.Attach(passenger);
        await passenger.StartAsync();

        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala, mode: "B", vehicleType: "van");
        await harness.VehiclesAsync();

        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the entitled passenger should see the van");

        var connections = harness.Services.GetRequiredService<IHubConnections>();
        var connectionId = Assert.Single(connections.ConnectionsOf(passengerId));
        Assert.True(connections.Watches(connectionId, vehicleId));

        events.Clear();

        await harness.PublishAsync(
            EventTopics.RegistryEvents, vehicleId, "share.revoked", Events.Share(vehicleId, passengerId));

        // The client is told, so it can drop a marker that would otherwise sit at its last position
        // looking live.
        await FanoutHarness.WaitAsync(
            () => events.SharesRevoked.Contains(vehicleId), "the passenger should be told the share is gone");

        Assert.False(connections.Watches(connectionId, vehicleId));

        var entitlements = harness.Services.GetRequiredService<IEntitlementCache>();
        Assert.False(await entitlements.IsEntitledAsync(passengerId, vehicleId, CancellationToken.None));

        // And the frames stop, with no cell crossing and no re-join in between.
        await harness.Positions.PublishAsync(vehicleId, Samples.Dehiwala, mode: "B", vehicleType: "van", seq: 2);
        await harness.VehiclesAsync();
        await Task.Delay(300);

        Assert.False(events.Saw(vehicleId), "a revoked passenger must stop receiving the vehicle");

        await passenger.StopAsync();
    }

    /// <summary>
    /// D-22's "typical removal latency &lt; 200 ms", measured across the hop it is a budget for.
    /// </summary>
    /// <remarks>
    /// The budget is about the fan-out plane, not about Redpanda: what §11.10 promises is that a
    /// revocation reaches the passenger's socket without waiting for their next cell crossing, and
    /// the part of that under this component's control is the control-channel hop. So the signal is
    /// published directly here, and the clock runs from that publish to the client's event —
    /// deliberately across two replicas, because a single-process measurement would prove the
    /// cheapest case.
    /// </remarks>
    [Fact]
    public async Task A_revocation_crosses_replicas_inside_the_D22_budget()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(replicas: 2, events: false);

        var vehicleId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        await harness.Services.GetRequiredService<IEntitlementCache>()
            .GrantAsync(passengerId, vehicleId, CancellationToken.None);

        var events = new LiveEvents();

        // The passenger is on replica 1; the revocation is published from replica 0. Without the
        // control channel this send would simply never arrive, which is the failure a single-replica
        // test cannot see.
        await using var passenger = harness.Passenger(passengerId, replica: 1);
        events.Attach(passenger);
        await passenger.StartAsync();

        await FanoutHarness.WaitAsync(
            () => harness.Replicas[1].GetRequiredService<IHubConnections>().WatchedVehicles.Contains(vehicleId),
            "the passenger's replica should have joined them to the vehicle's stream");

        var control = harness.Replicas[0].GetRequiredService<IFanoutControlPlane>();
        var clock = Stopwatch.StartNew();

        await control.PublishAsync(
            new FanoutSignal(FanoutSignalKinds.ShareRevoked, passengerId, vehicleId), CancellationToken.None);

        await FanoutHarness.WaitAsync(
            () => events.SharesRevoked.Contains(vehicleId), "the revocation should cross to the other replica");

        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromMilliseconds(200),
            $"The directed removal took {clock.Elapsed.TotalMilliseconds:F0} ms; D-22's budget is 200 ms.");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_grant_accepted_mid_connection_takes_effect_without_a_reconnect()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var vehicleId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        var events = new LiveEvents();
        await using var passenger = harness.Passenger(passengerId);
        events.Attach(passenger);
        await passenger.StartAsync();

        // The socket is long-lived — a map that only shows a newly-shared vehicle after the app is
        // restarted is a map that looks broken.
        await GrantAsync(harness, vehicleId, passengerId);

        await FanoutHarness.WaitAsync(
            () => harness.Services.GetRequiredService<IHubConnections>().WatchedVehicles.Contains(vehicleId),
            "the grant should join the live connection to the vehicle's stream");

        await harness.Positions.PublishAsync(vehicleId, Samples.Moratuwa, mode: "B", vehicleType: "van");
        await harness.VehiclesAsync();

        await FanoutHarness.WaitAsync(() => events.Saw(vehicleId), "the newly-shared vehicle should arrive");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_share_event_that_names_no_passenger_is_skipped_rather_than_stalling_the_partition()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var orphan = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var passengerId = Guid.NewGuid();

        // D6' §5.1's own payload shape — `{vehicleId}` and nothing else — is one this service cannot
        // act on. Stalling here would stop every later revocation behind it, which turns one
        // unusable message into an unbounded visibility leak.
        await harness.PublishAsync(
            EventTopics.RegistryEvents, orphan, "share.revoked", new { vehicleId = orphan });

        await GrantAsync(harness, vehicleId, passengerId);

        Assert.True(
            await harness.Services.GetRequiredService<IEntitlementCache>()
                .IsEntitledAsync(passengerId, vehicleId, CancellationToken.None),
            "a later, well-formed grant must still be applied");
    }

    /// <summary>Publishes <c>share.granted</c> and waits for the cache to catch up.</summary>
    private static async Task GrantAsync(FanoutHarness harness, Guid vehicleId, Guid passengerId)
    {
        await harness.PublishAsync(
            EventTopics.RegistryEvents, vehicleId, "share.granted", Events.Share(vehicleId, passengerId));

        var entitlements = harness.Services.GetRequiredService<IEntitlementCache>();

        await FanoutHarness.WaitAsync(
            () => entitlements.IsEntitledAsync(passengerId, vehicleId, CancellationToken.None).GetAwaiter().GetResult(),
            $"share:{passengerId} should contain {vehicleId}");
    }

    private Task<FanoutHarness> StartAsync(int replicas = 1, bool events = true) =>
        FanoutHarness.StartAsync(
            redis,
            redpanda,
            emqx,
            new FanoutHarnessOptions
            {
                Pump = false,
                JoinSeedFrames = 0,
                Events = events,

                // A shared group across replicas, so exactly one of them consumes each event —
                // which is the deployment this component's backplane exists for.
                ConsumerGroup = $"fanout-{Guid.NewGuid():N}",
            },
            replicas);
}
