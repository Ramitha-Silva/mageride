using System.Net;
using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// US-7.9 — the buses currently running a route number.
/// </summary>
/// <remarks>
/// This is the one endpoint on the platform that takes a route number, and it is not a
/// <em>destination</em> search: AL-17 forbids a route row in the prediction list for a place, not the
/// ability to look up a route somebody has explicitly asked about. See <c>PlaceSearchTests</c>, where the
/// same route exists and search still cannot return it.
/// </remarks>
[Collection(QuerySvcCollection.Name)]
public sealed class RouteBusTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);
    private static readonly GeoPoint Kottawa = new(6.8410, 79.9720);

    /// <summary>
    /// Only vehicles on an <c>ACTIVE</c> session for this route, and only fresh ones.
    /// </summary>
    /// <remarks>
    /// A driver's phone that died an hour ago leaves the session <c>ACTIVE</c> — trip-state-svc's sweep
    /// closes it on its own clock — so the freshness rule is what keeps a bus that has stopped reporting
    /// off the list. The same filter as the map, for the same reason (US-7.17).
    /// </remarks>
    [Fact]
    public async Task Only_fresh_vehicles_on_an_active_session_for_the_route_are_listed()
    {
        await using var harness = await QueryHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Query:FreshnessWindow"] = "00:01:00" });

        var driver = await harness.CreateUserAsync("driver");
        var otherDriver = await harness.CreateUserAsync("driver");
        var thirdDriver = await harness.CreateUserAsync("driver");
        var fourthDriver = await harness.CreateUserAsync("driver");

        var number = QueryHarness.NextRouteNumber();
        var otherNumber = QueryHarness.NextRouteNumber();

        var route138 = await harness.CreateRouteAsync(number, "Kottawa – Pettah", [Kottawa, Fort]);
        var route120 = await harness.CreateRouteAsync(otherNumber, "Horana – Pettah", [Kottawa, Fort]);

        var running = await harness.CreateVehicleAsync(driver, mode: "A", vehicleType: "bus");
        var stale = await harness.CreateVehicleAsync(otherDriver, mode: "A", vehicleType: "bus");
        var finished = await harness.CreateVehicleAsync(thirdDriver, mode: "A", vehicleType: "bus");
        var otherRoute = await harness.CreateVehicleAsync(fourthDriver, mode: "A", vehicleType: "bus");

        await harness.CreateSessionAsync(driver, running, mode: "A", state: "ACTIVE", routeId: route138);
        await harness.CreateSessionAsync(otherDriver, stale, mode: "A", state: "ACTIVE", routeId: route138);
        await harness.CreateSessionAsync(thirdDriver, finished, mode: "A", state: "COMPLETED", routeId: route138);
        await harness.CreateSessionAsync(fourthDriver, otherRoute, mode: "A", state: "ACTIVE", routeId: route120);

        await harness.Positions.PublishAsync(running, Fort, mode: "A", vehicleType: "bus");
        await harness.Positions.PublishAsync(
            stale, Fort, mode: "A", vehicleType: "bus", sampleTs: DateTimeOffset.UtcNow.AddMinutes(-5));
        await harness.Positions.PublishAsync(finished, Fort, mode: "A", vehicleType: "bus");
        await harness.Positions.PublishAsync(otherRoute, Fort, mode: "A", vehicleType: "bus");

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync($"/v1/routes/{number}/buses", harness.Tokens.Passenger(passenger));

        var listed = body.GetProperty("vehicles").EnumerateArray()
            .Select(vehicle => vehicle.GetProperty("vehicleId").GetString()!)
            .ToArray();

        Assert.Equal([running.ToString()], listed);
    }

    /// <summary>
    /// "The 138 has finished for the night" and "there is no 138" are different answers, and a client
    /// cannot show US-7.14's message without the distinction.
    /// </summary>
    [Fact]
    public async Task A_known_route_with_nothing_running_is_empty_and_an_unknown_one_is_not_found()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var number = QueryHarness.NextRouteNumber();
        await harness.CreateRouteAsync(number, "Mount Lavinia – Pettah", [Kottawa, Fort]);

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        var idle = await harness.GetJsonAsync($"/v1/routes/{number}/buses", bearer);
        Assert.Empty(idle.GetProperty("vehicles").EnumerateArray());

        using var unknown = await harness.GetAsync(
            $"/v1/routes/{QueryHarness.NextRouteNumber()}/buses", bearer);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>Route numbers are matched case-insensitively — "138E" and "138e" are one route.</summary>
    [Fact]
    public async Task A_route_number_matches_regardless_of_case()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var number = QueryHarness.NextRouteNumber() + "E";
        await harness.CreateRouteAsync(number, "Kottawa – Pettah express", [Kottawa, Fort]);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(
            $"/v1/routes/{number.ToLowerInvariant()}/buses", harness.Tokens.Passenger(passenger));

        Assert.Empty(body.GetProperty("vehicles").EnumerateArray());
    }
}
