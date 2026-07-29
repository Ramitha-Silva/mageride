using System.Net;
using System.Text.Json;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// The D-06 Job Board, its intents and the D5' §3.7 T-30 dispatch (US-6A.5, US-6A.8, US-6A.15).
/// </summary>
[Collection<DispatchCollection>]
public sealed class JobBoardTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>Colombo Fort, where every booking's pickup is.</summary>
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>~1.2 km further out — same neighbourhood, measurably further from the pickup.</summary>
    private static readonly GeoPoint Further = new(6.9450, 79.8480);

    /// <summary>Kandy, ~93 km inland. Well outside the 30 km board.</summary>
    private static readonly GeoPoint FarAway = new(7.2906, 80.6337);

    [Fact]
    public async Task The_board_shows_bookings_within_30_km_and_hides_the_rest()
    {
        await using var harness = await StartAsync();

        var soon = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(3));

        // A second booking whose pickup is in Kandy: on the board for a Kandy driver, not for
        // this one. It is the ST_DWithin that decides, not the H3 ring — this service never uses
        // an H3 cell as a distance bound (R-06).
        await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(3), pickup: FarAway);

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var response = await harness.JobBoardAsync(driver, Nearest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await DispatchHarness.ReadJsonAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(soon.ToString(), items[0].GetProperty("scheduledRideId").GetString());

        // The card carries the distance the driver is deciding on, measured from where they asked.
        Assert.InRange(items[0].GetProperty("distanceM").GetInt32(), 0, 30_000);
        Assert.Equal(0, items[0].GetProperty("intentCount").GetInt32());
        Assert.False(items[0].GetProperty("hasIntent").GetBoolean());
    }

    /// <summary>
    /// <b>Definition of Done.</b> US-6A.8: Level 1 loses the Job Board and scheduled-ride
    /// privileges, on both routes, with a reason that says it is not a ban.
    /// </summary>
    [Fact]
    public async Task A_level_1_driver_is_refused_the_board_and_the_intent_route_with_a_clear_reason()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(3));

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetDriverLevelAsync(driver.DriverId, 1);

        using var board = await harness.JobBoardAsync(driver, Nearest);
        Assert.Equal(HttpStatusCode.Forbidden, board.StatusCode);

        var problem = await DispatchHarness.ReadJsonAsync(board);
        var detail = problem.GetProperty("detail").GetString()!;

        // US-6A.8 is explicit that this is not a ban, so the 403 says what is still available and
        // how the level comes back — an error a support agent can read out loud.
        Assert.Contains("Level 1", detail, StringComparison.Ordinal);
        Assert.Contains("not a ban", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("immediate Mode C", detail, StringComparison.Ordinal);

        using var intent = await harness.PostIntentAsync(driver, scheduledRideId);
        Assert.Equal(HttpStatusCode.Forbidden, intent.StatusCode);

        // A Level-2 driver keeps both: the gate is `< job_board_min_level`, which is 2 by default.
        await harness.SetDriverLevelAsync(driver.DriverId, 2);

        using var allowed = await harness.JobBoardAsync(driver, Nearest);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>US-6A.5: one intent per driver per ride; re-posting is a replay, not a second row.</summary>
    [Fact]
    public async Task Posting_intent_twice_records_one_intent()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddHours(3));

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var first = await harness.PostIntentAsync(driver, scheduledRideId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await harness.PostIntentAsync(driver, scheduledRideId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await DispatchHarness.ReadJsonAsync(first);
        var secondBody = await DispatchHarness.ReadJsonAsync(second);

        Assert.Equal(firstBody.GetProperty("intentId").GetString(), secondBody.GetProperty("intentId").GetString());

        using var board = await harness.JobBoardAsync(driver, Nearest);
        var card = (await DispatchHarness.ReadJsonAsync(board)).GetProperty("items")[0];

        Assert.Equal(1, card.GetProperty("intentCount").GetInt32());
        Assert.True(card.GetProperty("hasIntent").GetBoolean());
    }

    /// <summary>
    /// <b>Definition of Done.</b> D5' §3.7: the T-30 job goes to the <em>closest</em>
    /// intent-poster, and a tie on distance is broken by the higher level.
    /// </summary>
    [Fact]
    public async Task The_T30_job_goes_to_the_closest_intent_poster_and_ties_break_on_level()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var scheduledRideId = await harness.ScheduleRideForAsync(passengerId, DateTimeOffset.UtcNow.AddMinutes(20));

        // Two drivers standing on the same coordinate — the only way to make the distance an exact
        // tie and leave the level as the deciding term — and one further away who is a better
        // weighted candidate on every other axis.
        var tiedLow = await harness.CreateOnlineDriverAsync(Nearest);
        var tiedHigh = await harness.CreateOnlineDriverAsync(Nearest);
        var farther = await harness.CreateOnlineDriverAsync(Further);

        await harness.SetDriverLevelAsync(tiedLow.DriverId, 2);
        await harness.SetDriverLevelAsync(tiedHigh.DriverId, 3);
        await harness.SetDriverLevelAsync(farther.DriverId, 3);

        foreach (var driver in new[] { tiedLow, tiedHigh, farther })
        {
            using var intent = await harness.PostIntentAsync(driver, scheduledRideId);
            Assert.Equal(HttpStatusCode.OK, intent.StatusCode);
        }

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var booking = await harness.ReadScheduledRideAsync(scheduledRideId);
        var outcome = await OfferLoopTests.DispatchAsync(harness, booking.RideId!.Value);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        // Both tied drivers are nearer than `farther`, so the winner is one of those two — and of
        // those two it is the Level-3 one. §3.7 says distance decides and the level breaks the tie,
        // which is a different rule from §3.3's weighted score and not a re-weighting of it.
        Assert.Equal(tiedHigh.DriverId, outcome.DriverId);

        // The audit says which rule ordered the cascade, so a rank that disagrees with the score
        // reads as a different rule rather than as a bug (R-11).
        var scores = await harness.ReadScoresAsync(booking.RideId!.Value);
        using var breakdown = JsonDocument.Parse(scores.Single(row => row.DriverId == tiedHigh.DriverId).Breakdown);

        Assert.Equal("job-board-proximity", breakdown.RootElement.GetProperty("ordering").GetString());
        Assert.Equal(0, breakdown.RootElement.GetProperty("rank").GetInt32());
    }

    /// <summary>
    /// The board is post-intent only: a driver who never posted intent is not a candidate for a
    /// scheduled ride, however near they are standing (D5' §3.7).
    /// </summary>
    [Fact]
    public async Task A_driver_who_posted_no_intent_is_not_offered_the_scheduled_ride()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddMinutes(20));

        var interested = await harness.CreateOnlineDriverAsync(Further);
        await harness.CreateOnlineDriverAsync(Nearest);

        using var intent = await harness.PostIntentAsync(interested, scheduledRideId);
        Assert.Equal(HttpStatusCode.OK, intent.StatusCode);

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var booking = await harness.ReadScheduledRideAsync(scheduledRideId);
        var outcome = await OfferLoopTests.DispatchAsync(harness, booking.RideId!.Value);

        // The nearer driver would have won an ordinary Mode C round outright.
        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(interested.DriverId, outcome.DriverId);
        Assert.Equal(1, outcome.PreFilterCount);
    }

    /// <summary>
    /// An intent posted by a driver who has since gone offline is not a candidate: the §3.2 gates
    /// run over the intent list exactly as they run over the standby pool.
    /// </summary>
    [Fact]
    public async Task An_intent_from_a_driver_who_went_offline_is_not_a_candidate()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddMinutes(20));

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var intent = await harness.PostIntentAsync(driver, scheduledRideId);
        Assert.Equal(HttpStatusCode.OK, intent.StatusCode);

        using var offline = await harness.GoOfflineAsync(driver);
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var booking = await harness.ReadScheduledRideAsync(scheduledRideId);
        var outcome = await OfferLoopTests.DispatchAsync(harness, booking.RideId!.Value);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(0, outcome.CandidateCount);
    }

    /// <summary>US-6A.15: the driver's upcoming list is the rides they have actually been offered.</summary>
    [Fact]
    public async Task An_offered_scheduled_ride_appears_on_the_drivers_upcoming_list()
    {
        await using var harness = await StartAsync();

        var scheduledRideId = await harness.ScheduleRideForAsync(
            await harness.CreatePassengerAsync(), DateTimeOffset.UtcNow.AddMinutes(20));

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        using var intent = await harness.PostIntentAsync(driver, scheduledRideId);
        Assert.Equal(HttpStatusCode.OK, intent.StatusCode);

        // Before the offer the driver has posted interest, not been assigned anything.
        using var before = await harness.GetAsync($"/v1/rides/scheduled/{driver.DriverId}", driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Empty((await DispatchHarness.ReadJsonAsync(before)).GetProperty("items").EnumerateArray());

        Assert.Equal(1, await harness.MaterialiseDueScheduledRidesAsync());

        var booking = await harness.ReadScheduledRideAsync(scheduledRideId);
        var outcome = await OfferLoopTests.DispatchAsync(harness, booking.RideId!.Value);
        Assert.Equal(DispatchResult.Offered, outcome.Result);

        using var after = await harness.GetAsync($"/v1/rides/scheduled/{driver.DriverId}", driver.Bearer);
        var items = (await DispatchHarness.ReadJsonAsync(after)).GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(scheduledRideId.ToString(), items[0].GetProperty("scheduledRideId").GetString());
        Assert.Equal(booking.RideId!.Value.ToString(), items[0].GetProperty("rideId").GetString());
    }

    [Fact]
    public async Task A_driver_cannot_read_another_drivers_upcoming_list()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var other = await harness.CreateDriverAsync();

        using var response = await harness.GetAsync($"/v1/rides/scheduled/{other.DriverId}", driver.Bearer);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
