using System.Net;
using MageRide.TestKit;
using MageRide.TripState.Domain;
using MageRide.TripState.Sessions;
using MageRide.TripState.Tests.Infrastructure;

namespace MageRide.TripState.Tests.Integration;

/// <summary>
/// D-03 / US-9.6: a driver holds one live session, and the database is what says so.
/// </summary>
[Collection<TripStateCollection>]
public sealed class SessionMutexTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task Ten_concurrent_starts_leave_exactly_one_live_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);

        // Ten different vehicles, so nothing but the driver-scoped index can be what stops them.
        // Ten starts on one vehicle would also be settled by the idempotency middleware, and that
        // would prove the wrong thing.
        var vehicles = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => harness.CreateVehicleAsync(driverId)));

        var responses = await Task.WhenAll(vehicles.Select(vehicleId => harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.A },
            bearer)));

        var created = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
        var refused = responses.Count(response => response.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, created);
        Assert.Equal(9, refused);

        // The invariant itself, read from the table rather than inferred from the responses.
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));

        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "driver-already-live");
        }
    }

    /// <summary>
    /// The mutex is per driver, not per vehicle — two drivers going live at once is the normal
    /// case for a fleet and must not contend.
    /// </summary>
    [Fact]
    public async Task Two_drivers_may_hold_sessions_at_the_same_time()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var first = await harness.CreateUserAsync();
        var second = await harness.CreateUserAsync();

        await harness.StartAsync(harness.Tokens.Driver(first), await harness.CreateVehicleAsync(first));
        await harness.StartAsync(harness.Tokens.Driver(second), await harness.CreateVehicleAsync(second));

        Assert.Equal(1, await harness.ActiveSessionCountAsync(first));
        Assert.Equal(1, await harness.ActiveSessionCountAsync(second));
    }

    /// <summary>Ending frees the mutex, which is what makes the next journey possible at all.</summary>
    [Fact]
    public async Task Ending_a_session_frees_the_driver_to_start_another()
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

        // The published fact the dispatch and tracking planes read (D-03).
        Assert.Equal(sessionId, await harness.PublishedSessionAsync(driverId));

        var ended = await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal(SessionViews.Ended, (await TripStateHarness.ReadJsonAsync(ended)).GetProperty("state").GetString());
        Assert.Null(await harness.PublishedSessionAsync(driverId));

        await harness.StartAsync(bearer, second);
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }

    /// <summary>
    /// R-01, at the boundary this service exists to hold. A Mode C vehicle is a ride, and
    /// ride-svc owns it — the message says so rather than answering "unknown mode".
    /// </summary>
    [Fact]
    public async Task A_mode_c_request_is_refused_and_says_where_it_belongs()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = "C" },
            harness.Tokens.Driver(driverId));

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Contains("/v1/rides/request", problem.Root.GetProperty("errors").ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Mode C <i>vehicle</i> is refused even when the body claims B: the mode is registry's
    /// fact, not the client's, and a Mode C three-wheeler must not acquire a tracking session.
    /// </summary>
    [Fact]
    public async Task A_mode_c_vehicle_cannot_be_given_a_tracking_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.C);

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.B },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "mode-not-allowed");
        Assert.Empty(await harness.SessionsAsync(vehicleId));
    }

    /// <summary>The mode in the body has to be the mode the vehicle is registered as.</summary>
    [Fact]
    public async Task A_mode_that_disagrees_with_the_vehicle_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.A);

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.B },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// The eligibility projection is registry's, and this service maps its own errors from the raw
    /// columns — an unapproved vehicle is <c>vehicle-not-approved</c>, not "no such vehicle".
    /// </summary>
    [Fact]
    public async Task An_unapproved_vehicle_is_403_vehicle_not_approved()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId, OperatingModes.A, status: "PENDING");

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.A },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "vehicle-not-approved");
    }

    /// <summary>E-03's document-expiry suspension is the other half of the same gate.</summary>
    [Fact]
    public async Task A_document_suspended_vehicle_is_refused_and_says_why()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(
            driverId, OperatingModes.A, dispatchState: "DISPATCH_SUSPENDED");

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.A },
            harness.Tokens.Driver(driverId));

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "vehicle-not-approved");
        Assert.Contains("E-03", problem.Root.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A vehicle the driver may not operate is 404, not 403: the projection is driver-scoped, so
    /// "not yours" and "does not exist" are the same query result and telling them apart would
    /// leak a stranger's vehicle.
    /// </summary>
    [Fact]
    public async Task Another_drivers_vehicle_is_404_vehicle_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var ownerId = await harness.CreateUserAsync();
        var strangerId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(ownerId);

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode = OperatingModes.A },
            harness.Tokens.Driver(strangerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "vehicle-not-found");
    }

    /// <summary>R-14: a retried start replays rather than colliding with the mutex it just took.</summary>
    [Fact]
    public async Task A_replayed_start_returns_the_same_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var key = Guid.NewGuid().ToString();
        var body = new { vehicleId = vehicleId.ToString(), mode = OperatingModes.A };

        var first = await harness.PostAsync("/v1/sessions/start", body, bearer, key);
        var second = await harness.PostAsync("/v1/sessions/start", body, bearer, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single(await harness.SessionsAsync(vehicleId));
    }

    /// <summary>Opening the Driver App does not grant the driver role (C020 decision 4).</summary>
    [Fact]
    public async Task A_passenger_cannot_start_a_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var passengerId = await harness.CreateUserAsync("passenger");

        var response = await harness.PostAsync(
            "/v1/sessions/start",
            new { vehicleId = Guid.NewGuid().ToString(), mode = OperatingModes.A },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Ending somebody else's session is 403, and changes nothing.</summary>
    [Fact]
    public async Task A_stranger_cannot_end_a_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var strangerId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/end", null, harness.Tokens.Driver(strangerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
    }
}
