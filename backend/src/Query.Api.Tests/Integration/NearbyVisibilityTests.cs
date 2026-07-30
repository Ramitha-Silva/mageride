using System.Text.Json;
using MageRide.Query.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Query.Tests.Integration;

/// <summary>
/// The Definition of Done's first two claims: <c>GET /v1/nearby</c> excludes engaged Mode C vehicles
/// and anything stale beyond the freshness window, and a type filter returns exactly the requested
/// types including trains.
/// </summary>
/// <remarks>
/// Every one of these drives a real Redis holding real <c>geo:live</c> / <c>veh:meta</c> hashes written
/// by the <b>real</b> position-processor-svc writer, and a real Postgres holding the registry rows the
/// disclosure rules read. The rule under test is
/// <c>MageRide.Shared.Realtime.VehicleVisibilityRules</c> — the same function fanout-svc applies to a
/// socket frame — so what these prove is that the snapshot path reaches it with the right inputs.
/// </remarks>
[Collection(QuerySvcCollection.Name)]
public sealed class NearbyVisibilityTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>Colombo Fort. Everything in these tests happens within a few hundred metres of it.</summary>
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);

    /// <summary>Galle Face — ~1.2 km from <see cref="Fort"/>, comfortably inside a 3 km view.</summary>
    private static readonly GeoPoint GalleFace = new(6.9271, 79.8449);

    /// <summary>Kandy — 100 km away, so outside any radius these tests ask for.</summary>
    private static readonly GeoPoint Kandy = new(7.2906, 80.6337);

    [Fact]
    public async Task An_engaged_Mode_C_vehicle_is_not_on_the_public_map()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var idle = await harness.CreateVehicleAsync(driver, mode: "C");
        var engaged = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.Positions.PublishAsync(idle, Fort, mode: "C");
        await harness.Positions.PublishAsync(engaged, Fort, mode: "C");

        // fanout-svc sets this from `ride.accepted` (US-7.16). query-svc only reads it.
        await harness.Positions.EngageAsync(engaged, Guid.NewGuid());

        var stranger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(stranger), Fort);

        Assert.Contains(idle.ToString(), vehicles.Keys);
        Assert.DoesNotContain(engaged.ToString(), vehicles.Keys);
    }

    /// <summary>
    /// US-7.16's second half — "only the booking passenger sees the assigned vehicle" — and US-7.12's
    /// disclosure of the driver's name and plate after acceptance.
    /// </summary>
    /// <remarks>
    /// Membership is decided by <c>rides.rides</c>, not by a state list this service keeps: the
    /// engagement key names the ride and the database says whether the caller is a party to it. So this
    /// test also pins that a passenger who is a <em>booker</em> rather than the rider sees the car
    /// (P-01/P-03), which a passenger-id-only predicate would get wrong.
    /// </remarks>
    [Fact]
    public async Task The_booking_passenger_sees_their_own_engaged_vehicle_with_the_driver_and_plate()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateUserAsync("driver");
        var vehicle = await harness.CreateVehicleAsync(driver, mode: "C", driverName: "Nimal Perera");

        var rideId = await harness.CreateRideAsync(
            passenger, Fort, GalleFace, state: "InProgress", driverId: driver, vehicleId: vehicle);

        await harness.Positions.PublishAsync(vehicle, Fort, mode: "C");
        await harness.Positions.EngageAsync(vehicle, rideId);

        var mine = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        Assert.True(mine.TryGetValue(vehicle.ToString(), out var seen), "the passenger cannot see their own car");
        Assert.Equal("Nimal Perera", seen.GetProperty("driverName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(seen.GetProperty("registrationNumber").GetString()));

        // US-7.11: the ride is InProgress, so the estimate points at the drop-off.
        Assert.True(seen.GetProperty("etaSeconds").GetInt32() > 0);

        var stranger = await harness.CreateUserAsync();
        var theirs = await NearbyAsync(harness, harness.Tokens.Passenger(stranger), Fort);

        Assert.DoesNotContain(vehicle.ToString(), theirs.Keys);
    }

    /// <summary>
    /// An idle Mode C vehicle discloses no identity at all — US-7.4: "Standby on-demand vehicles do not
    /// show info when tapped."
    /// </summary>
    [Fact]
    public async Task An_idle_Mode_C_vehicle_discloses_neither_a_plate_nor_a_driver()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var vehicle = await harness.CreateVehicleAsync(driver, mode: "C", driverName: "Nimal Perera");

        await harness.Positions.PublishAsync(vehicle, Fort, mode: "C");

        var passenger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        var seen = vehicles[vehicle.ToString()];

        Assert.False(seen.TryGetProperty("driverName", out _));
        Assert.False(seen.TryGetProperty("registrationNumber", out _));
    }

    /// <summary>A Mode A bus is public infrastructure and its plate is on the popup (US-7.4, MAP-07).</summary>
    [Fact]
    public async Task A_Mode_A_bus_carries_its_registration_and_an_arrival_estimate()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var operatorId = await harness.CreateUserAsync("fleet_owner");
        var bus = await harness.CreateVehicleAsync(operatorId, mode: "A", vehicleType: "bus");

        await harness.Positions.PublishAsync(bus, GalleFace, mode: "A", vehicleType: "bus");

        var passenger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        var seen = vehicles[bus.ToString()];

        Assert.False(string.IsNullOrWhiteSpace(seen.GetProperty("registrationNumber").GetString()));

        // US-7.11's second half: "buses (Mode A) can also display ETA when selected on the map". The
        // target is the passenger's own map centre — the only destination the request names.
        Assert.True(seen.GetProperty("etaSeconds").GetInt32() > 0);

        // Still not the driver's name: US-7.12 gives that to an accepted ride only.
        Assert.False(seen.TryGetProperty("driverName", out _));
    }

    [Fact]
    public async Task A_vehicle_stale_beyond_the_freshness_window_is_dropped()
    {
        await using var harness = await QueryHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Query:FreshnessWindow"] = "00:01:00" });

        var driver = await harness.CreateUserAsync("driver");
        var fresh = await harness.CreateVehicleAsync(driver, mode: "C");
        var stale = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.Positions.PublishAsync(fresh, Fort, mode: "C");

        // A GNSS instant two minutes old. This is the shape a reconnecting device's replay backlog
        // arrives in as well, which is why the rule is about the capture instant and not arrival time.
        await harness.Positions.PublishAsync(
            stale, Fort, mode: "C", sampleTs: DateTimeOffset.UtcNow.AddMinutes(-2));

        var passenger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        Assert.Contains(fresh.ToString(), vehicles.Keys);
        Assert.DoesNotContain(stale.ToString(), vehicles.Keys);
    }

    /// <summary>
    /// US-7.17's other half: the EMQX last will. The mark is an <em>instant</em>, so a fresher sample
    /// brings the vehicle back with no <c>online</c> message needed — which matters because a device
    /// that crashed and restarted may never send one.
    /// </summary>
    [Fact]
    public async Task An_offline_mark_hides_a_vehicle_until_a_fresher_sample_arrives()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var vehicle = await harness.CreateVehicleAsync(driver, mode: "C");
        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        await harness.Positions.PublishAsync(
            vehicle, Fort, mode: "C", sampleTs: DateTimeOffset.UtcNow.AddSeconds(-10), seq: 1);

        await harness.Positions.MarkOfflineAsync(vehicle, DateTimeOffset.UtcNow.AddSeconds(-5));

        Assert.DoesNotContain(vehicle.ToString(), (await NearbyAsync(harness, bearer, Fort)).Keys);

        // A newer fix than the last will. `seq` must advance or the processor discards it as a replay.
        await harness.Positions.PublishAsync(vehicle, Fort, mode: "C", seq: 2);

        Assert.Contains(vehicle.ToString(), (await NearbyAsync(harness, bearer, Fort)).Keys);
    }

    [Fact]
    public async Task A_Mode_B_vehicle_is_visible_only_to_an_entitled_passenger()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var fleetOwner = await harness.CreateUserAsync("fleet_owner");
        var van = await harness.CreateVehicleAsync(fleetOwner, mode: "B", vehicleType: "van");

        await harness.Positions.PublishAsync(van, Fort, mode: "B", vehicleType: "van");

        var entitled = await harness.CreateUserAsync();
        var stranger = await harness.CreateUserAsync();

        // fanout-svc writes `share:{userId}` from `registry.events` (D-23). query-svc reads it.
        await harness.Positions.ShareAsync(entitled, van);

        Assert.Contains(
            van.ToString(),
            (await NearbyAsync(harness, harness.Tokens.Passenger(entitled), Fort)).Keys);

        Assert.DoesNotContain(
            van.ToString(),
            (await NearbyAsync(harness, harness.Tokens.Passenger(stranger), Fort)).Keys);
    }

    /// <summary>Definition of Done: "a type filter returns exactly the requested vehicle types including trains".</summary>
    [Fact]
    public async Task A_type_filter_returns_exactly_the_requested_types_including_trains()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var operatorId = await harness.CreateUserAsync("fleet_owner");
        var driver = await harness.CreateUserAsync("driver");

        var bus = await harness.CreateVehicleAsync(operatorId, mode: "A", vehicleType: "bus");
        var train = await harness.CreateVehicleAsync(operatorId, mode: "A", vehicleType: "train");
        var threeWheeler = await harness.CreateVehicleAsync(driver, mode: "C", vehicleType: "three_wheeler");

        await harness.Positions.PublishAsync(bus, Fort, mode: "A", vehicleType: "bus");
        await harness.Positions.PublishAsync(train, Fort, mode: "A", vehicleType: "train");
        await harness.Positions.PublishAsync(threeWheeler, Fort, mode: "C", vehicleType: "three_wheeler");

        var passenger = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Passenger(passenger);

        // `geo:live` has no expiry and the Redis fixture is shared across this collection, so every
        // assertion here is about *these three* vehicles rather than about the size of the answer.
        var mine = new[] { bus.ToString(), train.ToString(), threeWheeler.ToString() };

        var all = await NearbyAsync(harness, bearer, Fort);
        Assert.Equal(mine.Order().ToArray(), all.Keys.Intersect(mine).Order().ToArray());

        // Trains alone — the case US-7.7 calls out by name. `Only` is the load-bearing word: nothing
        // else this test put on the map may come back, whatever type it is.
        var trainsOnly = await NearbyAsync(harness, bearer, Fort, types: "train");
        Assert.Equal([train.ToString()], trainsOnly.Keys.Intersect(mine).ToArray());
        Assert.Equal("train", trainsOnly[train.ToString()].GetProperty("type").GetString());
        Assert.All(trainsOnly.Values, vehicle => Assert.Equal("train", vehicle.GetProperty("type").GetString()));

        // Two types at once, comma-separated as the contract's `explode: false` array.
        var publicTransport = await NearbyAsync(harness, bearer, Fort, types: "bus,train");
        Assert.Equal(
            new[] { bus.ToString(), train.ToString() }.Order().ToArray(),
            publicTransport.Keys.Intersect(mine).Order().ToArray());
        Assert.All(
            publicTransport.Values,
            vehicle => Assert.Contains(vehicle.GetProperty("type").GetString(), new[] { "bus", "train" }));

        // A mode filter narrows the same set independently.
        var modeConly = await NearbyAsync(harness, bearer, Fort, modes: "C");
        Assert.Equal([threeWheeler.ToString()], modeConly.Keys.Intersect(mine).ToArray());
        Assert.All(modeConly.Values, vehicle => Assert.Equal("C", vehicle.GetProperty("mode").GetString()));
    }

    /// <summary>
    /// The exact post-filter. <c>geo:live</c> has no per-member expiry and nothing removes a member, so
    /// the GEO index is a superset of the live fleet: a vehicle that stopped reporting stays in it at the
    /// place it stopped, and one that has moved is at whichever position was written last. Every
    /// candidate is therefore re-read from <c>veh:meta</c> and re-measured.
    /// </summary>
    [Fact]
    public async Task A_geo_index_member_with_no_position_hash_is_not_drawn()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var live = await harness.CreateVehicleAsync(driver, mode: "C");
        var stranded = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.Positions.PublishAsync(live, Fort, mode: "C");

        // In `geo:live` at Fort, with no `veh:meta` — a vehicle whose hash aged out ten minutes ago.
        await harness.Positions.StrandInGeoIndexAsync(stranded, Fort);

        var passenger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        Assert.Contains(live.ToString(), vehicles.Keys);
        Assert.DoesNotContain(stranded.ToString(), vehicles.Keys);
    }

    /// <summary>A vehicle outside the radius is not returned, whatever the GEO index rounds to.</summary>
    [Fact]
    public async Task A_vehicle_outside_the_radius_is_not_returned()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var driver = await harness.CreateUserAsync("driver");
        var near = await harness.CreateVehicleAsync(driver, mode: "C");
        var far = await harness.CreateVehicleAsync(driver, mode: "C");

        await harness.Positions.PublishAsync(near, GalleFace, mode: "C");
        await harness.Positions.PublishAsync(far, Kandy, mode: "C");

        var passenger = await harness.CreateUserAsync();
        var vehicles = await NearbyAsync(harness, harness.Tokens.Passenger(passenger), Fort);

        Assert.Contains(near.ToString(), vehicles.Keys);
        Assert.DoesNotContain(far.ToString(), vehicles.Keys);
    }

    /// <summary>
    /// ADD §12's resilience table: "Redis failure … query-svc returns <c>limited_live</c> flag". A
    /// passenger during a cache outage gets an empty map that says it is incomplete, not a 500.
    /// </summary>
    [Fact]
    public async Task A_snapshot_without_the_live_index_is_limited_live_rather_than_an_error()
    {
        await using var harness = await QueryHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                // A dead address rather than an empty one, so the failure is a connection refusal and
                // not a client that silently talks to whatever is on localhost.
                ["ConnectionStrings:Redis"] =
                    "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200",
            });

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(
            $"/v1/nearby?lat={Fort.Latitude}&lng={Fort.Longitude}", harness.Tokens.Passenger(passenger));

        Assert.True(body.GetProperty("limitedLive").GetBoolean());
        Assert.Empty(body.GetProperty("vehicles").EnumerateArray());
    }

    /// <summary>The flag is always present, so "no vehicles" and "we do not know" are distinguishable.</summary>
    [Fact]
    public async Task A_healthy_snapshot_says_so_explicitly()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();

        var body = await harness.GetJsonAsync(
            $"/v1/nearby?lat={Fort.Latitude}&lng={Fort.Longitude}", harness.Tokens.Passenger(passenger));

        Assert.False(body.GetProperty("limitedLive").GetBoolean());
        Assert.True(body.TryGetProperty("asOf", out _));
    }

    [Fact]
    public async Task A_radius_above_the_contract_ceiling_is_refused_rather_than_clamped()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        var passenger = await harness.CreateUserAsync();

        using var response = await harness.GetAsync(
            $"/v1/nearby?lat={Fort.Latitude}&lng={Fort.Longitude}&radius=999999",
            harness.Tokens.Passenger(passenger));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Nearby_requires_a_bearer()
    {
        await using var harness = await QueryHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync(
            $"/v1/nearby?lat={Fort.Latitude}&lng={Fort.Longitude}", bearer: null);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reads a snapshot and indexes it by vehicle id.</summary>
    private static async Task<Dictionary<string, JsonElement>> NearbyAsync(
        QueryHarness harness, string bearer, GeoPoint centre, string? types = null, string? modes = null)
    {
        var url = $"/v1/nearby?lat={centre.Latitude}&lng={centre.Longitude}&radius=3000";

        if (types is not null)
        {
            url += "&types=" + Uri.EscapeDataString(types);
        }

        if (modes is not null)
        {
            url += "&modes=" + Uri.EscapeDataString(modes);
        }

        var body = await harness.GetJsonAsync(url, bearer);

        return body.GetProperty("vehicles")
            .EnumerateArray()
            .ToDictionary(vehicle => vehicle.GetProperty("vehicleId").GetString()!, vehicle => vehicle);
    }
}
