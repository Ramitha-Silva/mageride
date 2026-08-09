using System.Net;
using MageRide.E2E.Infrastructure;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// The Mode A journey, end to end: Start and End Journey, the idle timer, the arrival fence and the
/// five-minute grace restart (D1' §B.8, US-5.1–5.4, US-5.9, US-5.10, D-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every clock in here is driven by a real tracker.</b> The bus reports through
/// <see cref="TrackerDevice"/> on tcp-adapter's GT06 socket, and the fix travels EMQX →
/// mqtt-bridge-svc → <c>telemetry.raw</c> → position-processor-svc → <c>telemetry.normalized</c> →
/// trip-state-svc's session consumer before it becomes the <c>last_movement_at</c> US-5.3 measures
/// or the <c>last_position_geo</c> US-5.4's fence is tested against. Nothing here writes a position.
/// </para>
/// <para>
/// <b>R-01 is the fence.</b> There is no ride-svc in this fleet and no <c>rides.*</c> row in any of
/// these assertions. A Mode A/B journey is a tracking session, trip-state-svc is its only writer,
/// and <see cref="A_Mode_C_journey_is_refused_and_told_where_it_belongs"/> asserts the service says
/// so out loud rather than quietly accepting one.
/// </para>
/// </remarks>
[Collection<ModeAbCollection>]
[Trait("Category", "ModeAB")]
public sealed class ModeAJourneyScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeAbScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>US-5.1 and US-5.2 — the two buttons, and what each leaves behind.</summary>
    [Fact]
    public async Task A_driver_starts_and_ends_a_journey() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            var started = await fleet.StartJourneyAsync(bus.Vehicle);

            Assert.Equal("ACTIVE", started.State);
            Assert.Equal("A", started.Mode);
            Assert.Equal(bus.VehicleId, started.VehicleId);

            // The driver app's cold-start read finds it, which is the only way an app that was
            // killed mid-journey gets back to the right screen.
            using (var active = await fleet.ReadActiveSessionAsync(bus.Vehicle))
            {
                await ModeAbFleet.AssertSuccessAsync(active, "reading the vehicle's active session");
                Assert.Equal(started.SessionId, (await ModeAbFleet.ReadJsonAsync(active)).GetProperty("sessionId").GetGuid());
            }

            var live = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("ACTIVE", live.State);
            Assert.Equal("driver", live.StartedBy);
            Assert.Equal(bus.Vehicle.Driver.DriverId, live.DriverId);

            using (var ended = await fleet.EndJourneyAsync(bus.Vehicle, started.SessionId))
            {
                await ModeAbFleet.AssertSuccessAsync(ended, "End Journey");
                Assert.Equal("ENDED", (await ModeAbFleet.ReadJsonAsync(ended)).GetProperty("state").GetString());
            }

            var closed = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("COMPLETED", closed.State);
            Assert.Equal("driver_ended", closed.EndReason);
            Assert.Equal("driver", closed.EndedBy);

            // The vehicle is idle again — which is what takes it off the live map, because
            // fanout-svc and persistence-writer-svc both learn it from `session.ended`.
            //
            // 200 with nothing in it, not 404: the contract reserves 404 for a vehicle the caller
            // may not see, and answering it for an idle one would make "no journey" and "not yours"
            // indistinguishable to the driver's own app at cold start.
            using (var afterwards = await fleet.ReadActiveSessionAsync(bus.Vehicle))
            {
                await ModeAbFleet.AssertSuccessAsync(afterwards, "reading the vehicle's active session");

                var body = (await afterwards.Content.ReadAsStringAsync()).Trim();

                Assert.True(
                    body.Length == 0 || string.Equals(body, "null", StringComparison.Ordinal),
                    $"The vehicle still has a live session after End Journey: {body}");
            }

            // Both halves of R-13: the domain log a support engineer reads, and the events the rest
            // of the platform is built on. `trips.outbox` is keyed by the *vehicle*, so an end and
            // the start after it arrive on `trip.events` in that order.
            Assert.Equal(["session.started", "session.ended"], await fleet.TripEventKindsAsync(started.SessionId));
            Assert.Equal(["session.started", "session.ended"], await fleet.TripOutboxAsync(bus.VehicleId));

            // A driver who pressed End meant it. Offering to undo a deliberate End would make the
            // button ambiguous, so only an *auto*-ended session is restartable.
            using var refused = await fleet.RestartJourneyAsync(bus.Vehicle, started.SessionId);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        });

    /// <summary>US-5.3 — thirty minutes without movement ends the journey, and a parked bus is not moving.</summary>
    /// <remarks>
    /// The distinction the scenario turns on is D5's: <b>reporting is not moving</b>. The tracker
    /// keeps publishing from a standstill for the whole of the wait, exactly as a bus at a terminus
    /// does, and every one of those fixes advances <c>last_position_at</c> and none of them advances
    /// <c>last_movement_at</c>. If they did, US-5.3's timer would be unreachable — which is the
    /// failure the rule exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_bus_that_stops_moving_is_ended_by_the_idle_timer() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var started = await fleet.StartJourneyAsync(bus.Vehicle);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            // The bus pulls out of the depot: four fixes, each a real GT06 frame, each crossing the
            // whole hot path. The last one is what the idle clock is measured from — so the scenario
            // waits for exactly that one before it takes its baseline.
            var pullOut = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 160));
            var moving = await fleet.WaitForFixAsync(bus.VehicleId, started.SessionId, pullOut);

            Assert.Equal(pullOut.CapturedAt, moving.LastMovementAt);

            // Now it stands still and keeps reporting. Ten frames is well past the point at which a
            // consumer that counted a fix as activity would have moved the clock.
            var standing = pullOut;

            for (var frame = 0; frame < 10; frame++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);
                standing = new ReportedFix(pullOut.At, await device.ReportAsync(pullOut.At, speedKph: 0));
            }

            var parked = await fleet.WaitForFixAsync(bus.VehicleId, started.SessionId, standing);

            // Ten more fixes have landed and the idle clock has not moved: reporting is not moving.
            Assert.Equal("ACTIVE", parked.State);
            Assert.Equal(pullOut.CapturedAt, parked.LastMovementAt);
            Assert.True(parked.LastPositionAt > parked.LastMovementAt);

            // The window is the URD's, read off the running service; only then is the clock moved.
            await fleet.AgeIdleClockAsync(started.SessionId);

            var ended = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, started.SessionId, session => session.State == "COMPLETED",
                "the idle timer ending the journey");

            Assert.Equal("idle_timeout", ended.EndReason);
            Assert.Equal("system", ended.EndedBy);
            Assert.True(ended.WasAutoEnded);
        });

    /// <summary>US-5.4 — a hundred metres from where the last journey finished ends this one.</summary>
    /// <remarks>
    /// <para>
    /// <b>The bus has to move between the two journeys, and that is not a contrivance.</b>
    /// <c>end_geo</c> is copied from the session's last position when it closes, so the fence's
    /// centre is always exactly where the vehicle is standing the moment the previous journey ends —
    /// which means a fenced journey started from there would be "arrived" on its first fix. US-5.4
    /// exists for the driver who <em>forgot</em> to press End, and a tracker publishes whether or
    /// not the app holds a session (US-3.22), so the bus deadheads back to the depot with nothing
    /// live. Those fixes cross the same hot path and are dropped by trip-state-svc for want of a
    /// session; the scenario waits for the last of them to reach Timescale before starting the
    /// journey that has the fence.
    /// </para>
    /// <para>
    /// The half that makes the assertion mean anything is the one in the middle: the bus is held
    /// 200 m out, reporting, through several sweep passes, and the session stays ACTIVE. Without it
    /// this would pass just as well against a fence that fired on any fix at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_bus_that_arrives_where_it_last_finished_is_ended_by_the_fence() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var terminus = ModeAbFleet.MetresNorth(bus.Depot, 200);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            // Journey one: depot to terminus, then End Journey at the terminus.
            var outbound = await fleet.StartJourneyAsync(bus.Vehicle);
            var arrival = await DriveAsync(device, bus.Depot, terminus);

            // Every fix, not just the first: a frame still in flight when this journey ends lands on
            // whatever session is live when it arrives.
            await fleet.WaitForFixAsync(bus.VehicleId, outbound.SessionId, arrival);

            using (var ended = await fleet.EndJourneyAsync(bus.Vehicle, outbound.SessionId))
            {
                await ModeAbFleet.AssertSuccessAsync(ended, "ending the outbound journey");
            }

            // The deadhead: back to the depot with no session live. Drained through Timescale before
            // anything else happens, because the first of these fixes is inside the fence about to
            // be armed — and if the drain were incomplete the next assertion fails loudly rather
            // than the test passing for the wrong reason.
            var backAtDepot = await DriveAsync(device, terminus, bus.Depot);
            await fleet.WaitForTelemetryAsync(bus.VehicleId, backAtDepot);

            Assert.Null(await fleet.ActiveSessionAsync(bus.VehicleId));

            // Journey two, with the fence asked for. The bus is 200 m short of the terminus.
            var inbound = await fleet.StartJourneyAsync(bus.Vehicle, autoEndAtDestination: true);

            var armed = await fleet.ReadSessionAsync(inbound.SessionId);

            Assert.True(armed.AutoEndAtDestination);
            Assert.True(armed.DestinationArmed);

            var shortOf = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 60));
            await fleet.WaitForFixAsync(bus.VehicleId, inbound.SessionId, shortOf);

            // A hundred and forty metres out, reporting, across several sweep passes: the fence is
            // armed and does not fire. US-5.4's radius is a hundred metres and this is not inside it.
            Assert.Equal(100, fleet.Windows.GeofenceRadiusM);

            for (var frame = 0; frame < 5; frame++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);
                shortOf = new ReportedFix(shortOf.At, await device.ReportAsync(shortOf.At, speedKph: 0));
            }

            await fleet.WaitForFixAsync(bus.VehicleId, inbound.SessionId, shortOf);

            Assert.Equal("ACTIVE", (await fleet.ReadSessionAsync(inbound.SessionId)).State);

            // And now it arrives — 35 m from where the last journey ended, which is inside the fence.
            await DriveAsync(device, shortOf.At, ModeAbFleet.MetresNorth(terminus, -35));

            var arrived = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, inbound.SessionId, session => session.State == "COMPLETED",
                "the arrival fence ending the journey");

            Assert.Equal("destination_geofence", arrived.EndReason);
            Assert.Equal("system", arrived.EndedBy);
        });

    /// <summary>US-5.4's precondition — a vehicle's first journey has nowhere to arrive at.</summary>
    /// <remarks>
    /// The radius is centred on "the previous journey's end position", so a vehicle that has never
    /// finished one arms nothing. Both readings of the alternative are wrong: an unarmed fence that
    /// is treated as armed either never fires, or fires on the first fix and ends the journey the
    /// driver has just started. This asserts the platform takes the first reading and says so — the
    /// driver asked for the fence, and the session carries the request with no centre behind it.
    /// </remarks>
    [Fact]
    public async Task A_vehicles_first_journey_arms_no_fence() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            var first = await fleet.StartJourneyAsync(bus.Vehicle, autoEndAtDestination: true);
            var session = await fleet.ReadSessionAsync(first.SessionId);

            Assert.True(session.AutoEndAtDestination, "the driver asked for the fence");
            Assert.False(session.DestinationArmed, "and there was no previous journey to centre it on");

            // Fixes arrive, sweeps run, and nothing ends the journey.
            var moving = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 120));
            await fleet.WaitForFixAsync(bus.VehicleId, first.SessionId, moving);

            for (var frame = 0; frame < 4; frame++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);
                moving = new ReportedFix(moving.At, await device.ReportAsync(moving.At, speedKph: 0));
            }

            await fleet.WaitForFixAsync(bus.VehicleId, first.SessionId, moving);

            Assert.Equal("ACTIVE", (await fleet.ReadSessionAsync(first.SessionId)).State);
        });

    /// <summary>US-5.10 — the five-minute grace, both halves of it.</summary>
    /// <remarks>
    /// <b>The restart is in place.</b> The passengers watching hold the session id, so a resumed
    /// journey keeps it — and keeps <c>started_at</c>, because a driver who took a wrong turn and
    /// stopped for six minutes has not started a second journey. A new row would break "the driver's
    /// current session" for everything that cached the old one.
    /// </remarks>
    [Fact]
    public async Task An_auto_ended_journey_can_be_taken_back_inside_the_grace_and_not_after_it() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);
            var started = await fleet.StartJourneyAsync(bus.Vehicle);

            await using var device = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, bus.Imei);

            var pullOut = await DriveAsync(device, bus.Depot, ModeAbFleet.MetresNorth(bus.Depot, 80));
            await fleet.WaitForFixAsync(bus.VehicleId, started.SessionId, pullOut);

            await fleet.AgeIdleClockAsync(started.SessionId);

            var autoEnded = await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, started.SessionId, session => session.State == "COMPLETED",
                "the idle timer ending the journey");

            Assert.Equal("idle_timeout", autoEnded.EndReason);
            Assert.Equal(TimeSpan.FromMinutes(5), fleet.Windows.RestartGrace);

            using (var resumed = await fleet.RestartJourneyAsync(bus.Vehicle, started.SessionId))
            {
                await ModeAbFleet.AssertSuccessAsync(resumed, "restarting inside the grace window");

                var body = await ModeAbFleet.ReadJsonAsync(resumed);

                Assert.Equal(started.SessionId, body.GetProperty("sessionId").GetGuid());
                Assert.Equal("ACTIVE", body.GetProperty("state").GetString());
            }

            var live = await fleet.ReadSessionAsync(started.SessionId);

            Assert.Equal("ACTIVE", live.State);
            Assert.Null(live.EndReason);
            Assert.Null(live.EndedAt);
            Assert.Equal(autoEnded.StartedAt, live.StartedAt);

            Assert.Equal(
                ["session.started", "session.ended", "session.restarted"],
                await fleet.TripEventKindsAsync(started.SessionId));

            // Now let it be auto-ended again and let the window close. 410 Gone rather than 409:
            // the request was well formed and would have worked a minute ago, which is what Gone
            // means and what the contract lists.
            await fleet.AgeIdleClockAsync(started.SessionId);

            await fleet.WaitForSessionByIdAsync(
                bus.VehicleId, started.SessionId, session => session.State == "COMPLETED",
                "the idle timer ending the resumed journey");

            await fleet.AgeRestartGraceAsync(started.SessionId);

            using var expired = await fleet.RestartJourneyAsync(bus.Vehicle, started.SessionId);

            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        });

    /// <summary>D-03 — one live session per driver, settled by the index rather than by a read.</summary>
    /// <remarks>
    /// A relief driver taking a second bus out without ending the first is the ordinary way this
    /// happens, and the answer has to be the same when ten requests arrive at once — which is why
    /// the platform does not pre-check: it inserts, and turns the unique violation into the refusal.
    /// </remarks>
    [Fact]
    public async Task A_driver_holds_one_live_session_at_a_time() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var org = await fleet.CreateApprovedOrgAsync();
            var driver = await fleet.CreateDriverAsync();

            var first = await fleet.OnboardVehicleAsync(org, driver);
            var second = await fleet.OnboardVehicleAsync(org, driver);

            vehicles.Add(first.VehicleId);
            vehicles.Add(second.VehicleId);

            var live = await fleet.StartJourneyAsync(first);

            using (var refused = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = second.VehicleId.ToString(), mode = "A" },
                driver.Bearer))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal("driver-already-live", await ModeAbFleet.ProblemCodeAsync(refused));
            }

            Assert.Null(await fleet.ActiveSessionAsync(second.VehicleId));

            // And the second bus is available the moment the first journey is over.
            using (var ended = await fleet.EndJourneyAsync(first, live.SessionId))
            {
                await ModeAbFleet.AssertSuccessAsync(ended, "End Journey");
            }

            var switched = await fleet.StartJourneyAsync(second);

            Assert.Equal(second.VehicleId, switched.VehicleId);
        });

    /// <summary>R-01, said out loud: a Mode C journey is a ride, and this service refuses to hold one.</summary>
    /// <remarks>
    /// Named in the refusal rather than lumped into "unknown mode", because a client that sent
    /// <c>C</c> is not malformed — it is talking to the wrong service, and the fence between
    /// ride-svc and trip-state-svc is worth spelling out at the boundary where it is crossed.
    /// </remarks>
    [Fact]
    public async Task A_Mode_C_journey_is_refused_and_told_where_it_belongs() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var bus = await ArriveAsync(fleet, vehicles);

            using var refused = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = bus.VehicleId.ToString(), mode = "C" },
                bus.Vehicle.Driver.Bearer);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            var problem = await refused.Content.ReadAsStringAsync();

            Assert.Contains("ride", problem, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("R-01", problem, StringComparison.Ordinal);

            Assert.Null(await fleet.ActiveSessionAsync(bus.VehicleId));
        });
}
