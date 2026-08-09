using MageRide.E2E.Infrastructure;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// AL-32 — a tracker-equipped Mode A/B vehicle starts and ends its journey on the ignition key, and
/// the dashboard overrides the device in both directions (US-3.22, US-3.23, D6' §I-25.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The ACC line is decoded from a real GT06 status frame.</b> tcp-adapter reads bit 1 of the
/// terminal-information byte, notices the <em>transition</em>, and calls trip-state-svc's
/// <c>POST /v1/internal/sessions/ignition</c> — a route that had no caller at all until C043 landed
/// one, and which nothing else on the platform can reach. So the whole of this file is a socket at
/// one end and a session at the other.
/// </para>
/// <para>
/// <b>Symmetry is the subject, not a detail.</b> A dashboard End closes a device-started session and
/// records that the device was overridden; an ACC-off leaves a <em>dashboard</em>-started session
/// alone, because a driver waiting at a depot with the engine off has said what they want. The
/// device is never authoritative in either direction, and a platform that got one half right and
/// the other wrong would look correct in casual use and strand a driver in the case that matters.
/// </para>
/// </remarks>
[Collection<ModeAbCollection>]
[Trait("Category", "ModeAB")]
public sealed class IgnitionScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeAbScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>US-3.22/3.23 — the key starts the journey, and the key ends it.</summary>
    /// <remarks>
    /// The session is attributed to the vehicle's <b>owner</b>. A tracker knows its vehicle and
    /// nothing else, so that is the only person it can name — and getting it wrong would take some
    /// other driver's D-03 mutex and block the journey they were trying to start themselves.
    /// </remarks>
    [Fact]
    public async Task Turning_the_key_starts_a_journey_and_turning_it_off_ends_it() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            Assert.Null(await fleet.ActiveSessionAsync(bus.VehicleId));

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            await device.ReportIgnitionAsync(on: true);

            var live = await fleet.WaitForSessionAsync(
                bus.VehicleId, session => session.State == "ACTIVE", "ignition-on opening a session");

            Assert.Equal("device", live.StartedBy);
            Assert.Equal("A", live.Mode);
            Assert.Equal(bus.Org.OwnerId, live.DriverId);

            // The bus runs its route on the same socket, and the fixes land on the session the key
            // opened.
            var arrival = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 120));
            await fleet.WaitForFixAsync(bus.VehicleId, live.Id, arrival);

            await device.ReportIgnitionAsync(on: false);

            var ended = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, live.Id, session => session.State == "COMPLETED", "ignition-off ending it");

            Assert.Equal("ignition_off", ended.EndReason);
            Assert.Equal("device", ended.EndedBy);

            // `ignition_off` is one of the reasons neither D4' nor the contract had — AL-32 needed
            // it and migration 0504 widened the CHECK for it — and it is an automatic end, so the
            // driver may take the journey back inside the grace.
            Assert.True(ended.WasAutoEnded);

            Assert.Contains("ignition", await fleet.TripEventKindsAsync(live.Id));
            Assert.Equal(["session.started", "session.ended"], await fleet.TripOutboxAsync(bus.VehicleId));
        });

    /// <summary>AL-32's first half — the dashboard closes what the device opened, and it is recorded.</summary>
    /// <remarks>
    /// A tracker that is still publishing does not get a veto. It gets a cadence hint telling it to
    /// stand down, and its next position arrives on a vehicle with no session — which is the only
    /// behaviour that lets a driver end a journey on a bus whose tracker is mis-reporting ignition.
    /// The override is written to <c>trips.events</c> because "the device started this and a person
    /// overruled it" is exactly what a support engineer is looking for six weeks later.
    /// </remarks>
    [Fact]
    public async Task A_dashboard_end_overrides_a_device_started_journey() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            await device.ReportIgnitionAsync(on: true);

            var live = await fleet.WaitForSessionAsync(
                bus.VehicleId, session => session.State == "ACTIVE", "ignition-on opening a session");

            Assert.Equal("device", live.StartedBy);

            // The owner presses End Journey with the engine still running — the session is theirs,
            // because the device could name nobody else.
            using (var ended = await fleet.EndJourneyAsync(bus.Org.OwnerBearer, live.Id))
            {
                await ModeAbFleet.AssertSuccessAsync(ended, "the dashboard ending a device-started journey");
            }

            var closed = await fleet.ReadSessionAsync(live.Id);

            Assert.Equal("COMPLETED", closed.State);
            Assert.Equal("driver_ended", closed.EndReason);
            Assert.Equal("driver", closed.EndedBy);

            // Not restartable: this was a person's decision, whatever the key is doing.
            Assert.False(closed.WasAutoEnded);
            Assert.Contains("device.overridden", await fleet.TripEventKindsAsync(live.Id));

            // And the engine is still on. A second ACC-on heartbeat is not a transition and opens
            // nothing — the device has already told the platform the key is turned.
            await device.ReportIgnitionAsync(on: true);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Null(await fleet.ActiveSessionAsync(bus.VehicleId));
        });

    /// <summary>AL-32's second half — an ACC-off does not close a journey a person started.</summary>
    /// <remarks>
    /// <para>
    /// A driver waiting at a depot with the engine off, or one on a bus whose tracker reports
    /// ignition wrongly, has said what they want by pressing Start. The report is recorded and
    /// ignored, which is a different thing from being dropped: <c>trips.events</c> carries it so the
    /// next person to ask why a journey did not end has an answer.
    /// </para>
    /// <para>
    /// <b>The engine is started first, and it has to be.</b> tcp-adapter reports a <em>transition</em>
    /// rather than a level, and it deliberately does not report the first ACC-off of a socket — that
    /// is the state the device was already in, and reporting it would auto-end a session the
    /// dashboard had just started, which is the very thing AL-32 forbids. So this drives the real
    /// sequence: key on (already live, nothing happens), then key off (a transition, and ignored).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ignition_off_leaves_a_dashboard_started_journey_alone() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var started = await fleet.StartJourneyAsync(bus.Vehicle);

            var byDriver = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("driver", byDriver.StartedBy);
            Assert.Equal(bus.Vehicle.Driver.DriverId, byDriver.DriverId);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            // The key turns while the dashboard already has a journey open: the vehicle is live, so
            // there is nothing to start and the report is logged against the session that exists.
            await device.ReportIgnitionAsync(on: true);

            await fleet.UntilAsync(
                bus.VehicleId,
                async () => (await fleet.TripEventKindsAsync(started.SessionId)).Count(kind => kind == "ignition") == 1,
                "the ignition-on report reaching the session's domain log");

            Assert.Equal("ACTIVE", (await fleet.ReadSessionAsync(started.SessionId)).State);

            // And now the key turns off. The dashboard wins.
            await device.ReportIgnitionAsync(on: false);

            await fleet.UntilAsync(
                bus.VehicleId,
                async () => (await fleet.TripEventKindsAsync(started.SessionId)).Count(kind => kind == "ignition") == 2,
                "the ignition-off report reaching the session's domain log");

            var still = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("ACTIVE", still.State);
            Assert.Null(still.EndReason);
            Assert.Equal("driver", still.StartedBy);

            // The journey ends when the person says so.
            using var ended = await fleet.EndJourneyAsync(bus.Vehicle, started.SessionId);

            await ModeAbFleet.AssertSuccessAsync(ended, "End Journey");

            var closed = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("driver_ended", closed.EndReason);

            // No override was recorded: this session was the driver's from the start, so there was
            // nothing for the dashboard to overrule.
            Assert.DoesNotContain("device.overridden", await fleet.TripEventKindsAsync(started.SessionId));
        });

    /// <summary>
    /// Ignition declines rather than guesses — a vehicle nobody has approved starts nothing.
    /// </summary>
    /// <remarks>
    /// A session opened on an unapproved vehicle would put a bus on the live map that a Verification
    /// Officer has not passed, and would take its owner's D-03 mutex for a journey they cannot
    /// legally make. The report is answered <c>202 declined</c> — a fact about a device, not a
    /// failure the adapter should retry — and the frame that carried it is still decoded and still
    /// published, because a position and an eligibility decision are different questions.
    /// </remarks>
    [Fact]
    public async Task Ignition_on_an_unapproved_vehicle_is_declined() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var org = await fleet.CreateApprovedOrgAsync();
            var driver = await fleet.CreateDriverAsync();

            // Onboarded and assigned, and still PENDING — no officer has decided it (AL-50).
            var vehicle = await fleet.OnboardVehicleAsync(org, driver, approve: false);
            vehicles.Add(vehicle.VehicleId);

            var imei = await fleet.BindTrackerAsync(org, vehicle);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, imei);

            await device.ReportIgnitionAsync(on: true);
            await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

            Assert.Null(await fleet.ActiveSessionAsync(vehicle.VehicleId));
            Assert.Empty(await fleet.SessionsOfAsync(vehicle.VehicleId));
        });
}
