using MageRide.Shared.Geo;
using System.Net;
using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 1 — "going online inserts presence and indexes the driver in the correct H3 res-5 cell".</b>
/// </summary>
[Collection<DispatchCollection>]
public sealed class PresenceTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    /// <summary>Colombo Fort's res-5 cell, from the reference H3 v4 implementation.</summary>
    private const string ColomboFortRes5 = "85611cb3fffffff";

    [Fact]
    public async Task Going_online_inserts_presence_and_indexes_the_driver_in_the_right_cell()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        var response = await harness.GoOnlineAsync(driver, ColomboFort);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await DispatchHarness.ReadJsonAsync(response);
        Assert.Equal(PresenceStates.Available, body.GetProperty("state").GetString());

        // The durable half (dispatch.driver_presence, migration 0701).
        await using var connection = await harness.OpenAsync();
        var row = await connection.QuerySingleAsync<StoredPresence>(
            """
            SELECT state AS State, vehicle_id AS VehicleId, vehicle_type AS VehicleType,
                   ST_Y(geo::geometry) AS Lat, ST_X(geo::geometry) AS Lng
              FROM dispatch.driver_presence WHERE driver_id = @DriverId;
            """,
            new { driver.DriverId });

        Assert.Equal(PresenceStates.Available, row.State);
        Assert.Equal(driver.VehicleId, row.VehicleId);
        Assert.Equal("three_wheeler", row.VehicleType);
        Assert.Equal(ColomboFort.Latitude, row.Lat, 6);
        Assert.Equal(ColomboFort.Longitude, row.Lng, 6);

        // The hot half (R-08). Asserted against the literal ADD §9.4 key, not against the helper
        // that builds it — position-processor-svc writes this key from another codebase.
        var db = harness.Redis.GetDatabase();
        var indexKey = (RedisKey)$"geo:drivers:available:three_wheeler:{ColomboFortRes5}";

        Assert.NotNull(await db.SortedSetScoreAsync(indexKey, driver.DriverId.ToString()));

        var position = await db.GeoPositionAsync(indexKey, driver.DriverId.ToString());
        Assert.NotNull(position);
        Assert.Equal(ColomboFort.Latitude, position.Value.Latitude, 3);
        Assert.Equal(ColomboFort.Longitude, position.Value.Longitude, 3);
    }

    [Fact]
    public async Task The_availability_hash_carries_the_ADD_9_4_shape_and_a_60_second_TTL()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(ColomboFort);

        var db = harness.Redis.GetDatabase();
        var key = (RedisKey)RedisKeys.DriverAvailability(driver.DriverId);

        var fields = await db.HashGetAllAsync(key);
        var byName = fields.ToDictionary(f => f.Name.ToString(), f => f.Value.ToString(), StringComparer.Ordinal);

        Assert.Equal(PresenceStates.Available, byName["state"]);
        Assert.Equal("three_wheeler", byName["vehicleType"]);
        Assert.Equal(ColomboFortRes5, byName["cell"]);
        Assert.Contains("lastSeen", byName);

        // R-08's TTL. Nothing refreshes it in this slice — position-processor-svc (C039) owns the
        // heartbeat — which is exactly why the exact post-filter reads the durable presence row.
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Going_online_again_somewhere_else_leaves_no_ghost_in_the_old_cell()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(ColomboFort);

        // Negombo — a different res-5 cell entirely.
        var negombo = new GeoPoint(7.2083, 79.8358);
        Assert.Equal(HttpStatusCode.OK, (await harness.GoOnlineAsync(driver, negombo)).StatusCode);

        var db = harness.Redis.GetDatabase();
        var oldKey = (RedisKey)RedisKeys.AvailableDrivers("three_wheeler", ColomboFortRes5);

        Assert.Null(await db.SortedSetScoreAsync(oldKey, driver.DriverId.ToString()));

        var newCell = new H3Grid(5, 2).CellAt(negombo);
        Assert.NotEqual(ColomboFortRes5, newCell);
        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", newCell), driver.DriverId.ToString()));
    }

    [Fact]
    public async Task Going_offline_clears_both_halves()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(ColomboFort);

        var response = await harness.GoOfflineAsync(driver);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            PresenceStates.Offline,
            (await DispatchHarness.ReadJsonAsync(response)).GetProperty("state").GetString());

        var db = harness.Redis.GetDatabase();
        Assert.Null(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", ColomboFortRes5), driver.DriverId.ToString()));
        Assert.False(await db.KeyExistsAsync(RedisKeys.DriverAvailability(driver.DriverId)));

        await using var connection = await harness.OpenAsync();
        var state = await connection.ExecuteScalarAsync<string>(
            "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId });

        Assert.Equal(PresenceStates.Offline, state);
    }

    [Fact]
    public async Task Going_offline_twice_is_not_an_error()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(ColomboFort);

        Assert.Equal(HttpStatusCode.OK, (await harness.GoOfflineAsync(driver)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.GoOfflineAsync(driver)).StatusCode);
    }

    [Fact]
    public async Task A_driver_who_was_never_online_can_still_go_offline()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        Assert.Equal(HttpStatusCode.OK, (await harness.GoOfflineAsync(driver)).StatusCode);
    }

    [Fact]
    public async Task An_unapproved_vehicle_cannot_go_on_standby()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync(status: "PENDING");

        var response = await harness.GoOnlineAsync(driver, ColomboFort);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorAsync(response, "vehicle-not-approved");
    }

    [Fact]
    public async Task A_mode_A_vehicle_is_not_a_Mode_C_candidate()
    {
        await using var harness = await StartAsync();

        // R-01: Mode A/B is trip-state-svc's tracking plane. The boundary is enforced where a
        // driver would otherwise cross it.
        var driver = await harness.CreateDriverAsync(vehicleType: "sedan", mode: "B");

        var response = await harness.GoOnlineAsync(driver, ColomboFort);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorAsync(response, "mode-not-allowed");
    }

    [Fact]
    public async Task A_driver_cannot_go_online_on_somebody_elses_vehicle()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var stranger = await harness.CreateDriverAsync();

        var response = await harness.GoOnlineAsync(driver, ColomboFort, vehicleId: stranger.VehicleId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "vehicle-not-found");
    }

    [Fact]
    public async Task A_passenger_who_opened_the_driver_app_is_refused()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.PassengerOnDriverApp(driver.DriverId);

        var response = await harness.PostAsync(
            "/v1/standby/online",
            new
            {
                vehicleId = driver.VehicleId.ToString(),
                position = new { lat = ColomboFort.Latitude, lng = ColomboFort.Longitude },
            },
            bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        await using var harness = await StartAsync();

        var response = await harness.PostAsync("/v1/standby/offline", new { }, bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_position_outside_the_world_is_a_400()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        var response = await harness.PostAsync(
            "/v1/standby/online",
            new { vehicleId = driver.VehicleId.ToString(), position = new { lat = 91.0, lng = 79.8 } },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "validation-failed");
    }

    [Fact]
    public async Task A_missing_position_is_a_400()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        var response = await harness.PostAsync(
            "/v1/standby/online", new { vehicleId = driver.VehicleId.ToString() }, driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_D_06_job_board_anchor_is_stored_even_though_nothing_reads_it_yet()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var home = new GeoPoint(6.8000, 79.9000);

        Assert.Equal(HttpStatusCode.OK, (await harness.GoOnlineAsync(driver, ColomboFort, home)).StatusCode);

        await using var connection = await harness.OpenAsync();
        var storedLat = await connection.ExecuteScalarAsync<double>(
            "SELECT ST_Y(driver_home::geometry) FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { driver.DriverId });

        Assert.Equal(home.Latitude, storedLat, 6);

        // A later heartbeat without driverHome must not erase it — the 30 km ST_DWithin the anchor
        // exists for (C035) would otherwise silently stop matching after one position update.
        Assert.Equal(HttpStatusCode.OK, (await harness.GoOnlineAsync(driver, ColomboFort)).StatusCode);

        var afterHeartbeat = await connection.ExecuteScalarAsync<double?>(
            "SELECT ST_Y(driver_home::geometry) FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { driver.DriverId });

        Assert.Equal(home.Latitude, afterHeartbeat!.Value, 6);
    }

    [Fact]
    public async Task Presence_is_one_row_per_driver_however_many_vehicles_they_own()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        // A second APPROVED vehicle for the same owner — US-9.6 allows owning several, and the
        // presence row is the plane where "only one at a time" is expressed.
        var secondVehicleId = Guid.NewGuid();
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO registry.vehicles
                  (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
                VALUES (@Id, @OwnerId, @Plate, 'sedan', 'C', 'APPROVED', 'Test Driver');
                """,
                new { Id = secondVehicleId, OwnerId = driver.DriverId, Plate = DispatchHarness.NextPlate() });
        }

        Assert.Equal(HttpStatusCode.OK, (await harness.GoOnlineAsync(driver, ColomboFort)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.GoOnlineAsync(driver, ColomboFort, vehicleId: secondVehicleId)).StatusCode);

        await using var check = await harness.OpenAsync();
        var rows = await check.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { driver.DriverId });

        Assert.Equal(1, rows);

        var tier = await check.ExecuteScalarAsync<string>(
            "SELECT vehicle_type FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { driver.DriverId });

        Assert.Equal("sedan", tier);

        // And the three_wheeler index no longer claims them — the pool must never advertise a
        // driver under a tier they are not currently driving.
        var db = harness.Redis.GetDatabase();
        Assert.Null(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", ColomboFortRes5), driver.DriverId.ToString()));
        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("sedan", ColomboFortRes5), driver.DriverId.ToString()));
    }

    private Task<DispatchHarness> StartAsync()
    {
        // Skip-not-fail, the TestKit's convention: a developer without a Docker daemon still runs
        // the pure tests. CI sets MAGERIDE_REQUIRE_CONTAINERS=1, which turns this into a failure.
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string code)
    {
        var body = await DispatchHarness.ReadJsonAsync(response);
        Assert.Equal($"https://mageride.lk/errors/{code}", body.GetProperty("type").GetString());
    }

    /// <summary>The <c>dispatch.driver_presence</c> columns this suite reads back.</summary>
    private sealed record StoredPresence(string State, Guid VehicleId, string VehicleType, double Lat, double Lng);
}
