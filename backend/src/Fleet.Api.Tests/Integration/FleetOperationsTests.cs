using System.Net;
using Dapper;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Operations;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.3 / US-13.4 / US-13.5 / US-13.11 — the live map, the analytics table, the geofence CRUD
/// and the not-started alarm.
/// </summary>
/// <remarks>
/// The C059 definition of done's fourth item — "the fleet map returns only the caller org's
/// vehicles under RLS" — is <see cref="The_map_returns_only_the_callers_own_vehicles"/>.
/// </remarks>
[Collection<FleetCollection>]
public sealed class FleetOperationsTests(PostgresFixture postgres)
{
    /// <summary><b>Definition of done:</b> the fleet map returns only the caller org's vehicles.</summary>
    /// <remarks>
    /// The scoping is the database's — <c>telemetry.positions_fleet</c> filtered on
    /// <c>app.fleet_id</c>, which the fleet reader holds its only telemetry grant on (1804). This
    /// asserts it from the outside; <c>RowLevelSecurityTests</c> asserts the same thing from the
    /// inside, as a non-superuser login with no application SQL in the path.
    /// </remarks>
    [Fact]
    public async Task The_map_returns_only_the_callers_own_vehicles()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);
        await harness.ApproveAsync(theirs.FleetId);

        var myVan = await AddVehicleAsync(harness, mine, "WP-MA-1001");
        var theirBus = await AddVehicleAsync(harness, theirs, "WP-MB-1002");

        await harness.AddPositionAsync(mine.FleetId, myVan, 6.9271, 79.8612);
        await harness.AddPositionAsync(theirs.FleetId, theirBus, 7.2906, 80.6337);

        var map = await harness.GetAsync<FleetMapResponse>(
            $"/v1/fleets/{mine.FleetId}/map", mine.OwnerBearer);

        var seen = Assert.Single(map.Vehicles);

        Assert.Equal(myVan.ToString(), seen.VehicleId);
        Assert.Equal("WP-MA-1001", seen.RegistrationNumber);
        Assert.Equal(6.9271, seen.Lat, 4);

        // The other organisation's bus is not merely filtered out of this response — it is not
        // reachable through the relation at all.
        Assert.DoesNotContain(map.Vehicles, vehicle => vehicle.VehicleId == theirBus.ToString());

        var theirMap = await harness.GetAsync<FleetMapResponse>(
            $"/v1/fleets/{theirs.FleetId}/map", theirs.OwnerBearer);

        Assert.Equal(theirBus.ToString(), Assert.Single(theirMap.Vehicles).VehicleId);
    }

    /// <summary>US-7.16/7.17's judgement, applied to the fleet map: a dark tracker is not "here".</summary>
    [Fact]
    public async Task A_stale_position_is_left_off_the_map()
    {
        await using var harness = await FleetHarness.StartAsync(
            postgres,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fleet:MapStaleAfter"] = "00:05:00",
            });

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var live = await AddVehicleAsync(harness, fleet, "WP-MC-2001");
        var dark = await AddVehicleAsync(harness, fleet, "WP-MD-2002");

        await harness.AddPositionAsync(fleet.FleetId, live, 6.9, 79.9, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.AddPositionAsync(fleet.FleetId, dark, 6.8, 79.8, DateTimeOffset.UtcNow.AddHours(-3));

        var map = await harness.GetAsync<FleetMapResponse>(
            $"/v1/fleets/{fleet.FleetId}/map", fleet.OwnerBearer);

        Assert.Equal(live.ToString(), Assert.Single(map.Vehicles).VehicleId);
    }

    /// <summary>US-13.4: trips, distance, active hours and utilisation, per vehicle, per period.</summary>
    [Fact]
    public async Task Analytics_counts_journeys_and_measures_distance_from_telemetry()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddVehicleAsync(harness, fleet, "WP-ME-3001");
        var idle = await AddVehicleAsync(harness, fleet, "WP-MF-3002");
        var (driverId, _) = await harness.CreateDriverAsync();

        var started = DateTimeOffset.UtcNow.AddHours(-4);

        await harness.StartSessionAsync(van, driverId, started);

        // Two samples about 1.1 km apart along a line of latitude at Colombo's longitude.
        await harness.AddPositionAsync(fleet.FleetId, van, 6.9271, 79.8612, started, seq: 1);
        await harness.AddPositionAsync(fleet.FleetId, van, 6.9371, 79.8612, started.AddMinutes(5), seq: 2);

        var analytics = await harness.GetAsync<FleetAnalyticsResponse>(
            $"/v1/fleets/{fleet.FleetId}/analytics", fleet.OwnerBearer);

        Assert.Equal(2, analytics.Items.Count);

        var driven = Assert.Single(analytics.Items, row => row.VehicleId == van.ToString());

        Assert.Equal(1, driven.TripCount);
        Assert.InRange(driven.DistanceKm, 1.0, 1.3);

        // The session is still open, so it is measured to the end of the period rather than
        // skipped — a bus that has been out all day must not read as zero hours until it returns.
        Assert.True(driven.ActiveHours > 3.5, $"active hours were {driven.ActiveHours}");
        Assert.InRange(driven.UtilisationPct, 0, 100);

        // Every vehicle on the roster is a row, including the one that did nothing: a report that
        // omitted the idle vehicles would be a report about the busy ones.
        var quiet = Assert.Single(analytics.Items, row => row.VehicleId == idle.ToString());

        Assert.Equal(0, quiet.TripCount);
        Assert.Equal(0, quiet.DistanceKm);

        // A fleet's Mode A/B vehicles take no fares on this platform, so the field is absent rather
        // than zero — and a currency beside a null amount would be a fact about nothing.
        Assert.Null(quiet.EarningsMinor);
        Assert.Null(quiet.Currency);
    }

    [Fact]
    public async Task An_analytics_range_that_is_backwards_or_too_wide_is_refused()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var backwards = await harness.GetAsync(
            $"/v1/fleets/{fleet.FleetId}/analytics?from=2026-07-31&to=2026-07-01", fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        using var wide = await harness.GetAsync(
            $"/v1/fleets/{fleet.FleetId}/analytics?from=2020-01-01&to=2026-07-31", fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
    }

    /// <summary>US-13.5: the polygons are stored and scoped; nothing alerts on them (Phase 3).</summary>
    [Fact]
    public async Task Geofences_are_replaced_wholesale_and_belong_to_one_organisation()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);
        await harness.ApproveAsync(theirs.FleetId);

        var colombo = new object[]
        {
            new { lat = 6.90, lng = 79.85 },
            new { lat = 6.95, lng = 79.85 },
            new { lat = 6.95, lng = 79.90 },
            new { lat = 6.90, lng = 79.85 },
        };

        var stored = await harness.PutAsync<GeofenceCountResponse>(
            $"/v1/fleets/{mine.FleetId}/geofences",
            new { geofences = new[] { new { name = "Colombo depot", polygon = colombo } } },
            mine.OwnerBearer);

        Assert.Equal(1, stored.Count);

        await harness.PutAsync<GeofenceCountResponse>(
            $"/v1/fleets/{theirs.FleetId}/geofences",
            new { geofences = new[] { new { name = "Kandy depot", polygon = colombo } } },
            theirs.OwnerBearer);

        var mineRead = await harness.GetAsync<GeofencesResponse>(
            $"/v1/fleets/{mine.FleetId}/geofences", mine.OwnerBearer);

        var fence = Assert.Single(mineRead.Items);

        Assert.Equal("Colombo depot", fence.Name);
        Assert.Equal(4, fence.Polygon.Count);
        Assert.Equal(6.90, fence.Polygon[0].Lat!.Value, 4);

        // A PUT replaces this org's set and reaches nobody else's — the predicate is `fleet_id`,
        // so §17's platform polygons are outside it too.
        var emptied = await harness.PutAsync<GeofenceCountResponse>(
            $"/v1/fleets/{mine.FleetId}/geofences", new { geofences = Array.Empty<object>() }, mine.OwnerBearer);

        Assert.Equal(0, emptied.Count);

        var theirsRead = await harness.GetAsync<GeofencesResponse>(
            $"/v1/fleets/{theirs.FleetId}/geofences", theirs.OwnerBearer);

        Assert.Equal("Kandy depot", Assert.Single(theirsRead.Items).Name);
    }

    [Fact]
    public async Task An_unclosed_or_out_of_bounds_ring_is_refused_and_nothing_is_stored()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var unclosed = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/geofences",
            new
            {
                geofences = new[]
                {
                    new
                    {
                        name = "Not a ring",
                        polygon = new object[]
                        {
                            new { lat = 6.90, lng = 79.85 },
                            new { lat = 6.95, lng = 79.85 },
                            new { lat = 6.95, lng = 79.90 },
                            new { lat = 6.91, lng = 79.86 },
                        },
                    },
                },
            },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(unclosed);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Contains("first and last", problem.Body, StringComparison.Ordinal);

        // All-or-nothing: the route replaces a set, and refusing halfway would leave an operator
        // with whichever fences happened to sort first.
        var geofences = await harness.GetAsync<GeofencesResponse>(
            $"/v1/fleets/{fleet.FleetId}/geofences", fleet.OwnerBearer);

        Assert.Empty(geofences.Items);
    }

    /// <summary>US-13.11: a booked departure nobody made becomes MISSED, exactly once.</summary>
    [Fact]
    public async Task A_departure_nobody_made_is_claimed_once_and_one_that_was_made_is_left_alone()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var missedVehicle = await AddVehicleAsync(harness, fleet, "WP-MG-4001");
        var runVehicle = await AddVehicleAsync(harness, fleet, "WP-MH-4002");
        var (driverId, _) = await harness.CreateDriverAsync();

        var departAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Booked in the past, which the API refuses for the reason the sweep exists — so the two
        // rows go in directly, which is also the only way to test a sweep without sleeping.
        var missedId = await InsertScheduleAsync(harness, fleet.FleetId, missedVehicle, departAt);
        var runId = await InsertScheduleAsync(harness, fleet.FleetId, runVehicle, departAt);

        // One of the two vehicles actually left, a few minutes early.
        await harness.StartSessionAsync(runVehicle, driverId, departAt.AddMinutes(-4));

        var worker = harness.Services.GetRequiredService<ScheduleAlarmWorker>();

        Assert.Equal(1, await worker.SweepAsync(CancellationToken.None));

        var schedules = await harness.GetAsync<FleetSchedulesResponse>(
            $"/v1/fleets/{fleet.FleetId}/schedules?from={departAt.AddHours(-1):O}", fleet.OwnerBearer);

        var missed = Assert.Single(schedules.Items, row => row.ScheduleId == missedId.ToString());
        var ran = Assert.Single(schedules.Items, row => row.ScheduleId == runId.ToString());

        Assert.Equal("MISSED", missed.Status);
        Assert.NotNull(missed.AlarmRaisedAt);

        // A bus that pulled out four minutes early made its departure.
        Assert.Equal("STARTED", ran.Status);
        Assert.Null(ran.AlarmRaisedAt);

        // The claim is the update, so a second pass — or a second replica — finds nothing.
        Assert.Equal(0, await worker.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_departure_is_booked_in_the_future_for_a_vehicle_of_this_fleet()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);
        await harness.ApproveAsync(theirs.FleetId);

        var vehicle = await AddVehicleAsync(harness, mine, "WP-MJ-5001");
        var theirVehicle = await AddVehicleAsync(harness, theirs, "WP-MK-5002");

        var departAt = DateTimeOffset.UtcNow.AddHours(6);

        var schedule = await harness.PostJsonAsync<FleetScheduleResponse>(
            $"/v1/fleets/{mine.FleetId}/schedules",
            new { vehicleId = vehicle.ToString(), departAt, notStartedAlarmMinutes = 15 },
            mine.OwnerBearer);

        Assert.Equal("SCHEDULED", schedule.Status);
        Assert.Equal(15, schedule.NotStartedAlarmMinutes);

        // Same vehicle, same instant, twice — two managers entering the 06:10 minutes apart is not
        // something an Idempotency-Key catches, and ux_fleet_schedules_slot does.
        using var duplicate = await harness.PostAsync(
            $"/v1/fleets/{mine.FleetId}/schedules",
            new { vehicleId = vehicle.ToString(), departAt },
            mine.OwnerBearer);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var past = await harness.PostAsync(
            $"/v1/fleets/{mine.FleetId}/schedules",
            new { vehicleId = vehicle.ToString(), departAt = DateTimeOffset.UtcNow.AddHours(-1) },
            mine.OwnerBearer);

        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);

        using var crossOrg = await harness.PostAsync(
            $"/v1/fleets/{mine.FleetId}/schedules",
            new { vehicleId = theirVehicle.ToString(), departAt },
            mine.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(crossOrg);

        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("vehicle-not-found", problem.Code);
    }

    /// <summary>
    /// US-13.5 is Phase 3, so the page is empty by construction rather than by filtering.
    /// </summary>
    [Fact]
    public async Task The_alert_page_exists_and_is_empty()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        var alerts = await harness.GetAsync<FleetAlertsResponse>(
            $"/v1/fleets/{fleet.FleetId}/alerts", fleet.OwnerBearer);

        Assert.Empty(alerts.Items);
        Assert.False(alerts.HasMore);
        Assert.Null(alerts.Cursor);
    }

    /// <summary>
    /// US-13.A7 disables onboarding and assignment, not monitoring.
    /// </summary>
    /// <remarks>
    /// A PENDING organisation waits days for a Verification Officer, and refusing it the map for
    /// that whole time would take away the one thing it can honestly do — watch the vehicles it
    /// already runs. The writes on the same group stay gated, which is what the second half checks.
    /// </remarks>
    [Fact]
    public async Task A_pending_organisation_can_still_watch_its_fleet_and_still_cannot_change_it()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        var map = await harness.GetAsync<FleetMapResponse>(
            $"/v1/fleets/{fleet.FleetId}/map", fleet.OwnerBearer);

        Assert.Empty(map.Vehicles);

        await harness.GetAsync<FleetAnalyticsResponse>(
            $"/v1/fleets/{fleet.FleetId}/analytics", fleet.OwnerBearer);

        using var refused = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/geofences", new { geofences = Array.Empty<object>() }, fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("fleet-not-approved", problem.Code);
    }

    /// <summary>
    /// A hop with nowhere to go leaves its routes unmapped rather than answering from nowhere.
    /// </summary>
    /// <remarks>
    /// A bind that silently did nothing would leave an operator believing an ST-901 was armed on a
    /// bus nothing is tracking, and a subscriber roster served from nowhere would be a screen full
    /// of zeroes. 404 is the honest answer, and it is announced as an error at start-up.
    /// </remarks>
    [Fact]
    public async Task The_tracker_and_subscription_routes_are_absent_when_their_service_is_not_configured()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var patterns = harness.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(patterns, pattern => pattern.Contains("/trackers/bind", StringComparison.Ordinal));
        Assert.DoesNotContain(patterns, pattern => pattern.Contains("/subscribers", StringComparison.Ordinal));

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/trackers/bind",
            new { imei = "356938035643809", vehicleId = Guid.CreateVersion7().ToString() },
            fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> AddVehicleAsync(
        FleetHarness harness, SeededFleet fleet, string registration)
    {
        var vehicle = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = registration, vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        return Guid.Parse(vehicle.VehicleId);
    }

    /// <summary>
    /// A departure in the past, which the route refuses and the sweep exists for.
    /// </summary>
    private static async Task<Guid> InsertScheduleAsync(
        FleetHarness harness, Guid fleetId, Guid vehicleId, DateTimeOffset departAt)
    {
        await using var connection = await harness.OpenAsync();

        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO registry.fleet_schedules
              (fleet_id, vehicle_id, depart_at, not_started_alarm_minutes)
            VALUES (@FleetId, @VehicleId, @DepartAt, 10)
            RETURNING id;
            """,
            new { FleetId = fleetId, VehicleId = vehicleId, DepartAt = departAt });
    }
}
