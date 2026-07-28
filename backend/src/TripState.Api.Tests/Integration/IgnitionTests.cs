using System.Net;
using MageRide.TestKit;
using MageRide.TripState.Domain;
using MageRide.TripState.Endpoints;
using MageRide.TripState.Sessions;
using MageRide.TripState.Tests.Infrastructure;

namespace MageRide.TripState.Tests.Integration;

/// <summary>
/// AL-32 / US-3.22 / US-3.23 / US-5.12: a tracker-equipped Mode A/B vehicle auto-starts and
/// auto-ends on ignition, and the dashboard overrides the device in both directions.
/// </summary>
[Collection<TripStateCollection>]
public sealed class IgnitionTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>The DoD assertion: ACC on starts the journey, and the dashboard shows it started.</summary>
    [Fact]
    public async Task Ignition_on_auto_starts_a_session_and_the_dashboard_reads_it_as_started()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        // No app involved — US-3.22 is explicit that "the mobile app is not needed".
        var reported = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        Assert.Equal(
            "started",
            (await TripStateHarness.ReadJsonAsync(reported)).GetProperty("outcome").GetString());

        var session = Assert.Single(await harness.SessionsAsync(vehicleId));
        Assert.Equal(SessionStates.Active, session.State);

        // AL-32: the row says the device did it, so a support timeline can tell an ignition
        // auto-start from a driver's tap.
        Assert.Equal(SessionActors.Device, session.StartedBy);

        // US-5.12: the driver opens the app afterwards and the dashboard reads "journey started".
        var active = await harness.GetAsync(
            $"/v1/sessions/{vehicleId}/active", harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        var body = await TripStateHarness.ReadJsonAsync(active);
        Assert.Equal(SessionViews.Active, body.GetProperty("state").GetString());
        Assert.Equal(driverId.ToString(), body.GetProperty("driverId").GetString());

        Assert.Contains(
            SessionEventTypes.SessionStarted,
            (await harness.OutboxAsync(vehicleId)).Select(e => e.EventType));
    }

    /// <summary>The DoD's fourth item: End Journey wins while the device is still publishing.</summary>
    [Fact]
    public async Task A_dashboard_end_closes_a_device_started_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        var active = await TripStateHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/sessions/{vehicleId}/active", bearer));

        var sessionId = Guid.Parse(active.GetProperty("sessionId").GetString()!);

        var ended = await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);

        var session = Assert.Single(await harness.SessionsAsync(vehicleId));
        Assert.Equal(SessionStates.Completed, session.State);
        Assert.Equal(EndReasons.DriverEnded, session.EndReason);
        Assert.Equal(SessionActors.Driver, session.EndedBy);

        // The override is recorded, because "the driver overruled the tracker" is the thing a
        // support engineer will be looking for six weeks later.
        Assert.Contains(TripEventKinds.DeviceOverridden, await harness.TripEventKindsAsync(sessionId));

        // And the device carrying on is not a veto: its next fix simply lands on a vehicle with no
        // session, which the position consumer drops.
        Assert.False(await harness.ReportPositionAsync(vehicleId, new MageRide.Shared.Primitives.GeoPoint(6.93, 79.85)));
        Assert.Equal(0, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>ACC off ends what ACC on started.</summary>
    [Fact]
    public async Task Ignition_off_ends_a_device_started_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.B);

        await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        var off = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOff });

        Assert.Equal("ended", (await TripStateHarness.ReadJsonAsync(off)).GetProperty("outcome").GetString());

        var session = Assert.Single(await harness.SessionsAsync(vehicleId));
        Assert.Equal(EndReasons.IgnitionOff, session.EndReason);
        Assert.Equal(SessionActors.Device, session.EndedBy);

        // Auto-ended, so the driver gets the US-5.10 grace — the tracker may have mis-reported.
        var ended = Assert.Single(
            await harness.OutboxAsync(vehicleId), e => e.EventType == SessionEventTypes.SessionEnded);

        Assert.Contains("restartableUntil", ended.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// AL-32 in the other direction: ACC off must <b>not</b> end a session the driver started from
    /// the dashboard. A driver waiting at a depot with the engine off has said what they want.
    /// </summary>
    [Fact]
    public async Task Ignition_off_leaves_a_dashboard_started_session_alone()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        var off = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOff });

        Assert.Equal("nochange", (await TripStateHarness.ReadJsonAsync(off)).GetProperty("outcome").GetString());
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));

        // It is recorded even though it changed nothing — the log is what explains a support
        // ticket that says "my journey did not end when I switched off".
        Assert.Contains(TripEventKinds.Ignition, await harness.TripEventKindsAsync(sessionId));
    }

    /// <summary>ACC on for a vehicle that is already live is a no-op, not a second session.</summary>
    [Fact]
    public async Task Ignition_on_over_a_live_session_changes_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);

        var on = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        Assert.Equal("nochange", (await TripStateHarness.ReadJsonAsync(on)).GetProperty("outcome").GetString());

        var session = Assert.Single(await harness.SessionsAsync(vehicleId));
        // Still the driver's, which is what keeps the later ACC-off from closing it.
        Assert.Equal(SessionActors.Driver, session.StartedBy);
        Assert.Equal(started.GetProperty("sessionId").GetString(), (await ActiveSessionIdAsync(harness, vehicleId, driverId)));
    }

    /// <summary>
    /// R-01 again, from the tracker side. A Mode C vehicle's ignition is dispatch's business
    /// (T-11), not a tracking session's.
    /// </summary>
    [Fact]
    public async Task Ignition_on_a_mode_c_vehicle_is_declined()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.C);

        var response = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        Assert.Equal("declined", (await TripStateHarness.ReadJsonAsync(response)).GetProperty("outcome").GetString());
        Assert.Empty(await harness.SessionsAsync(vehicleId));
    }

    /// <summary>An unapproved vehicle does not get a session by turning its key.</summary>
    [Fact]
    public async Task Ignition_on_an_ineligible_vehicle_is_declined()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.A, status: "PENDING");

        var response = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = vehicleId.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        Assert.Equal("declined", (await TripStateHarness.ReadJsonAsync(response)).GetProperty("outcome").GetString());
        Assert.Empty(await harness.SessionsAsync(vehicleId));
    }

    /// <summary>
    /// The owner is already live on another vehicle, so the tracker cannot take their mutex — the
    /// vehicle they are actually driving keeps it (D-03).
    /// </summary>
    [Fact]
    public async Task Ignition_on_is_declined_when_the_owner_is_live_elsewhere()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var driving = await harness.CreateVehicleAsync(driverId);
        var parked = await harness.CreateVehicleAsync(driverId);

        await harness.StartAsync(harness.Tokens.Driver(driverId), driving);

        var response = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = parked.ToString(), state = InternalSessionEndpoints.IgnitionOn });

        Assert.Equal("declined", (await TripStateHarness.ReadJsonAsync(response)).GetProperty("outcome").GetString());
        Assert.Empty(await harness.SessionsAsync(parked));
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    [Fact]
    public async Task A_malformed_ignition_state_is_400()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var response = await harness.PostInternalAsync(
            "/v1/internal/sessions/ignition",
            new { vehicleId = Guid.NewGuid().ToString(), state = "maybe" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    private static async Task<string?> ActiveSessionIdAsync(
        TripStateHarness harness, Guid vehicleId, Guid driverId)
    {
        var body = await TripStateHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/sessions/{vehicleId}/active", harness.Tokens.Driver(driverId)));

        return body.ValueKind == System.Text.Json.JsonValueKind.Null
            ? null
            : body.GetProperty("sessionId").GetString();
    }
}
