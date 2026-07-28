using System.Net;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using MageRide.TripState.Domain;
using MageRide.TripState.Sessions;
using MageRide.TripState.Tests.Infrastructure;

namespace MageRide.TripState.Tests.Integration;

/// <summary>
/// The three durable timers and the grace restart: US-5.3, US-5.4, US-5.9, US-5.10, R-15/T-04.
/// </summary>
[Collection<TripStateCollection>]
public sealed class AutoEndTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>Colombo Fort, and a point 3 km away — well outside any fence under test.</summary>
    private static readonly GeoPoint ColomboFort = new(6.9355, 79.8487);
    private static readonly GeoPoint Maradana = new(6.9294, 79.8756);

    /// <summary>The DoD assertion: idle at 30 minutes, then restartable inside 5.</summary>
    [Fact]
    public async Task An_idle_session_auto_ends_and_can_be_restarted_inside_the_grace_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        // Nothing is due yet — a sweep that ended a live session would be worse than one that
        // never ran.
        Assert.Equal(0, (await harness.SweepAsync()).Total);

        await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(31));

        var swept = await harness.SweepAsync();
        Assert.Equal(1, swept.Idle);

        var session = Assert.Single(await harness.SessionsAsync(vehicleId));
        Assert.Equal(SessionStates.Completed, session.State);
        Assert.Equal(EndReasons.IdleTimeout, session.EndReason);
        Assert.Equal(SessionActors.System, session.EndedBy);

        // The mutex is free and the published fact is gone, so the driver could start something
        // else — which is exactly what makes the restart worth having rather than automatic.
        Assert.Equal(0, await harness.ActiveSessionCountAsync(driverId));
        Assert.Null(await harness.PublishedSessionAsync(driverId));

        // US-5.9's push needs the reason and the deadline; both are on the event.
        var ended = Assert.Single(
            await harness.OutboxAsync(vehicleId), e => e.EventType == SessionEventTypes.SessionEnded);

        Assert.Contains(EndReasons.IdleTimeout, ended.Payload, StringComparison.Ordinal);
        Assert.Contains("restartableUntil", ended.Payload, StringComparison.Ordinal);

        // US-5.10.
        var restarted = await harness.PostAsync($"/v1/sessions/{sessionId}/restart", null, bearer);
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);

        var body = await TripStateHarness.ReadJsonAsync(restarted);
        Assert.Equal(SessionViews.Active, body.GetProperty("state").GetString());
        Assert.Equal(sessionId.ToString(), body.GetProperty("sessionId").GetString());

        // In place, not a new row: the passengers watching it hold this id.
        Assert.Single(await harness.SessionsAsync(vehicleId));
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
        Assert.Equal(sessionId.ToString(), await harness.PublishedSessionAsync(driverId));
    }

    /// <summary>
    /// Past the grace it is 410 Gone, not 409: the request was well formed and would have worked a
    /// minute ago.
    /// </summary>
    [Fact]
    public async Task A_restart_after_the_grace_window_is_410_gone()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(31));
        await harness.SweepAsync();

        // Six minutes past the auto-end, one past the window.
        await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(6));

        var response = await harness.PostAsync($"/v1/sessions/{sessionId}/restart", null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Gone, "session-restart-expired");
        Assert.Equal(0, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>
    /// A driver who pressed End Journey meant it. Offering to undo it would make the button
    /// ambiguous, so only an <i>auto</i>-ended session is restartable at all.
    /// </summary>
    [Fact]
    public async Task A_driver_ended_session_is_not_restartable_even_immediately()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var ended = await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer);
        var body = await TripStateHarness.ReadJsonAsync(ended);

        // The contract's `restartableUntil` is absent on a driver-ended session, which is how the
        // dashboard knows not to offer the button.
        Assert.False(body.TryGetProperty("restartableUntil", out _));

        var response = await harness.PostAsync($"/v1/sessions/{sessionId}/restart", null, bearer);
        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "conflict");
    }

    /// <summary>
    /// A restart re-takes the mutex. A driver who started something else during the grace window
    /// cannot have two.
    /// </summary>
    [Fact]
    public async Task A_restart_is_refused_when_the_driver_has_gone_live_elsewhere()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var first = await harness.CreateVehicleAsync(driverId);
        var second = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, first);
        var sessionId = started.GetProperty("sessionId").GetString();

        await harness.AgeSessionAsync(Guid.Parse(sessionId!), TimeSpan.FromMinutes(31));
        await harness.SweepAsync();

        await harness.StartAsync(bearer, second);

        var response = await harness.PostAsync($"/v1/sessions/{sessionId}/restart", null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "driver-already-live");
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>
    /// US-5.3's whole point: a parked bus keeps reporting fixes, and those are not activity.
    /// </summary>
    [Fact]
    public async Task Reporting_from_a_standstill_does_not_hold_the_idle_timer_open()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        // The first fix is movement by definition — the vehicle has only just gone live.
        Assert.True(await harness.ReportPositionAsync(vehicleId, ColomboFort));

        await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(31));

        // Now it reports again from a few metres away, as GNSS drift on a parked vehicle does.
        // That must not wind the idle clock forward.
        Assert.False(await harness.ReportPositionAsync(
            vehicleId, new GeoPoint(ColomboFort.Latitude + 0.0001, ColomboFort.Longitude), speedMps: 0));

        Assert.Equal(1, (await harness.SweepAsync()).Idle);
    }

    /// <summary>...and a bus that is actually driving keeps its session.</summary>
    [Fact]
    public async Task A_moving_vehicle_holds_its_session_open()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        await harness.ReportPositionAsync(vehicleId, ColomboFort);
        await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(31));

        // 3 km down the road: unambiguously movement, on both the speed and the distance signal.
        Assert.True(await harness.ReportPositionAsync(vehicleId, Maradana, speedMps: 8));

        Assert.Equal(0, (await harness.SweepAsync()).Total);
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>US-5.4: arriving within 100 m of the previous journey's end closes the session.</summary>
    [Fact]
    public async Task Arriving_at_the_destination_auto_ends_the_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        // A previous journey that finished at Colombo Fort — the fence has to be centred on
        // something, and US-5.4 says it is where the last one ended.
        var previous = await harness.StartAsync(bearer, vehicleId);
        var previousId = Guid.Parse(previous.GetProperty("sessionId").GetString()!);

        // Its last fix is what End copies into end_geo — the fence centre is produced by the
        // mechanism under test rather than written into the column behind its back.
        await harness.ReportPositionAsync(vehicleId, ColomboFort, speedMps: 0);
        await harness.PostAsync($"/v1/sessions/{previousId}/end", null, bearer);

        var started = await harness.StartAsync(bearer, vehicleId, autoEndAtDestination: true);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        // Still 3 km out.
        await harness.ReportPositionAsync(vehicleId, Maradana, speedMps: 8);
        Assert.Equal(0, (await harness.SweepAsync()).Arrived);

        // Now 40 m from the fence centre — inside the 100 m radius.
        await harness.ReportPositionAsync(
            vehicleId, new GeoPoint(ColomboFort.Latitude + 0.00035, ColomboFort.Longitude), speedMps: 2);

        Assert.Equal(1, (await harness.SweepAsync()).Arrived);

        var sessions = await harness.SessionsAsync(vehicleId);
        Assert.Equal(EndReasons.DestinationGeofence, sessions[0].EndReason);
        Assert.Equal(sessionId.ToString(), started.GetProperty("sessionId").GetString());
    }

    /// <summary>
    /// A first journey has nowhere to arrive at, so the fence is not armed — an empty fence would
    /// either never fire or fire on the first fix.
    /// </summary>
    [Fact]
    public async Task A_first_journey_arms_no_fence()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId, autoEndAtDestination: true);
        await harness.ReportPositionAsync(vehicleId, ColomboFort, speedMps: 0);

        Assert.Equal(0, (await harness.SweepAsync()).Arrived);
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>
    /// The auto-end route rejects <c>driver_ended</c>: a timer must never overwrite the reason
    /// that decides whether the grace window opens.
    /// </summary>
    [Fact]
    public async Task The_internal_auto_end_refuses_a_driver_ended_reason()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostInternalAsync(
            $"/v1/internal/sessions/{sessionId}/auto-end", new { reason = EndReasons.DriverEnded });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>A timer that fires against an already-closed session loses, and says so.</summary>
    [Fact]
    public async Task A_timer_that_loses_the_race_to_the_driver_does_not_overwrite_the_reason()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/sessions/{sessionId}/auto-end", new { reason = EndReasons.IdleTimeout });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "conflict");

        // Still driver_ended, so the dashboard does not start offering a restart button.
        Assert.Equal(EndReasons.DriverEnded, (await harness.SessionsAsync(vehicleId))[0].EndReason);
    }

    /// <summary>The internal family is service-to-service only.</summary>
    [Fact]
    public async Task The_internal_routes_refuse_a_caller_without_the_secret()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/sessions/{Guid.NewGuid()}/auto-end",
            new { reason = EndReasons.IdleTimeout },
            apiKey: null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }
}
