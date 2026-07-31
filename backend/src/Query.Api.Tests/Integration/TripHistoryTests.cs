using System.Net;
using MageRide.Query.Geo;
using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// Trip history and detail (US-8.7), including the Definition of Done's third claim: trip detail
/// returns the <b>stored</b> polyline rather than a re-derivation from raw positions.
/// </summary>
[Collection(QuerySvcCollection.Name)]
public sealed class TripHistoryTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);
    private static readonly GeoPoint GalleFace = new(6.9271, 79.8449);

    /// <summary>
    /// A passenger's history is their Mode C rides; a driver's also carries their Mode A/B sessions.
    /// </summary>
    /// <remarks>
    /// The union is ordered as one series and paged with a keyset cursor. The asymmetry is not a choice:
    /// the only link from a <em>user</em> to a <c>trips.sessions</c> row is <c>driver_id</c>, because the
    /// platform records no ridership for a bus or a school van.
    /// </remarks>
    [Fact]
    public async Task A_driver_history_spans_both_planes_newest_first()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var passenger = await harness.CreateUserAsync();
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");
        var bus = await harness.CreateVehicleAsync(driver, mode: "A", vehicleType: "bus");

        var older = DateTimeOffset.UtcNow.AddHours(-5);
        var newer = DateTimeOffset.UtcNow.AddHours(-1);

        var session = await harness.CreateSessionAsync(
            driver, bus, mode: "A", startedAt: older, endedAt: older.AddHours(1));

        var ride = await harness.CreateRideAsync(
            passenger, Fort, GalleFace,
            state: "Paid", driverId: driver, vehicleId: taxi,
            createdAt: newer, terminalAt: newer.AddMinutes(20));

        var body = await harness.GetJsonAsync($"/v1/trips/{driver}", harness.Tokens.Driver(driver));

        var items = body.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal(ride.ToString(), items[0].GetProperty("tripId").GetString());
        Assert.Equal("ride", items[0].GetProperty("plane").GetString());
        Assert.Equal(session.ToString(), items[1].GetProperty("tripId").GetString());
        Assert.Equal("session", items[1].GetProperty("plane").GetString());
        Assert.Equal("A", items[1].GetProperty("mode").GetString());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// The keyset cursor walks the union without skipping or repeating a row, including across trips that
    /// share a timestamp — which is why the tie-break id is in the key.
    /// </summary>
    [Fact]
    public async Task History_pages_through_the_union_without_skipping_or_repeating()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        var sharedInstant = DateTimeOffset.UtcNow.AddHours(-3);
        var expected = new List<string>();

        for (var i = 0; i < 7; i++)
        {
            // Two of the seven deliberately share `created_at` to the microsecond.
            var createdAt = i is 2 or 3 ? sharedInstant : DateTimeOffset.UtcNow.AddMinutes(-10 * (i + 1));

            expected.Add((await harness.CreateRideAsync(
                passenger, Fort, GalleFace,
                state: "Paid", driverId: driver, vehicleId: taxi,
                createdAt: createdAt, terminalAt: createdAt.AddMinutes(15))).ToString());
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var url = $"/v1/trips/{passenger}?limit=3" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await harness.GetJsonAsync(url, harness.Tokens.Passenger(passenger));

            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("tripId").GetString()!));

            cursor = page.GetProperty("cursor").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null
                : page.GetProperty("cursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
        Assert.Equal(expected.Order().ToArray(), seen.Order().ToArray());
    }

    /// <summary>
    /// Definition of Done: "trip detail returns the stored polyline, not a re-derivation from raw
    /// positions."
    /// </summary>
    /// <remarks>
    /// The Mode A/B track is the one ADD §9.2 promises and persistence-writer-svc stores in
    /// <c>trips.session_summaries.polyline</c>. Nothing here reads <c>telemetry.positions</c>: the test
    /// writes the stored line and no raw rows at all, so a re-derivation would come back empty.
    /// </remarks>
    [Fact]
    public async Task Session_detail_returns_the_stored_polyline_and_its_provenance()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var bus = await harness.CreateVehicleAsync(driver, mode: "A", vehicleType: "bus");

        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var session = await harness.CreateSessionAsync(
            driver, bus, mode: "A", startedAt: startedAt, endedAt: startedAt.AddMinutes(45));

        var path = new[]
        {
            new GeoPoint(6.9344, 79.8428),
            new GeoPoint(6.9310, 79.8440),
            new GeoPoint(6.9271, 79.8449),
        };

        await harness.AddSessionSummaryAsync(
            session, bus, driver, "A", path, distanceM: 1_820.5,
            startedAt: startedAt, endedAt: startedAt.AddMinutes(45));

        var body = await harness.GetJsonAsync(
            $"/v1/trips/{driver}/{session}", harness.Tokens.Driver(driver));

        Assert.Equal("session", body.GetProperty("plane").GetString());
        Assert.Equal("telemetry", body.GetProperty("geometrySource").GetString());
        Assert.Equal(1.8205, body.GetProperty("distanceKm").GetDouble(), 3);
        Assert.Equal(45 * 60, body.GetProperty("durationSec").GetInt32());

        // The encoded line decodes back to the three points that were stored, to five decimals.
        var decoded = EncodedPolyline.Decode(body.GetProperty("polyline").GetString());

        Assert.Equal(3, decoded.Count);

        for (var i = 0; i < path.Length; i++)
        {
            Assert.Equal(path[i].Latitude, decoded[i].Latitude, 5);
            Assert.Equal(path[i].Longitude, decoded[i].Longitude, 5);
        }
    }

    /// <summary>
    /// A journey that produced fewer than two distinct fixes has no line, and says so rather than
    /// returning a degenerate one.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_stored_line_reports_no_geometry()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var bus = await harness.CreateVehicleAsync(driver, mode: "A", vehicleType: "bus");
        var session = await harness.CreateSessionAsync(driver, bus, mode: "A");

        await harness.AddSessionSummaryAsync(
            session, bus, driver, "A", [new GeoPoint(6.9344, 79.8428)], distanceM: 0, geometrySource: "none");

        var body = await harness.GetJsonAsync(
            $"/v1/trips/{driver}/{session}", harness.Tokens.Driver(driver));

        Assert.Equal("none", body.GetProperty("geometrySource").GetString());
        Assert.False(body.TryGetProperty("polyline", out _));
    }

    /// <summary>
    /// A Mode C ride's detail carries no fabricated distance.
    /// </summary>
    /// <remarks>
    /// No service stores a Mode C track: ADD §9.2's stored summary is per <em>session</em> and its
    /// <c>mode</c> CHECK admits only A and B, while E-04's Kalman-filtered track is computed by fare-svc
    /// for the distance the fare is charged on and never persisted. The line is therefore read from the
    /// <c>telemetry.positions_1m</c> continuous aggregate — one point per minute — and the distance is
    /// <b>omitted</b> rather than derived from it, because a minute-grain chord chain understates a city
    /// journey by a third or more and this is the number on the receipt.
    /// </remarks>
    [Fact]
    public async Task A_ride_detail_omits_the_distance_rather_than_deriving_one()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C", driverName: "Kamala Silva");

        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var ride = await harness.CreateRideAsync(
            passenger, Fort, GalleFace,
            state: "Paid", driverId: driver, vehicleId: taxi,
            createdAt: createdAt, terminalAt: createdAt.AddMinutes(18), fareEstimateMinor: 45_000);

        await harness.AddPaymentAsync(ride, amountMinor: 48_000, surchargeMinor: 2_400, tipMinor: 5_000);

        var body = await harness.GetJsonAsync(
            $"/v1/trips/{passenger}/{ride}", harness.Tokens.Passenger(passenger));

        Assert.Equal("ride", body.GetProperty("plane").GetString());
        Assert.Equal("C", body.GetProperty("mode").GetString());
        Assert.Equal(48_000, body.GetProperty("fareMinor").GetInt64());
        Assert.Equal("Kamala Silva", body.GetProperty("driver").GetProperty("name").GetString());

        Assert.False(body.TryGetProperty("distanceKm", out _));

        // No telemetry was written, so the aggregate holds nothing and the source says so honestly.
        Assert.Equal("none", body.GetProperty("geometrySource").GetString());
    }

    [Fact]
    public async Task A_trip_that_is_not_yours_is_not_found()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var mine = await harness.CreateUserAsync();
        var theirs = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        var ride = await harness.CreateRideAsync(
            theirs, Fort, GalleFace, state: "Paid", driverId: driver, vehicleId: taxi,
            terminalAt: DateTimeOffset.UtcNow);

        // Their trip, asked for under my own id: the query is scoped by the token's subject, so this is
        // a 404 — "does not exist" and "is not yours" must be the same answer.
        using var mismatched = await harness.GetAsync(
            $"/v1/trips/{mine}/{ride}", harness.Tokens.Passenger(mine));

        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);

        // Their id in the path, my token: refused before any query runs.
        using var impersonation = await harness.GetAsync(
            $"/v1/trips/{theirs}/{ride}", harness.Tokens.Passenger(mine));

        Assert.Equal(HttpStatusCode.Forbidden, impersonation.StatusCode);
    }

    /// <summary>US-24.9/24.10: the back office reads a passenger's trips read-only.</summary>
    [Fact]
    public async Task A_support_agent_may_read_another_users_history()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var agent = await harness.CreateUserAsync("support_csr");
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.CreateRideAsync(
            passenger, Fort, GalleFace, state: "Paid", driverId: driver, vehicleId: taxi,
            terminalAt: DateTimeOffset.UtcNow);

        var body = await harness.GetJsonAsync($"/v1/trips/{passenger}", harness.Tokens.Support(agent));

        Assert.Single(body.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// P-01/P-03: a proxy booking shows up in the booker's history as well as the rider's.
    /// </summary>
    [Fact]
    public async Task A_proxy_booking_appears_in_the_bookers_history()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var booker = await harness.CreateUserAsync();
        var rider = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var taxi = await harness.CreateVehicleAsync(driver, mode: "C");

        var ride = await harness.CreateRideAsync(
            rider, Fort, GalleFace, state: "Paid", driverId: driver, vehicleId: taxi,
            terminalAt: DateTimeOffset.UtcNow, bookerId: booker);

        var forBooker = await harness.GetJsonAsync($"/v1/trips/{booker}", harness.Tokens.Passenger(booker));
        var forRider = await harness.GetJsonAsync($"/v1/trips/{rider}", harness.Tokens.Passenger(rider));

        Assert.Equal(
            ride.ToString(),
            forBooker.GetProperty("items").EnumerateArray().Single().GetProperty("tripId").GetString());

        Assert.Equal(
            ride.ToString(),
            forRider.GetProperty("items").EnumerateArray().Single().GetProperty("tripId").GetString());
    }
}
