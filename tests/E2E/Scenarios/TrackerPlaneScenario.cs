using System.Net;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Primitives;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// The hardware tracker plane, from a socket to the hypertable: all four protocol adapters, the
/// binding a frame is resolved through, and what happens when a device is refused or goes away
/// (D6' §4, T-01 … T-12).
/// </summary>
/// <remarks>
/// <para>
/// <b>C121's second fence, and the reason this file exists.</b> Every frame here is written to a
/// real TCP or UDP socket on tcp-adapter's own listener; nothing publishes to EMQX on a device's
/// behalf. What is asserted is the far end — a row in <c>telemetry.positions</c> carrying the
/// coordinates the device encoded and the family code of the decoder that read them — so a
/// scenario that passes has taken bytes through the adapter, the broker, mqtt-bridge-svc,
/// <c>telemetry.raw</c>, position-processor-svc, <c>telemetry.normalized</c> and
/// persistence-writer-svc.
/// </para>
/// <para>
/// <b>The IMEI is bound through the Fleet Portal, not written into a cache.</b> US-13.12's
/// <c>POST /v1/fleets/{id}/trackers/bind</c> forwards the operator's own bearer to provisioning-svc,
/// which mints the credential and writes <c>prov.tracker_bindings</c> and <c>imei:{imei}</c> — the
/// two sources T-03 says the adapter resolves through, in that order.
/// </para>
/// </remarks>
[Collection<ModeAbCollection>]
[Trait("Category", "ModeAB")]
public sealed class TrackerPlaneScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeAbScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>
    /// T-01 — every one of D6' §4.1's four adapters carries a real frame to the hypertable.
    /// </summary>
    /// <remarks>
    /// One theory case per family, and the coordinates are asserted rather than the row count: a
    /// decoder that is wrong about a hemisphere bit, a BCD nibble, a knot or a time zone produces a
    /// number that visibly disagrees, and a test that only counted rows would not notice. The
    /// <c>source</c> code is asserted too, because it is the only field that says which decoder
    /// produced them.
    /// </remarks>
    [Theory]
    [InlineData(ProtocolFamily.Gt06, 1)]
    [InlineData(ProtocolFamily.Jt808, 2)]
    [InlineData(ProtocolFamily.H02, 3)]
    [InlineData(ProtocolFamily.NmeaUdp, 4)]
    public async Task Every_protocol_adapter_carries_a_fix_to_the_hypertable(ProtocolFamily family, int source) =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            await using var device = await TrackerDevice.ConnectAsync(fleet, family, bus.Imei);

            var reported = await device.ReportAsync(bus.Depot, speedKph: 30);

            await fleet.WaitForTelemetryAsync(bus.VehicleId, new ReportedFix(bus.Depot, reported));

            var landed = await fleet.NewestTelemetryAsync(bus.VehicleId);

            Assert.NotNull(landed);

            // Within a metre. Every one of these formats quantises a coordinate — GT06 to 1/1,800,000
            // of a degree, JT/T 808 to a millionth, and the two ASCII families to four decimal
            // minutes — so an exact comparison would be asserting the quantisation rather than the
            // decode.
            Assert.True(
                ModeAbFleet.DistanceM(bus.Depot, new GeoPoint(landed.Lat, landed.Lng)) < 2,
                $"{ProtocolFamilies.Name(family)} decoded {landed.Lat},{landed.Lng} for a device that "
                + $"reported {bus.Depot.Latitude},{bus.Depot.Longitude}.");

            Assert.Equal(source, landed.Source);
            Assert.Equal(bus.Org.FleetId, landed.FleetId);

            // 30 km/h, whatever the format spelled it in: GT06 a whole byte of km/h, JT/T 808 tenths
            // of km/h, and H02 and NMEA both knots — which read as km/h understate a coach by 1.85×
            // and would pass every ADD §12.6 threshold on the way through.
            Assert.NotNull(landed.SpeedMps);
            Assert.True(
                Math.Abs(landed.SpeedMps!.Value - (30 / 3.6)) < 0.5,
                $"{ProtocolFamilies.Name(family)} decoded {landed.SpeedMps} m/s for a device reporting 30 km/h.");
        });

    /// <summary>
    /// The GT06 login handshake, pinned against the one frame in these four formats that anybody
    /// can check.
    /// </summary>
    /// <remarks>
    /// <c>78 78 05 01 00 01 D9 DC 0D 0A</c> is the acknowledgement the GT06 documentation prints,
    /// and it is the only independently attestable fixed point in the family — everything else about
    /// these frames is this suite's reading of a layout. Asserting it in both directions is what
    /// stops a device and a decoder that are wrong in the same way from agreeing with each other:
    /// the left-hand side is built by <see cref="TrackerDevice"/>'s own framing and CRC, and the
    /// right-hand side is what tcp-adapter actually wrote back down the socket.
    /// </remarks>
    [Fact]
    public async Task The_GT06_login_is_acknowledged_with_the_documented_frame() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            byte[] documented = [0x78, 0x78, 0x05, 0x01, 0x00, 0x01, 0xD9, 0xDC, 0x0D, 0x0A];

            Assert.Equal(documented, TrackerDevice.Gt06Frame(0x01, [], serial: 1));

            var bus = await ArriveAsync(fleet, vehicles);

            var port = await fleet.TrackerPortAsync(ProtocolFamily.Gt06);
            Assert.True(port > 0);

            // ConnectAsync sends the login and waits for a reply; this asserts the bytes of it.
            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            var second = await device.ReportAsync(bus.Depot);
            await fleet.WaitForTelemetryAsync(bus.VehicleId, new ReportedFix(bus.Depot, second));

            // And the adapter does not acknowledge a location frame. The protocol does not ask for
            // one and some firmware drops the session on an unexpected reply.
            Assert.Empty(await device.ReceiveAsync(TimeSpan.FromSeconds(2)));
        });

    /// <summary>
    /// T-01/T-03 — an IMEI nobody bound is refused at connect, and nothing it says is published.
    /// </summary>
    /// <remarks>
    /// <b>Unresolvable means refused, not "allowed pending confirmation".</b> The topic a sample is
    /// published to is derived from the binding and there is no other authorisation on this path — a
    /// tracker cannot present a JWT, so EMQX's <c>verify_claims</c> and ACL, which is what confines
    /// an MQTT-native device, is simply unavailable here. The guarantee is structural: the only
    /// thing that produces a topic is a resolved vehicle id.
    /// </remarks>
    [Fact]
    public async Task An_unbound_IMEI_is_refused_and_publishes_nothing() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            // Fifteen digits, well formed, and bound to nothing.
            const string Stranger = "356938035643001";

            Assert.Null(await fleet.TrackerBindingAsync(Stranger));

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.H02, Stranger);

            await device.ReportAsync(bus.Depot);

            Assert.True(
                await device.WasClosedAsync(TimeSpan.FromSeconds(15)),
                "tcp-adapter left a socket open for a device it could not resolve.");

            // And the vehicle whose depot it claimed to be sitting at heard nothing.
            Assert.Equal(0, await fleet.TelemetryRowCountAsync(bus.VehicleId));
        });

    /// <summary>
    /// T-12 — a revoked tracker's socket is closed inside ADD §7.7.3's one second.
    /// </summary>
    /// <remarks>
    /// <b>A subscription, not a poll.</b> provisioning-svc publishes the credential signal on the
    /// <c>prov:tracker</c> Redis channel inside the transaction that revokes the binding, and
    /// tcp-adapter's watcher closes any socket holding that IMEI. The five-minute revalidation is
    /// the backstop for a signal that never arrives, not the mechanism — which is why the budget
    /// here is a second and not a sweep interval.
    /// </remarks>
    [Fact]
    public async Task A_revoked_tracker_is_closed_within_a_second() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            var reported = await device.ReportAsync(bus.Depot);
            await fleet.WaitForTelemetryAsync(bus.VehicleId, new ReportedFix(bus.Depot, reported));

            // Decommissioning is an Admin Portal action, not an operator's: an operator may bind a
            // device to their own vehicle, and taking a credential off the air permanently is
            // somebody else's decision.
            using (var revoked = await ModeAbFleet.DeleteAsync(
                fleet.ProvisioningClient, $"/v1/trackers/{bus.Imei}", await fleet.AdminBearerAsync()))
            {
                await ModeAbFleet.AssertSuccessAsync(revoked, $"revoking tracker {bus.Imei}");
            }

            var closed = await device.WasClosedAsync(TimeSpan.FromSeconds(5));

            Assert.True(closed, "tcp-adapter held a revoked tracker's socket open.");

            var binding = await fleet.TrackerBindingAsync(bus.Imei);

            Assert.NotNull(binding);
            Assert.Equal("REVOKED", binding!.Value.State);
        });

    /// <summary>
    /// T-04 and R-15 together — a device that loses its uplink is marked away, and the journey it
    /// was on is ended once the grace passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three services and a broker, in a row. The device half-closes its socket, which is what a
    /// tracker losing GSM actually does; tcp-adapter sees <c>ReadAsync</c> return zero and publishes
    /// the retained <c>veh/{id}/status = offline</c> that emulates an MQTT last will;
    /// trip-state-svc's subscription records <c>offline_since</c>; and the sweep decides afterwards.
    /// </para>
    /// <para>
    /// <b>A last will does not end a session; it starts a clock.</b> Ending on the first
    /// <c>offline</c> would close a journey every time a bus passes under a bridge, so the grace is
    /// what separates a tunnel from a device that has gone. Nothing in the platform pins the length
    /// of that grace — it is trip-state-svc's own two minutes — so this asserts the mark, then moves
    /// the clock the platform wrote, and lets the real sweep decide.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_tracker_that_loses_its_uplink_is_marked_away_and_its_journey_is_ended() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var started = await fleet.StartJourneyAsync(bus.Vehicle);

            var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            await using (device)
            {
                var reported = await device.ReportAsync(bus.Depot);
                await fleet.WaitForFixAsync(bus.VehicleId, started.SessionId, new ReportedFix(bus.Depot, reported));

                // The uplink drops. The broker's retained `offline` is published by the adapter,
                // which is what R-15's consumers see; nothing here publishes it.
                device.LoseUplink();
            }

            var away = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, started.SessionId, session => session.OfflineSince is not null,
                "the last will marking the vehicle away");

            // Marked, and still live: a tunnel is not the end of a journey.
            Assert.Equal("ACTIVE", away.State);

            await fleet.AgeOfflineGraceAsync(started.SessionId);

            var ended = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, started.SessionId, session => session.State == "COMPLETED",
                "the offline grace ending the journey");

            Assert.Equal("mqtt_offline", ended.EndReason);
            Assert.Equal("system", ended.EndedBy);

            // The retained value is what a subscriber joining afterwards reads, so the vehicle reads
            // dark to anything that connects after the fact — which is what takes it off the map.
            Assert.Equal(VehicleStatus.Offline, await fleet.RetainedStatusAsync(bus.VehicleId));
        });

    /// <summary>
    /// T-11 — a Mode A bus publishes whether or not a driver is online, and that is the point.
    /// </summary>
    /// <remarks>
    /// §7.7.7's mode gate requires <c>veh:driver:{vehicleId}</c> — dispatch-svc's standby binding —
    /// for Mode C only. US-3.22/3.23 make the tracker authoritative for Mode A and Mode B: "the
    /// mobile app is not needed". So this bus has no session, no driver on standby and no handset
    /// anywhere, and its position still reaches the platform.
    /// </remarks>
    [Fact]
    public async Task A_Mode_A_bus_publishes_with_no_driver_online_at_all() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            Assert.Null(await fleet.ActiveSessionAsync(bus.VehicleId));
            Assert.False(
                await fleet.Cache.KeyExistsAsync(RedisKeys.VehicleDriver(bus.VehicleId)),
                "no driver has gone online in an app for this vehicle");

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            var reported = await device.ReportAsync(bus.Depot);

            await fleet.WaitForTelemetryAsync(bus.VehicleId, new ReportedFix(bus.Depot, reported));

            Assert.True(await fleet.TelemetryRowCountAsync(bus.VehicleId) > 0);
        });

    /// <summary>
    /// T-08 — a second claim on a live IMEI quarantines both records and refuses the bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both, not the challenger.</b> Two devices presenting one identity is either a clone or a
    /// mis-keyed provisioning, and from the outside there is no way to tell which of the two is the
    /// genuine one — so provisioning-svc holds both and a person decides (US-3.4). Refusing only the
    /// second would leave a clone publishing under an ACTIVE binding.
    /// </para>
    /// <para>
    /// The 409 travels the Fleet Portal hop with the upstream's own code rather than becoming a
    /// generic 502: an operator has to see that the IMEI they typed is already on another vehicle,
    /// because what happens next is somebody walking out to look at two buses.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_second_claim_on_one_IMEI_quarantines_both_records() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            var second = await fleet.OnboardVehicleAsync(bus.Org, await fleet.CreateDriverAsync());
            vehicles.Add(second.VehicleId);

            using var refused = await ModeAbFleet.PostAsync(
                fleet.FleetClient,
                $"/v1/fleets/{bus.Org.FleetId}/trackers/bind",
                new { imei = bus.Imei, vehicleId = second.VehicleId.ToString(), autoStartSession = true },
                bus.Org.OwnerBearer);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var bindings = await fleet.TrackerBindingsAsync(bus.Imei);

            Assert.Equal(2, bindings.Count);
            Assert.All(bindings, binding => Assert.Equal("QUARANTINED", binding.State));
            Assert.Contains(bindings, binding => binding.VehicleId == bus.VehicleId);
            Assert.Contains(bindings, binding => binding.VehicleId == second.VehicleId);
        });
}
