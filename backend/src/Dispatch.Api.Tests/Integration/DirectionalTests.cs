using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// Directional Travel end to end (DT-01..DT-08, D5' §12) — the filter's whole life through the real
/// routes, the real predicate and the real durable timers.
/// </summary>
/// <remarks>
/// The pickup is Colombo Fort and every ride runs to Dehiwala, ~9.5 km south-south-east; a driver
/// heading to Panadura is going the same way and one heading to Negombo is going the other. The
/// arithmetic behind those words is asserted clause by clause in
/// <see cref="Domain.DirectionalPredicateTests"/>; what is under test here is that the filter
/// reaches the round at all, that it can only subtract from it, and that every way it ends leaves
/// the driver back in the full pool.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class DirectionalTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>~70 m from the pickup — well inside the 2 km detour ceiling.</summary>
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>~180 m from the pickup, so the two candidates are separable by distance.</summary>
    private static readonly GeoPoint AlsoNear = new(6.9360, 79.8430);

    /// <summary>Panadura — further down the same coast the ride is going.</summary>
    private static readonly GeoPoint SameWay = new(6.7132, 79.9026);

    /// <summary>Negombo — the opposite way.</summary>
    private static readonly GeoPoint OtherWay = new(7.2083, 79.8358);

    /// <summary>
    /// ~84 km due east. The ride to Dehiwala runs 75° off that heading, so the <em>bearing</em>
    /// clause is the only one that refuses it — it does still leave the driver ~2 km closer and the
    /// pickup is 70 m away. The one destination for which widening θ, and only widening θ, changes
    /// the answer.
    /// </summary>
    private static readonly GeoPoint AcrossTheGrain = new(6.9344, 80.6);

    // ------------------------------------------------------------------------------------------
    // DoD 1 — a ride heading away is filtered out, and candidate_scores says so
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>DoD 1.</b> The driver's filter points north, the ride runs south: they are dropped from
    /// the round, and the row an operator reads back carries the bearings and distances that
    /// decided it (DT-02, R-11).
    /// </summary>
    [Fact]
    public async Task A_ride_heading_away_from_the_destination_is_filtered_out_and_the_audit_says_why()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, OtherWay, "Home");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        // They cleared the H3 ring, the exact ST_DWithin post-filter and every hard gate. The
        // predicate is what stopped them — and it stopped them from *this round*, not from the pool.
        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.CandidateCount);
        Assert.Equal(0, outcome.EligibleCount);

        var scores = await harness.ReadScoresAsync(rideId);
        var row = Assert.Single(scores);

        using var document = JsonDocument.Parse(row.Breakdown);
        var breakdown = document.RootElement;

        Assert.Equal(EligibilityGates.Directional, breakdown.GetProperty("rejectedBy").GetString());

        var directional = breakdown.GetProperty("directional");

        Assert.False(directional.GetProperty("matched").GetBoolean());
        Assert.Equal(DirectionalClauses.Bearing, directional.GetProperty("failedOn").GetString());

        // The decision is reproducible from the row alone: the measurement, the threshold it was
        // judged against, and both bearings it was computed from (R-11 extended by DT-02).
        var bearingDiff = directional.GetProperty("bearingDiffDeg").GetDouble();
        var thetaMax = directional.GetProperty("thetaMaxDeg").GetInt32();

        Assert.True(bearingDiff > thetaMax);
        Assert.Equal(45, thetaMax);
        Assert.Equal(2_000, directional.GetProperty("detourMaxM").GetInt32());
        Assert.Equal(250, directional.GetProperty("progressMinM").GetInt32());

        Assert.Equal(
            Shared.Geo.GeoMath.AngularDifferenceDeg(
                directional.GetProperty("driverBearingDeg").GetDouble(),
                directional.GetProperty("rideBearingDeg").GetDouble()),
            bearingDiff,
            9);
    }

    /// <summary>
    /// The other half of the same predicate: a driver whose destination lies past the drop-off is
    /// kept, and the offer carries DT-08's <c>directionalMatched</c> badge.
    /// </summary>
    [Fact]
    public async Task A_ride_that_heads_the_drivers_way_is_offered_and_badged_as_directional()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay, "Home");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(driver.DriverId, outcome.DriverId);

        using var document = JsonDocument.Parse((await harness.ReadScoresAsync(rideId))[0].Breakdown);
        var directional = document.RootElement.GetProperty("directional");

        Assert.True(directional.GetProperty("matched").GetBoolean());
        Assert.False(directional.TryGetProperty("failedOn", out _));

        var offer = Assert.Single(
            await harness.ReadOutboxAsync(rideId), row => row.EventType == "offer.created");

        using var envelope = JsonDocument.Parse(offer.Payload);
        Assert.True(envelope.RootElement.GetProperty("directionalMatched").GetBoolean());
    }

    /// <summary>
    /// A driver with no filter is untouched by all of this: no <c>directional</c> member on their
    /// audit row and no badge on their offer. Most rounds are this one.
    /// </summary>
    [Fact]
    public async Task A_driver_with_no_filter_is_neither_filtered_nor_badged()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(driver.DriverId, outcome.DriverId);

        using var document = JsonDocument.Parse((await harness.ReadScoresAsync(rideId))[0].Breakdown);

        // Absent, not `"matched": true`: "no filter" and "the predicate ran and passed" are
        // different facts, and only one of them is this driver's.
        Assert.False(document.RootElement.TryGetProperty("directional", out _));

        var offer = Assert.Single(
            await harness.ReadOutboxAsync(rideId), row => row.EventType == "offer.created");

        using var envelope = JsonDocument.Parse(offer.Payload);
        Assert.False(envelope.RootElement.GetProperty("directionalMatched").GetBoolean());
    }

    // ------------------------------------------------------------------------------------------
    // Fences — DT-05 and DT-06
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>DT-05.</b> The predicate runs after the hard gates and can only remove. A driver a hard
    /// gate already refused reads as refused by that gate even though their filter matched
    /// perfectly — the directional clause never re-admits anybody.
    /// </summary>
    [Fact]
    public async Task The_predicate_never_overrules_a_hard_gate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        // A perfect directional match, and reputation-svc has delisted them.
        await harness.SetBlockStateAsync(driver.DriverId, "DELISTED");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);

        using var document = JsonDocument.Parse((await harness.ReadScoresAsync(rideId))[0].Breakdown);
        var breakdown = document.RootElement;

        Assert.Equal(EligibilityGates.BlockState, breakdown.GetProperty("rejectedBy").GetString());

        // The predicate still ran and still recorded what it saw — it simply is not what excluded
        // them, and the audit distinguishes the two.
        Assert.True(breakdown.GetProperty("directional").GetProperty("matched").GetBoolean());
    }

    /// <summary>
    /// <b>DT-06.</b> A filtered-out driver never blocks the ride: another driver standing beside
    /// them, with no filter, gets it. The passenger sees nothing at all.
    /// </summary>
    [Fact]
    public async Task A_filtered_driver_does_not_block_the_ride_from_matching_someone_else()
    {
        await using var harness = await StartAsync();

        var filtered = await harness.CreateOnlineDriverAsync(Nearest);
        var available = await harness.CreateOnlineDriverAsync(AlsoNear);

        await harness.SetDirectionalForAsync(filtered, OtherWay);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        // The filtered driver is nearer and would have won on score. The ride goes to the other one
        // rather than to nobody, which is the whole of DT-06.
        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(available.DriverId, outcome.DriverId);
        Assert.Equal(2, outcome.CandidateCount);
        Assert.Equal(1, outcome.EligibleCount);
    }

    /// <summary>
    /// <b>DT-06, the empty-pool case.</b> When the directional driver is the <em>only</em> candidate
    /// the ride behaves exactly as it does when nobody is near — it stays in Matching for the rest
    /// of the US-6A.11 window rather than being cancelled early, and no offer is ever made.
    /// </summary>
    [Fact]
    public async Task A_directional_driver_who_is_the_only_candidate_leaves_the_ride_matching()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, OtherWay);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, rideId);

        var ride = await harness.ReadRideAsync(rideId);

        Assert.Equal("Matching", ride.State);
        Assert.Null(ride.CurrentOfferId);

        // And the driver stays available: they were dropped from a round, not taken out of the pool.
        // A ride that suits them, placed a second later, still reaches them.
        var suitable = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var second = await OfferLoopTests.DispatchAsync(harness, suitable);

        Assert.Equal(DispatchResult.NoCandidate, second.Result);
        Assert.Equal(1, second.CandidateCount);
    }

    // ------------------------------------------------------------------------------------------
    // DoD 4 — no reputation or acceptance-rate consequence (US-6A.23)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>DoD 4.</b> A driver who sits out three rides because none of them suited their filter is
    /// not treated as having declined anything: no offer row, so their US-6A.14 acceptance rate is
    /// untouched, and no no-show, penalty or level movement anywhere.
    /// </summary>
    [Fact]
    public async Task Sitting_out_rides_on_a_filter_costs_the_driver_no_acceptance_rate_and_no_penalty()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        // One accepted ride first, so the acceptance rate is a real number rather than the "no
        // offers yet" default — a rate that starts and ends at 0 would prove nothing.
        var firstRide = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var first = await OfferLoopTests.DispatchAsync(harness, firstRide);

        Assert.Equal(DispatchResult.Offered, first.Result);
        await harness.AcceptOfferAsync(driver, firstRide, first.OfferId!.Value, first.Version!.Value);
        await ReturnToPoolAsync(harness, driver.DriverId);

        var before = await ReadStatsAsync(harness, driver);

        await harness.SetDirectionalForAsync(driver, OtherWay);

        for (var i = 0; i < 3; i++)
        {
            var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
            Assert.Equal(DispatchResult.NoCandidate, (await OfferLoopTests.DispatchAsync(harness, rideId)).Result);
        }

        var after = await ReadStatsAsync(harness, driver);

        Assert.Equal(before.GetProperty("acceptanceRate").GetDouble(), after.GetProperty("acceptanceRate").GetDouble());
        Assert.Equal(0, after.GetProperty("noShows").GetInt32());

        // The audit rows exist — they are how an operator answers "why did I get no rides" — but
        // nothing that costs the driver anything does.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.offers WHERE driver_id = @DriverId;",
                new { driver.DriverId }));

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.no_show_events WHERE driver_id = @DriverId;",
                new { driver.DriverId }));

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM reputation.block_states WHERE user_id = @DriverId AND state <> 'OK';",
                new { driver.DriverId }));
    }

    // ------------------------------------------------------------------------------------------
    // DoD 2 — the daily-use limit (DT-03, US-6A.18/6A.19)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>DoD 2.</b> Two activations a Colombo day, and the third is <c>409
    /// directional-limit-reached</c> — counted over activation rows, so turning each one off first
    /// does not buy another (US-6A.19's anti-gaming rule).
    /// </summary>
    [Fact]
    public async Task The_third_activation_in_one_colombo_day_is_refused_with_the_daily_limit_reason()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var first = await harness.SetDirectionalForAsync(driver, SameWay);
        Assert.Equal(1, first.GetProperty("usesRemaining").GetInt32());
        Assert.Equal(7_200, first.GetProperty("maxDurationSec").GetInt32());

        using (var off = await harness.ClearDirectionalAsync(driver))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);

            // The use is gone with the filter. This is the sentence US-6A.19 exists for.
            var body = await DispatchHarness.ReadJsonAsync(off);
            Assert.False(body.GetProperty("active").GetBoolean());
            Assert.Equal(1, body.GetProperty("usesRemaining").GetInt32());
        }

        var second = await harness.SetDirectionalForAsync(driver, SameWay);
        Assert.Equal(0, second.GetProperty("usesRemaining").GetInt32());

        await harness.ClearDirectionalAsync(driver);

        using var third = await harness.SetDirectionalAsync(driver, SameWay);

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(third);
        Assert.EndsWith("directional-limit-reached", problem.GetProperty("type").GetString(), StringComparison.Ordinal);

        // Refused means nothing was written: two activation rows, not three.
        Assert.Equal(2, (await harness.ReadDirectionalFiltersAsync(driver.DriverId)).Count);
    }

    /// <summary>
    /// The budget is per Asia/Colombo date (D-38), so yesterday's activations do not spend today's.
    /// </summary>
    [Fact]
    public async Task Yesterdays_activations_do_not_count_against_today()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        await harness.SetDirectionalForAsync(driver, SameWay);
        await harness.ClearDirectionalAsync(driver);
        await harness.SetDirectionalForAsync(driver, SameWay);
        await harness.ClearDirectionalAsync(driver);

        await harness.BackdateDirectionalUsesAsync(driver.DriverId, days: 1);

        var fresh = await harness.SetDirectionalForAsync(driver, SameWay);
        Assert.Equal(1, fresh.GetProperty("usesRemaining").GetInt32());
    }

    /// <summary>A second filter is refused rather than silently replacing the live one.</summary>
    [Fact]
    public async Task Setting_a_second_filter_while_one_is_live_is_a_conflict()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        using var second = await harness.SetDirectionalAsync(driver, OtherWay);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // And the live one is untouched — a refused request must not have moved the destination.
        var live = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));
        Assert.Equal(SameWay.Latitude, live.DestLat, 4);
        Assert.Null(live.ClearedAt);
    }

    /// <summary>A driver who is not on standby has nothing to filter, so nothing is spent.</summary>
    [Fact]
    public async Task An_offline_driver_cannot_set_a_filter()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.SetDirectionalAsync(driver, SameWay);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(response);
        Assert.EndsWith("not-online", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Empty(await harness.ReadDirectionalFiltersAsync(driver.DriverId));
    }

    // ------------------------------------------------------------------------------------------
    // DT-08 — the live state the driver's banner is drawn from
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_state_route_reports_the_destination_the_time_left_and_the_uses_left()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using (var empty = await harness.GetDirectionalAsync(driver))
        {
            var idle = await DispatchHarness.ReadJsonAsync(empty);

            Assert.False(idle.GetProperty("active").GetBoolean());
            Assert.Equal(0, idle.GetProperty("timeRemainingSec").GetInt32());
            Assert.Equal(2, idle.GetProperty("usesRemaining").GetInt32());
        }

        await harness.SetDirectionalForAsync(driver, SameWay, "Home");

        using var response = await harness.GetDirectionalAsync(driver);
        var state = await DispatchHarness.ReadJsonAsync(response);

        Assert.True(state.GetProperty("active").GetBoolean());
        Assert.Equal("Home", state.GetProperty("label").GetString());
        Assert.Equal(SameWay.Latitude, state.GetProperty("destination").GetProperty("lat").GetDouble(), 4);
        Assert.Equal(1, state.GetProperty("usesRemaining").GetInt32());

        // The 2 h duration, less however long this test took to get here.
        Assert.InRange(state.GetProperty("timeRemainingSec").GetInt32(), 7_100, 7_200);
    }

    /// <summary>DT-01's Redis keys: the hint with the filter's own TTL, and the day's counter.</summary>
    [Fact]
    public async Task Setting_a_filter_writes_the_redis_hint_with_its_remaining_ttl_and_the_day_counter()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay, "Home");

        var db = harness.Redis.GetDatabase();
        var key = RedisKeys.DriverDirectional(driver.DriverId);

        var hash = await db.HashGetAllAsync(key);
        Assert.NotEmpty(hash);

        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(115), TimeSpan.FromHours(2));

        var uses = RedisKeys.DriverDirectionalUses(driver.DriverId, BusinessCalendar.Today(TimeProvider.System));
        Assert.Equal("1", (await db.StringGetAsync(uses)).ToString());
        Assert.InRange(
            (await db.KeyTimeToLiveAsync(uses))!.Value, TimeSpan.FromHours(35), TimeSpan.FromHours(36));

        // And the hint goes when the filter does.
        await harness.ClearDirectionalAsync(driver);
        Assert.False(await db.KeyExistsAsync(key));
    }

    // ------------------------------------------------------------------------------------------
    // DoD 3 and DT-04 — every way a filter ends
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>DoD 3.</b> <c>POST /v1/standby/offline</c> clears the active filter, marks it
    /// <c>offline</c> and emits <c>directional.cleared</c>.
    /// </summary>
    [Fact]
    public async Task Going_offline_clears_the_filter_and_emits_directional_cleared()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        using (var offline = await harness.GoOfflineAsync(driver))
        {
            Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        }

        var filter = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));

        Assert.NotNull(filter.ClearedAt);
        Assert.Equal(DirectionalClearReasons.Offline, filter.ClearedReason);

        var cleared = Assert.Single(
            await harness.ReadOutboxAsync(driver.DriverId), row => row.EventType == "directional.cleared");

        using var envelope = JsonDocument.Parse(cleared.Payload);
        var payload = envelope.RootElement;

        Assert.Equal(driver.DriverId, payload.GetProperty("driverId").GetGuid());
        Assert.Equal(filter.Id, payload.GetProperty("filterId").GetGuid());
        Assert.Equal(DirectionalClearReasons.Offline, payload.GetProperty("reason").GetString());

        // The use it consumed is not refunded by going offline either.
        Assert.Equal(1, payload.GetProperty("usesRemaining").GetInt32());

        // The partition key is the driver, because this event is about them and not about a ride.
        Assert.Equal(driver.DriverId, cleared.AggregateId);
    }

    /// <summary>
    /// DT-04's durable expiry: the <c>dispatch.timers</c> row is the source of truth, and firing it
    /// puts the driver back in the full eligible pool.
    /// </summary>
    [Fact]
    public async Task The_durable_expiry_timer_clears_the_filter_and_returns_the_driver_to_the_pool()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, OtherWay);

        var armed = await harness.ReadDriverTimersAsync(driver.DriverId);

        Assert.Contains(armed, t => t.Kind == DispatchTimerKinds.DirectionalExpiry && t.FiredAt is null);
        Assert.Contains(armed, t => t.Kind == DispatchTimerKinds.DirectionalReminder && t.FiredAt is null);

        // The reminder is armed 10 minutes ahead of the expiry (US-10.14), not at it.
        var expiry = armed.Single(t => t.Kind == DispatchTimerKinds.DirectionalExpiry);
        var reminder = armed.Single(t => t.Kind == DispatchTimerKinds.DirectionalReminder);

        Assert.InRange(
            expiry.FireAt - reminder.FireAt,
            TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        await harness.DueDirectionalTimerAsync(
            driver.DriverId, DispatchTimerKinds.DirectionalExpiry, expireFilter: true);

        Assert.Equal(1, await harness.SweepDispatchTimersAsync());

        var filter = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));
        Assert.Equal(DirectionalClearReasons.Expiry, filter.ClearedReason);

        Assert.Single(await harness.ReadOutboxAsync(driver.DriverId), row => row.EventType == "directional.cleared");

        // The reminder was retired with it rather than left to fire nine minutes later at a filter
        // that no longer exists.
        Assert.All(await harness.ReadDriverTimersAsync(driver.DriverId), timer => Assert.NotNull(timer.FiredAt));

        // And the ride that was filtered out a moment ago now reaches them.
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(driver.DriverId, outcome.DriverId);
    }

    /// <summary>
    /// DT-08's 10-minute warning: the reminder timer hands <c>DIRECTIONAL_EXPIRING</c> to
    /// notification-svc and leaves the filter alone.
    /// </summary>
    [Fact]
    public async Task The_pre_expiry_reminder_hands_off_to_notification_svc_without_clearing_anything()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        await harness.DueDirectionalTimerAsync(driver.DriverId, DispatchTimerKinds.DirectionalReminder);
        Assert.Equal(1, await harness.SweepDispatchTimersAsync());

        var reminder = Assert.Single(
            await harness.ReadOutboxAsync(driver.DriverId), row => row.EventType == "directional.expiring");

        using var envelope = JsonDocument.Parse(reminder.Payload);
        var payload = envelope.RootElement;

        Assert.Equal("DIRECTIONAL_EXPIRING", payload.GetProperty("notificationType").GetString());
        Assert.Equal(driver.DriverId, payload.GetProperty("driverId").GetGuid());
        Assert.True(payload.GetProperty("minutesRemaining").GetInt32() > 0);

        // A warning, not an ending: the filter is still live and still filtering.
        var filter = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));
        Assert.Null(filter.ClearedAt);

        using var state = await harness.GetDirectionalAsync(driver);
        Assert.True((await DispatchHarness.ReadJsonAsync(state)).GetProperty("active").GetBoolean());
    }

    /// <summary>
    /// Every path into the clear is the same conditional UPDATE, so a filter that expires while the
    /// driver is going offline is cleared once and announced once.
    /// </summary>
    [Fact]
    public async Task A_filter_that_clears_twice_over_emits_one_event()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        await harness.GoOfflineAsync(driver);

        await harness.DueDirectionalTimerAsync(
            driver.DriverId, DispatchTimerKinds.DirectionalExpiry, expireFilter: true);

        await harness.SweepDispatchTimersAsync();

        var filter = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));

        // First writer wins the reason, and the expiry that arrived second changed nothing.
        Assert.Equal(DirectionalClearReasons.Offline, filter.ClearedReason);

        Assert.Single(await harness.ReadOutboxAsync(driver.DriverId), row => row.EventType == "directional.cleared");
    }

    /// <summary>Turning off a filter nobody set is the contract's 404, not a silent success.</summary>
    [Fact]
    public async Task Turning_off_a_filter_that_is_not_there_is_a_404()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var response = await harness.ClearDirectionalAsync(driver);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // The admin surface (DT-02, DT-03)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>PUT /v1/admin/dispatch/directional-config</c> changes the predicate for the very next
    /// round — the parameters live in a table precisely so no replica has to be restarted.
    /// </summary>
    [Fact]
    public async Task Widening_theta_max_admits_a_ride_the_previous_round_filtered_out()
    {
        await using var harness = await StartAsync();

        var admin = harness.Tokens.Admin(Guid.NewGuid());
        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        await harness.SetDirectionalForAsync(driver, AcrossTheGrain);

        var before = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var refused = await OfferLoopTests.DispatchAsync(harness, before);

        Assert.Equal(DispatchResult.NoCandidate, refused.Result);

        // The bearing, and only the bearing, is what refused it — so the PUT below is the only
        // thing that can change the outcome.
        using (var audit = JsonDocument.Parse((await harness.ReadScoresAsync(before))[0].Breakdown))
        {
            Assert.Equal(
                DirectionalClauses.Bearing,
                audit.RootElement.GetProperty("directional").GetProperty("failedOn").GetString());
        }

        using (var response = await harness.PutAsync(
            "/v1/admin/dispatch/directional-config", new { thetaMaxDeg = 180 }, admin))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var config = await DispatchHarness.ReadJsonAsync(response);

            Assert.Equal(180, config.GetProperty("thetaMaxDeg").GetInt32());

            // A partial PUT keeps the other five rather than resetting them to defaults.
            Assert.Equal(2_000, config.GetProperty("detourMaxM").GetInt32());
            Assert.Equal(250, config.GetProperty("progressMinM").GetInt32());
            Assert.Equal(2, config.GetProperty("maxUsesPerDay").GetInt32());
            Assert.Equal(7_200, config.GetProperty("maxDurationSec").GetInt32());
            Assert.False(config.GetProperty("clearOnFirstTrip").GetBoolean());
        }

        var after = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, after);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(driver.DriverId, outcome.DriverId);
    }

    [Fact]
    public async Task The_daily_limit_is_whatever_the_admin_configured()
    {
        await using var harness = await StartAsync();

        var admin = harness.Tokens.Admin(Guid.NewGuid());
        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using (var response = await harness.PutAsync(
            "/v1/admin/dispatch/directional-config", new { maxUsesPerDay = 1 }, admin))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var first = await harness.SetDirectionalForAsync(driver, SameWay);
        Assert.Equal(0, first.GetProperty("usesRemaining").GetInt32());

        await harness.ClearDirectionalAsync(driver);

        using var second = await harness.SetDirectionalAsync(driver, SameWay);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_driver_cannot_reconfigure_the_predicate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var response = await harness.PutAsync(
            "/v1/admin/dispatch/directional-config", new { thetaMaxDeg = 180 }, driver.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_impossible_angle_is_refused()
    {
        await using var harness = await StartAsync();

        using var response = await harness.PutAsync(
            "/v1/admin/dispatch/directional-config",
            new { thetaMaxDeg = 400 },
            harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// <c>clear_on_first_trip</c> is off by default and does what it says when an admin turns it on
    /// — on the accept, not on the offer.
    /// </summary>
    [Fact]
    public async Task Clear_on_first_trip_ends_the_filter_when_the_driver_accepts_a_ride()
    {
        await using var harness = await StartAsync();

        using (var response = await harness.PutAsync(
            "/v1/admin/dispatch/directional-config",
            new { clearOnFirstTrip = true },
            harness.Tokens.Admin(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDirectionalForAsync(driver, SameWay);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        // Still live while the offer is only an offer — a filter that ended on a ride the driver
        // went on to decline would be spent on nothing.
        Assert.Null((await harness.ReadDirectionalFiltersAsync(driver.DriverId))[0].ClearedAt);

        await harness.AcceptOfferAsync(driver, rideId, outcome.OfferId!.Value, outcome.Version!.Value);
        await MarkAcceptedAsync(harness, rideId, driver.DriverId);

        var filter = Assert.Single(await harness.ReadDirectionalFiltersAsync(driver.DriverId));
        Assert.Equal(DirectionalClearReasons.FirstMatchedTrip, filter.ClearedReason);
    }

    // ------------------------------------------------------------------------------------------

    private static async Task<JsonElement> ReadStatsAsync(DispatchHarness harness, SeededDriver driver)
    {
        using var response = await harness.GetAsync($"/v1/drivers/{driver.DriverId}/stats", driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await DispatchHarness.ReadJsonAsync(response);
    }

    private static async Task MarkAcceptedAsync(DispatchHarness harness, Guid rideId, Guid driverId)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IDispatchService>()
            .MarkAcceptedAsync(rideId, driverId, TestContext.Current.CancellationToken);
    }

    private static async Task ReturnToPoolAsync(DispatchHarness harness, Guid driverId)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IDispatchService>()
            .ReturnToPoolAsync(driverId, TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
