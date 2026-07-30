using System.Diagnostics;
using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Integration;

/// <summary>
/// The DoD's third line: <b>a half-closed socket publishes a retained <c>status=offline</c> within the
/// configured window</b> (T-04).
/// </summary>
/// <remarks>
/// <c>mqtt-topics.md</c> §6: "the TCP adapter emulates LWT on socket half-close by publishing the same
/// retained <c>status=offline</c> — a legacy device that simply loses its socket is indistinguishable,
/// to every consumer, from an MQTT device whose will fired". The three consumers are trip-state-svc's
/// auto-end, dispatch-svc's R-15 grace and fleet-health's rollup, and none of them knows or cares which
/// kind of device it is watching. That is the property under test.
/// </remarks>
[Collection(AdapterCollection.Name)]
[Trait("Category", "Presence")]
public sealed class PresenceTests(EmqxFixture emqx, RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task A_half_closed_socket_publishes_a_retained_offline_within_the_window()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/status");

        var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        await observer.WaitForPresenceAsync(vehicleId, VehicleStatus.Online);

        // The half-close: a FIN with the read half still open, which is what a device losing its uplink
        // actually does and what makes the adapter's ReadAsync return zero.
        var started = Stopwatch.GetTimestamp();
        device.HalfCloseAsync();

        await observer.WaitForPresenceAsync(vehicleId, VehicleStatus.Offline, TimeSpan.FromSeconds(10));

        var elapsed = Stopwatch.GetElapsedTime(started);

        // Adapter:OfflineWindow is 5 s in this harness and in the deployed configuration. The window is a
        // deadline rather than a wait: the publish starts as soon as the read loop sees EOF.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"status=offline took {elapsed} — the configured window is 5 s");

        await device.DisposeAsync();
    }

    [Fact]
    public async Task The_offline_message_is_retained_so_a_later_subscriber_still_learns_it()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var live = await BrokerObserver.SubscribeAsync(emqx, MqttTopics.Status(vehicleId));

        var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));
        await device.SendAsync(Frames.Gt06Login(imei));
        await live.WaitForPresenceAsync(vehicleId, VehicleStatus.Online);

        device.HalfCloseAsync();
        await live.WaitForPresenceAsync(vehicleId, VehicleStatus.Offline);
        await device.DisposeAsync();

        // §3.1: "status is retained so a consumer that subscribes after a device went offline still
        // learns it is offline." This is the assertion that matters for the three LWT consumers, none of
        // which is necessarily running when a tracker drops.
        await using var late = await BrokerObserver.SubscribeAsync(emqx, MqttTopics.Status(vehicleId));

        var replayed = await late.WaitForAsync(
            message => message.Topic == MqttTopics.Status(vehicleId),
            TimeSpan.FromSeconds(10),
            "the retained presence was not replayed to a new subscriber");

        Assert.True(replayed.Retained, "the broker must have kept it as the retained value");
        Assert.Equal(VehicleStatus.Offline, Encoding.UTF8.GetString(replayed.Payload));
    }

    [Fact]
    public async Task A_reconnect_leaves_the_retained_value_online_rather_than_offline()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        var port = await harness.PortFor(ProtocolFamily.Gt06);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, MqttTopics.Status(vehicleId));

        // A device on a marginal cell: the old socket is still draining when the new one authenticates.
        // The guard is SessionRegistry.IsCurrent — an `offline` from a session that has already been
        // replaced would overwrite the replacement's `online`, and the value is retained, so the vehicle
        // would read dark until its next reconnect.
        var first = await DeviceSocket.ConnectAsync(port);
        await first.SendAsync(Frames.Gt06Login(imei));
        await observer.WaitForPresenceAsync(vehicleId, VehicleStatus.Online);

        await using var second = await DeviceSocket.ConnectAsync(port);
        await second.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await second.ReceiveAsync());

        first.HalfCloseAsync();
        await first.DisposeAsync();

        // Give the displaced session time to finish its teardown, then check the retained value with a
        // fresh subscriber.
        await Task.Delay(TimeSpan.FromSeconds(2));

        await using var late = await BrokerObserver.SubscribeAsync(emqx, MqttTopics.Status(vehicleId));

        var retained = await late.WaitForAsync(
            message => message.Topic == MqttTopics.Status(vehicleId),
            TimeSpan.FromSeconds(10),
            "no retained presence for a vehicle whose tracker is connected");

        Assert.Equal(VehicleStatus.Online, Encoding.UTF8.GetString(retained.Payload));
    }

    private static string Imei() =>
        "35" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
}
