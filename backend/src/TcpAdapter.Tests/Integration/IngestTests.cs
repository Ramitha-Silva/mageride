using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Integration;

/// <summary>
/// A device on a real socket to a real EMQX: the whole of ADD §11.4's flow, end to end.
/// </summary>
[Collection(AdapterCollection.Name)]
[Trait("Category", "Ingest")]
public sealed class IngestTests(EmqxFixture emqx, RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task A_GT06_tracker_logs_in_is_acknowledged_and_its_fix_reaches_the_broker()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A", vehicleType: "bus");
        harness.Provisioning.Bind(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/pos/live", "veh/+/status");

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        // ADD §11.4: TCP connect, login packet, GET imei:{IMEI}, GT06 ACK, GPS packet, MQTT PUB.
        await device.SendAsync(Frames.Gt06Login(imei));

        var ack = await device.ReceiveAsync();
        Assert.Equal(Captures.Gt06LoginAck, ack);

        // The retained `online` half of the presence pair, published as an MQTT device does after
        // CONNECT — without it a vehicle would read offline for ever after its first disconnect.
        await observer.WaitForPresenceAsync(vehicleId, VehicleStatus.Online);

        // Stamped now, so this is the *live* path. Captures' frames are fixed and therefore always
        // older than Adapter:ReplayAge, which routes them to pos/replay — correct behaviour, and the
        // wrong thing to assert the live topic with. That routing has its own test below.
        var capturedAt = Frames.ToWholeSecond(DateTimeOffset.UtcNow);

        await device.SendAsync(Frames.Gt06Position(capturedAt));

        var sample = await observer.WaitForSampleAsync(vehicleId);

        Assert.Equal(vehicleId, sample.VehicleId);
        Assert.Equal(Captures.Latitude, sample.Lat, precision: 6);
        Assert.Equal(Captures.Longitude, sample.Lng, precision: 6);
        Assert.Equal(PositionSource.Gt06, sample.Source);
        Assert.Equal(capturedAt, sample.SampleTs);
        Assert.Equal(capturedAt.ToUnixTimeMilliseconds(), sample.Seq);

        // Denormalised from registry.vehicles by the T-11 lookup, so no consumer needs a join.
        Assert.Equal("A", sample.Mode);
        Assert.Equal("bus", sample.VehicleType);

        // The platform's receive clock, which is what makes the gap to sampleTs the replay lag.
        Assert.NotNull(sample.ReceivedTs);
    }

    [Fact]
    public async Task A_primed_IMEI_cache_authenticates_a_device_without_asking_provisioning_svc()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");

        // Present in imei:{imei} means ACTIVE — C030's rule, and there is no cached "revoked". So a hit
        // is the whole answer, which is what keeps a fleet of buses publishing through a
        // provisioning-svc restart. The stub is deliberately left with no binding at all.
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/pos/live");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        await device.SendAsync(Frames.Gt06Position(DateTimeOffset.UtcNow));

        var sample = await observer.WaitForSampleAsync(vehicleId);

        Assert.Equal(vehicleId, sample.VehicleId);
        Assert.Equal(0, harness.Provisioning.ValidateCalls);
    }

    [Fact]
    public async Task An_H02_bus_tracker_publishes_without_ever_logging_in()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");

        // H02 has no login frame: the device id is on every line, so the first position line is also
        // the authentication. The capture's IMEI is the one Captures uses.
        harness.Provisioning.Bind(Captures.Imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/pos/live");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.H02));

        await device.SendAsync(Captures.Ascii(Frames.H02Position(Captures.Imei, DateTimeOffset.UtcNow)));

        var sample = await observer.WaitForSampleAsync(vehicleId);

        Assert.Equal(PositionSource.H02, sample.Source);
        Assert.Equal(Captures.Latitude, sample.Lat, precision: 6);
    }

    [Fact]
    public async Task A_fix_older_than_the_replay_age_goes_to_the_backlog_topic()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        harness.Provisioning.Bind(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(
            emqx, "veh/+/pos/live", "veh/+/pos/replay");

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));

        // The golden capture's stamp is fixed, so against any later clock it is a
        // device's own history arriving late — T-05's case, and the separate topic R-09 splits it onto
        // so a returning fleet's backlog cannot drown live samples.
        await device.SendAsync(Captures.Gt06Position);

        var sample = await observer.WaitForSampleAsync(vehicleId, replay: true);

        Assert.Equal(Captures.CapturedAt, sample.SampleTs);
    }

    [Fact]
    public async Task A_device_talking_the_wrong_protocol_at_a_port_is_closed_rather_than_served()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);

        // An H02 line arriving at the GT06 listener. Every byte fails the start-marker scan, so nothing
        // is ever framed and no identity is presented — the socket goes rather than sitting open for the
        // idle timeout holding a slot in the pod's budget.
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Captures.Ascii(Captures.H02Position));
        await device.SendAsync(Captures.Ascii(Captures.H02Position));
        await device.SendAsync(Captures.Ascii(Captures.H02Position));
        await device.SendAsync(Captures.Ascii(Captures.H02Position));

        // Nothing was published and no session was registered.
        Assert.Equal(0, harness.Sessions.Count);
    }

    [Fact]
    public async Task The_socket_budget_refuses_a_connection_past_the_pods_ceiling()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        // ADD §7.7.6 sizes the plane at 10k sockets per pod; one is the same rule with a number a test
        // can reach. A refused connection is accepted and closed immediately rather than left in the
        // accept backlog, so the device's retry goes to another pod instead of waiting on this one.
        await using var harness = await AdapterHarness.StartAsync(
            emqx, redis, postgres, new Dictionary<string, string?> { ["Adapter:MaxSockets"] = "1" });

        var port = await harness.PortFor(ProtocolFamily.Gt06);
        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var first = await DeviceSocket.ConnectAsync(port);
        await first.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await first.ReceiveAsync());

        await using var second = await DeviceSocket.ConnectAsync(port);

        Assert.True(
            await second.WaitForCloseAsync(TimeSpan.FromSeconds(5)),
            "a connection past the pod's socket budget must be closed immediately");
    }

    /// <summary>A fresh 15-digit IMEI, so tests do not share a Redis key or a stub binding.</summary>
    private static string Imei() =>
        "35" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
}
