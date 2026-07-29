using MageRide.Shared.Geo;
using System.Text.Json;
using Dapper;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 2 — "a ride request produces exactly one offer to the nearest eligible driver"</b> and
/// <b>DoD 4 — "the exact-distance post-filter runs after the H3 pre-filter (the cell is never a
/// distance bound)".</b>
/// </summary>
[Collection<DispatchCollection>]
public sealed class OfferLoopTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Pickup = DispatchHarness.Pickup;

    /// <summary>~70 m from the pickup. Same res-5 cell.</summary>
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>~4.4 km from the pickup, one ring out. Inside the 5 km search radius.</summary>
    private static readonly GeoPoint FourKm = new(6.9700, 79.8600);

    /// <summary>
    /// ~15 km from the pickup and — the point of it — in the <b>same res-5 cell as the pickup</b>.
    /// The H3 pre-filter cannot tell this driver from the one 70 m away.
    /// </summary>
    private static readonly GeoPoint SameCellFifteenKm = new(6.8000, 79.8428);

    /// <summary>~22 km from the pickup, at ring 2. Inside the pre-filter, far outside the radius.</summary>
    private static readonly GeoPoint RingTwoTwentyTwoKm = new(7.1344, 79.8428);

    [Fact]
    public async Task A_ride_request_produces_exactly_one_offer_to_the_nearest_driver()
    {
        await using var harness = await StartAsync();

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var far = await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(near.DriverId, outcome.DriverId);

        // Exactly one, not "at least one". R-12 Phase 1 is sequential matching.
        await using var connection = await harness.OpenAsync();
        var offers = await connection.QueryAsync<OfferSummary>(
            "SELECT id AS Id, driver_id AS DriverId, status AS Status FROM dispatch.offers WHERE ride_id = @RideId;",
            new { RideId = rideId });

        var only = Assert.Single(offers);
        Assert.Equal(near.DriverId, only.DriverId);
        Assert.Equal(OfferStatuses.Offered, only.Status);
        Assert.Equal(outcome.OfferId, only.Id);

        // And the second-nearest driver is untouched — still AVAILABLE, still in the index.
        Assert.Equal(
            PresenceStates.Available,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { far.DriverId }));
    }

    /// <summary>
    /// <b>DoD 4.</b> The far driver is in the pickup's <em>own</em> res-5 cell — the H3 pre-filter
    /// returns them from the same Redis key as the near driver — and is 15 km away. If the cell
    /// were treated as a distance bound they would be a candidate; the mandatory
    /// <c>ST_DWithin</c> post-filter is the only thing that removes them.
    /// </summary>
    [Fact]
    public async Task A_driver_in_the_pickups_own_cell_but_15_km_away_is_removed_by_the_exact_post_filter()
    {
        await using var harness = await StartAsync();

        var distant = await harness.CreateOnlineDriverAsync(SameCellFifteenKm);

        // Same key: this is what makes the assertion about the post-filter and not about H3.
        var grid = new H3Grid(5, 2);
        Assert.Equal(grid.CellAt(Pickup), grid.CellAt(SameCellFifteenKm));

        var db = harness.Redis.GetDatabase();
        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(Pickup)), distant.DriverId.ToString()));

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);

        // The pre-filter DID return them — that is the whole point. The post-filter is what said no.
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(0, outcome.CandidateCount);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.offers WHERE ride_id = @RideId;", new { RideId = rideId }));
    }

    [Fact]
    public async Task A_driver_two_rings_out_is_pre_filtered_in_and_post_filtered_out()
    {
        await using var harness = await StartAsync();

        var distant = await harness.CreateOnlineDriverAsync(RingTwoTwentyTwoKm);

        var grid = new H3Grid(5, 2);
        Assert.Contains(grid.CellAt(RingTwoTwentyTwoKm), grid.DiskAt(Pickup));
        Assert.NotEqual(grid.CellAt(Pickup), grid.CellAt(RingTwoTwentyTwoKm));

        var outcome = await DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(0, outcome.CandidateCount);
        Assert.NotEqual(distant.DriverId, outcome.DriverId);
    }

    [Fact]
    public async Task Widening_the_search_radius_brings_the_same_far_driver_back()
    {
        // The mirror image of the two tests above: nothing about the driver changed, only the
        // radius the post-filter applies — which is what proves the post-filter is what decides.
        await using var harness = await StartAsync(
            new Dictionary<string, string?> { ["Dispatch:SearchRadiusM"] = "20000" });

        var distant = await harness.CreateOnlineDriverAsync(SameCellFifteenKm);

        var outcome = await DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(distant.DriverId, outcome.DriverId);
    }

    [Fact]
    public async Task The_offer_moves_the_ride_to_Offered_and_arms_a_15_second_window()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var before = DateTimeOffset.UtcNow;
        var outcome = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        // ride-svc owns rides.state; dispatch only asked.
        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("Offered", ride.State);
        Assert.Equal(outcome.OfferId, ride.CurrentOfferId);
        Assert.Equal(driver.DriverId, ride.OfferedDriverId);
        Assert.Equal(outcome.Version, ride.Version);

        // D5' §3.5 / US-6A.3: the window is 15 seconds. Measured against the deadline ride-svc
        // stamped, because ride-svc's clock is the one that decides an accept (ADD §11.11).
        var window = ride.OfferExpiresAt!.Value - before;
        Assert.InRange(window, TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(16));

        // ...and the option that produced it really is 15 s by default, not by test configuration.
        var options = harness.Services.GetRequiredService<IOptions<DispatchOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(15), options.OfferTtl);
    }

    [Fact]
    public async Task The_offer_mirror_the_backstop_and_the_Redis_hint_all_agree_on_one_deadline()
    {
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        var authoritative = (await harness.ReadRideAsync(rideId)).OfferExpiresAt!.Value;

        await using var connection = await harness.OpenAsync();

        // dispatch.offers mirrors ride-svc's instant, not its own guess.
        var mirrored = await connection.ExecuteScalarAsync<DateTimeOffset>(
            "SELECT expires_at FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId });
        Assert.Equal(authoritative, mirrored, TimeSpan.FromMilliseconds(1));

        // R-04: a durable rides.timers row exists, armed just after the deadline.
        var timer = await connection.QuerySingleAsync<TimerRow>(
            """
            SELECT fire_at AS FireAt, fired_at AS FiredAt, payload::text AS Payload
              FROM rides.timers WHERE ride_id = @RideId AND kind = 'offer_expiry';
            """,
            new { RideId = rideId });

        Assert.Null(timer.FiredAt);
        Assert.InRange(timer.FireAt - authoritative, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        using var payload = JsonDocument.Parse(timer.Payload);
        Assert.Equal(outcome.OfferId, payload.RootElement.GetProperty("offerId").GetGuid());
        Assert.Equal(outcome.DriverId, payload.RootElement.GetProperty("driverId").GetGuid());

        // D-07: the Redis hint carries the same instant and a matching TTL.
        var hint = await harness.Services.GetRequiredService<MageRide.Dispatch.Redis.IDriverIndex>()
            .ReadOfferAsync(rideId, TestContext.Current.CancellationToken);

        Assert.NotNull(hint);
        Assert.Equal(outcome.OfferId, hint.OfferId);
        Assert.Equal(authoritative, hint.ExpiresAt, TimeSpan.FromMilliseconds(1));

        var ttl = await harness.Redis.GetDatabase().KeyTimeToLiveAsync(RedisKeys.Offer(rideId));
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.Zero, TimeSpan.FromSeconds(16));
    }

    [Fact]
    public async Task An_offered_driver_leaves_the_candidate_pool()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        await DispatchAsync(harness, rideId);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            PresenceStates.Offered,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));

        // ADD §9.4: only AVAILABLE drivers belong in geo:drivers:available.
        var cell = new H3Grid(5, 2).CellAt(Nearest);
        Assert.Null(await harness.Redis.GetDatabase().SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", cell), driver.DriverId.ToString()));

        // R-10's fast path holds the driver.
        Assert.True(await harness.Redis.GetDatabase().KeyExistsAsync(
            RedisKeys.DriverOfferLock(driver.DriverId)));
    }

    [Fact]
    public async Task Offer_created_is_written_to_the_dispatch_outbox_with_the_ride_version()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        await using var connection = await harness.OpenAsync();
        var row = await connection.QuerySingleAsync<OutboxRowText>(
            """
            SELECT event_type AS EventType, aggregate_id AS AggregateId,
                   payload::text AS Payload, dispatched_at AS DispatchedAt
              FROM dispatch.outbox WHERE aggregate_id = @RideId;
            """,
            new { RideId = rideId });

        Assert.Equal("offer.created", row.EventType);
        Assert.Equal(rideId, row.AggregateId);
        Assert.Null(row.DispatchedAt);       // the dispatcher is off in this harness

        using var payload = JsonDocument.Parse(row.Payload);
        var e = payload.RootElement;

        Assert.Equal("offer.created", e.GetProperty("eventType").GetString());
        Assert.Equal(rideId, e.GetProperty("rideId").GetGuid());
        Assert.Equal(outcome.OfferId, e.GetProperty("offerId").GetGuid());
        Assert.Equal(driver.DriverId, e.GetProperty("driverId").GetGuid());
        Assert.Equal("cash", e.GetProperty("paymentMethod").GetString());
        Assert.Equal("LKR", e.GetProperty("currency").GetString());
        Assert.Equal(74_000, e.GetProperty("fareEstimateMinor").GetInt64());
        Assert.False(e.GetProperty("directionalMatched").GetBoolean());
        Assert.False(e.GetProperty("isPackage").GetBoolean());

        // The C022 handoff's ask: carry the version so OfferSession.accept() need not spend a
        // GET /v1/rides/{id}/state inside a 15-second window (C013 note 6).
        Assert.Equal(outcome.Version, e.GetProperty("version").GetInt64());
        Assert.Equal((await harness.ReadRideAsync(rideId)).Version, e.GetProperty("version").GetInt64());

        // And the distance the driver app renders on the offer card.
        Assert.InRange(e.GetProperty("distanceToPickupM").GetInt32(), 0, 200);
    }

    [Fact]
    public async Task Every_candidate_considered_is_written_to_the_R_11_scoring_audit()
    {
        await using var harness = await StartAsync();

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var far = await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await DispatchAsync(harness, rideId);

        var scores = await harness.ReadScoresAsync(rideId);

        // R-11: "written for every scored candidate, not only the winner".
        Assert.Equal(2, scores.Count);
        Assert.Equal(near.DriverId, scores[0].DriverId);
        Assert.Equal(far.DriverId, scores[1].DriverId);
        Assert.True(scores[0].Score > scores[1].Score);

        // Version 1 is the D5' §3.3 weighted algorithm C034 landed. The number is on every row so a
        // decision taken under a later formula is never reproduced with today's.
        Assert.All(scores, s => Assert.Equal(1, s.Version));

        using var breakdown = JsonDocument.Parse(scores[0].Breakdown);
        Assert.Equal("weighted-v1", breakdown.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(0, breakdown.RootElement.GetProperty("rank").GetInt32());
        Assert.InRange(breakdown.RootElement.GetProperty("distanceM").GetDouble(), 0, 200);

        // A passenger ride consulted no package table, and 0703's own comment says NULL is what
        // that means — a stored `true` would claim the P-11 gate had run and agreed.
        Assert.All(scores, s => Assert.Null(s.PackageSizeCompatible));
    }

    [Fact]
    public async Task A_driver_on_the_wrong_tier_is_never_a_candidate()
    {
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest, vehicleType: "sedan");

        var rideId = await harness.RequestRideAsync(
            await harness.CreatePassengerAsync(), vehicleType: "three_wheeler");

        var outcome = await DispatchAsync(harness, rideId);

        // The tier is part of the index key, so the pre-filter never even returns them.
        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(0, outcome.PreFilterCount);
    }

    [Fact]
    public async Task A_driver_who_went_offline_is_never_a_candidate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.GoOfflineAsync(driver);

        var outcome = await DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
    }

    /// <summary>
    /// D5' §3.2's GPS-freshness gate. The Redis hash has a 60 s TTL; the durable presence row has
    /// none, so without this check a driver whose phone died an hour ago would still be offered
    /// rides from the row the post-filter reads.
    /// </summary>
    [Fact]
    public async Task A_driver_whose_position_has_gone_stale_is_not_a_candidate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE dispatch.driver_presence SET last_seen_at = now() - interval '10 minutes' WHERE driver_id = @DriverId;",
                new { driver.DriverId });
        }

        var outcome = await DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.PreFilterCount);      // still indexed…
        Assert.Equal(0, outcome.CandidateCount);      // …and still refused
    }

    [Fact]
    public async Task With_nobody_online_the_ride_reaches_Matching_and_stops_there()
    {
        await using var harness = await StartAsync();

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);

        // Matching, not Offered and not stuck in Requested: the passenger's app shows "searching",
        // which is the truth.
        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("Matching", ride.State);
        Assert.Null(ride.CurrentOfferId);
    }

    [Fact]
    public async Task Dispatching_the_same_ride_twice_does_not_produce_a_second_offer()
    {
        // D6' §2.3 delivery is at-least-once, so ride.requested arriving twice must not put two
        // drivers en route. ride-svc's `state = 'Matching'` predicate is what refuses the second.
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest);
        await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var first = await DispatchAsync(harness, rideId);
        var second = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, first.Result);
        Assert.Equal(DispatchResult.RideNotDispatchable, second.Result);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.offers WHERE ride_id = @RideId AND status = 'OFFERED';",
                new { RideId = rideId }));

        // The second attempt reserved the 4 km driver, was refused by ride-svc, and unwound: no
        // stray OFFERED row, no driver left out of the pool, no orphan lock.
        var busy = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM dispatch.driver_presence WHERE state = 'OFFERED';");
        Assert.Equal(1, busy);
    }

    [Fact]
    public async Task Without_the_internal_key_presence_still_works_but_nothing_is_offered()
    {
        // The failure this warns about at start-up: from the outside it looks like "nobody is
        // online", which is the most expensive possible way to be misconfigured.
        await using var harness = await StartAsync(
            new Dictionary<string, string?> { ["Dispatch:RideServiceInternalKey"] = string.Empty });

        await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.RideNotDispatchable, outcome.Result);

        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("Requested", ride.State);
    }

    // -----------------------------------------------------------------------------------------

    internal static async Task<DispatchOutcome> DispatchAsync(DispatchHarness harness, Guid rideId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        return await dispatch.BeginAsync(
            await BuildRequestAsync(harness, rideId), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds what <c>ride.requested</c> carries, from the ride the real ride-svc just booked.
    /// The broker-backed path is <see cref="EventPipelineTests"/>; every other test drives the
    /// same entry point directly so a background consumer cannot race the assertion.
    /// </summary>
    internal static async Task<RideDispatchRequest> BuildRequestAsync(DispatchHarness harness, Guid rideId)
    {
        await using var connection = await harness.OpenAsync();

        var row = await connection.QuerySingleAsync<BookedRide>(
            """
            SELECT ST_Y(pickup_geo::geometry) AS Lat, ST_X(pickup_geo::geometry) AS Lng,
                   ST_Y(dropoff_geo::geometry) AS DropoffLat, ST_X(dropoff_geo::geometry) AS DropoffLng,
                   vehicle_type AS VehicleType, payment_method AS PaymentMethod,
                   fare_estimate_minor AS FareEstimateMinor, currency AS Currency,
                   passenger_id AS PassengerId, package_size AS PackageSize,
                   -- 0=passenger, 1=proxy, 2=package (ck_rides_kind, migration 0601). The event
                   -- payload carries the name, so the test builds the same value the envelope would.
                   CASE kind WHEN 1 THEN 'proxy' WHEN 2 THEN 'package' ELSE 'passenger' END AS Kind
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });

        return new RideDispatchRequest(
            rideId, new GeoPoint(row.Lat, row.Lng), row.VehicleType, row.PaymentMethod,
            row.FareEstimateMinor, row.Currency, row.PassengerId, row.Kind, row.PackageSize,

            // Δ C036: `ride.requested` has always carried the drop-off (D6' §2.2) and the DT-02
            // predicate is the first thing in this service to need it. Read from the same row as
            // the pickup so the request a test drives is the one the envelope would have produced.
            new GeoPoint(row.DropoffLat, row.DropoffLng));
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }

    private sealed record OfferSummary(Guid Id, Guid DriverId, string Status);

    private sealed record TimerRow(DateTimeOffset FireAt, DateTimeOffset? FiredAt, string Payload);

    private sealed record OutboxRowText(
        string EventType, Guid AggregateId, string Payload, DateTimeOffset? DispatchedAt);

    private sealed record BookedRide(
        double Lat,
        double Lng,
        double DropoffLat,
        double DropoffLng,
        string VehicleType,
        string PaymentMethod,
        long? FareEstimateMinor,
        string Currency,
        Guid PassengerId,
        string? PackageSize,
        string Kind);
}
