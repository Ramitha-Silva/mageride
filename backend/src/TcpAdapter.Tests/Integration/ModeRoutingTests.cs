using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Integration;

/// <summary>
/// The DoD's fourth line: <b>Mode C tracker GPS is accepted only while the driver is Online; Mode A is
/// accepted regardless</b> (T-11).
/// </summary>
/// <remarks>
/// <para>
/// ADD §7.7.7, read literally. Mode C: "tracker GPS for a Mode C vehicle is ingested only while the
/// vehicle is Online (the driver has gone online in the app) — pings sent while offline are rejected and
/// <b>never reach the live map or dispatch</b>". Mode A: "no driver-app session is required for position
/// to publish; the tracker is the authoritative and only source". Mode B (US-3.23): like Mode A for
/// tracker-installed vehicles.
/// </para>
/// <para>
/// Two facts decide it and they live in two different stores, which is why this suite needs both
/// containers: the vehicle's <b>mode</b> is <c>registry.vehicles.mode</c> in Postgres, and "online" is
/// <c>veh:driver:{vehicleId}</c> in Redis — the standby binding dispatch-svc writes at
/// <c>POST /v1/standby/online</c> and deletes when the driver goes off duty. The gate agreeing with both
/// is the property under test; agreeing with a mock of either would prove nothing.
/// </para>
/// </remarks>
[Collection(AdapterCollection.Name)]
[Trait("Category", "ModeRouting")]
public sealed class ModeRoutingTests(EmqxFixture emqx, RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task A_Mode_C_tracker_is_refused_while_its_driver_is_offline_and_accepted_once_online()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "C", vehicleType: "three_wheeler");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);
        await harness.TakeDriverOfflineAsync(vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(
            emqx, "veh/+/pos/live", "veh/+/pos/replay");

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await device.ReceiveAsync());

        // The tracker in a parked three-wheeler reports all night. Nothing reaches the broker.
        await device.SendAsync(Captures.Gt06Position);
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.False(
            observer.Saw(message => message.Topic.Contains(vehicleId.ToString(), StringComparison.Ordinal)),
            "a Mode C tracker's fix must not reach the plane while its driver is offline");

        // The driver goes online in the app. dispatch-svc writes the standby binding, and the very next
        // frame is publishable — the gate is per sample, not per session, because the fact it reads
        // changes while a socket is open.
        await harness.BringDriverOnlineAsync(vehicleId, driverId);
        await device.SendAsync(Captures.Gt06Position);

        var sample = await observer.WaitForSampleAsync(vehicleId, replay: true);

        Assert.Equal(vehicleId, sample.VehicleId);
        Assert.Equal(VehicleProfile.ModeC, sample.Mode);
        Assert.Equal("three_wheeler", sample.VehicleType);
    }

    [Fact]
    public async Task A_Mode_A_bus_publishes_with_no_driver_app_session_at_all()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A", vehicleType: "bus");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        // Deliberately absent: §7.7.7 makes the tracker "the authoritative and only source" for a Mode A
        // vehicle, and US-3.22 has the journey start on ignition with "the mobile app not needed".
        await harness.TakeDriverOfflineAsync(vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/pos/replay");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        await device.SendAsync(Captures.Gt06Position);

        var sample = await observer.WaitForSampleAsync(vehicleId, replay: true);

        Assert.Equal(VehicleProfile.ModeA, sample.Mode);
    }

    [Fact]
    public async Task A_Mode_B_vehicle_is_treated_like_Mode_A()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "B", vehicleType: "van");
        await harness.TakeDriverOfflineAsync(vehicleId);

        // US-3.23: a tracker-installed Mode B vehicle auto-starts and auto-ends on ignition with no app,
        // and the Epic 4 sharing grant applies to tracker-sourced positions identically. So the gate does
        // not apply to it.
        var verdict = await harness.Gate.EvaluateAsync(vehicleId, CancellationToken.None);

        Assert.True(verdict.Publishable);
        Assert.Equal("B", verdict.Profile!.Mode);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public async Task A_vehicle_the_registry_does_not_have_takes_the_configured_direction()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var vehicleId = Guid.NewGuid();

        // Open, which is the default and is argued at Adapter:PublishWhenModeUnknown: closed means a
        // database blip takes every Mode A bus on the platform off the live map, and there is no app for
        // those to fall back to.
        await using var open = await AdapterHarness.StartAsync(emqx, redis, postgres);

        var admitted = await open.Gate.EvaluateAsync(vehicleId, CancellationToken.None);

        Assert.True(admitted.Publishable);
        Assert.Null(admitted.Profile);

        // And the other way for a deployment that would rather lose Mode A telemetry than admit an
        // offline Mode C ping.
        await using var closed = await AdapterHarness.StartAsync(
            emqx, redis, postgres, new Dictionary<string, string?> { ["Adapter:PublishWhenModeUnknown"] = "false" });

        var refused = await closed.Gate.EvaluateAsync(vehicleId, CancellationToken.None);

        Assert.False(refused.Publishable);
        Assert.Equal(ModeGate.ReasonNoVehicle, refused.Reason);
    }

    [Fact]
    public async Task The_gate_reads_the_standby_binding_and_not_the_availability_phase()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "C", vehicleType: "sedan");

        // A driver mid-ride is not AVAILABLE and is emphatically online. Gating on the availability
        // hash's phase would take a Mode C vehicle off the map the moment it was hired, which is the
        // opposite of what dispatch needs — so the gate reads only the binding's presence.
        await harness.BringDriverOnlineAsync(vehicleId, Guid.NewGuid());

        var verdict = await harness.Gate.EvaluateAsync(vehicleId, CancellationToken.None);

        Assert.True(verdict.Publishable);

        await harness.TakeDriverOfflineAsync(vehicleId);

        var offline = await harness.Gate.EvaluateAsync(vehicleId, CancellationToken.None);

        Assert.False(offline.Publishable);
        Assert.Equal(ModeGate.ReasonModeCOffline, offline.Reason);
    }

    private static string Imei() =>
        "35" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
}
