using System.Net;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// The Driver Level System (D5' §4, US-6A.6/6A.7/6A.8/6A.14, US-14.12).
/// </summary>
/// <remarks>
/// The level-<em>down</em> rules driven by counters — three confirmed reports and the temporary
/// delisting that comes with them — are reputation-svc's (C033) and are tested there. What is here
/// is the half D3' and D5' §4.1 put on this side: points from ratings, the no-show decrement, the
/// two driver-facing reads and the admin configuration.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class DriverLevelTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>
    /// <b>D5' §4.2's own worked example.</b> "100 five-star = 500 points = +1 level." The driver
    /// starts at 1 rather than the default 3, because a level-up is invisible at the ceiling.
    /// </summary>
    [Fact]
    public async Task A_hundred_five_star_rides_are_five_hundred_points_and_one_level()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 100);

        var body = await ReadLevelAsync(harness, driver);

        Assert.Equal(2, body.GetProperty("level").GetInt32());
        Assert.Equal(500, body.GetProperty("levelUpThreshold").GetInt32());

        // 500 exactly, so the remainder is 0 — "points -= 500" leaves nothing over.
        Assert.Equal(0, body.GetProperty("ratingPoints").GetInt32());

        var stored = await harness.ReadDriverLevelAsync(driver.DriverId);
        Assert.Equal(500, stored!.PointsAwardedTotal);
    }

    /// <summary>
    /// The other example §4.2 prints: "50×5★ + 65×4★ = 250 + 260 = 510 ⇒ +1", with 10 points left
    /// over — which is also what proves 4★ is worth 4 and not 1.
    /// </summary>
    [Fact]
    public async Task Four_star_ratings_count_four_and_the_remainder_carries()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 50);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 4, times: 65);

        var body = await ReadLevelAsync(harness, driver);

        Assert.Equal(2, body.GetProperty("level").GetInt32());
        Assert.Equal(10, body.GetProperty("ratingPoints").GetInt32());
    }

    /// <summary>D5' §4.2: "counting only 4★ and 5★ (≤2★ ignored; 3★ counts 0)".</summary>
    [Fact]
    public async Task Three_star_and_below_are_worth_nothing()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 3, times: 200);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 1, times: 50);

        var body = await ReadLevelAsync(harness, driver);

        Assert.Equal(1, body.GetProperty("level").GetInt32());
        Assert.Equal(0, body.GetProperty("ratingPoints").GetInt32());
    }

    /// <summary>
    /// The engine recomputes and applies the delta, so reading a level twice cannot award the same
    /// ratings twice — which is what makes the sweep and the read path safe to run together.
    /// </summary>
    [Fact]
    public async Task Recounting_the_same_ratings_awards_them_once()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 100);

        for (var i = 0; i < 3; i++)
        {
            var body = await ReadLevelAsync(harness, driver);
            Assert.Equal(2, body.GetProperty("level").GetInt32());
            Assert.Equal(0, body.GetProperty("ratingPoints").GetInt32());
        }

        // The sweep is the other entry point and reaches the same conclusion: nothing to do.
        Assert.Equal(0, await SweepAsync(harness));
    }

    /// <summary>
    /// The sweep is what moves a level with nobody looking — the dispatch hot path reads the level
    /// through reputation-svc, which reads the table and never these routes.
    /// </summary>
    [Fact]
    public async Task The_sweep_levels_up_a_driver_nobody_has_read()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 100);

        Assert.Equal(1, await SweepAsync(harness));

        var stored = await harness.ReadDriverLevelAsync(driver.DriverId);
        Assert.Equal(2, stored!.Level);
    }

    /// <summary>US-6A.7: a no-show on an accepted ride costs a level, once per ride.</summary>
    [Fact]
    public async Task A_no_show_takes_one_level_and_a_redelivery_takes_none()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var rideId = Guid.NewGuid();

        using (var first = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show",
                   new { rideId = rideId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(2, (await DispatchHarness.ReadJsonAsync(first)).GetProperty("level").GetInt32());
        }

        using (var replay = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show",
                   new { rideId = rideId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            // Still 2. The insert into dispatch.no_show_events IS the claim, so a report delivered
            // twice takes one level (ux_no_show_driver_ride, migration 0713).
            Assert.Equal(2, (await DispatchHarness.ReadJsonAsync(replay)).GetProperty("level").GetInt32());
        }

        // A different ride is a different no-show, and it costs another level.
        using (var second = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show",
                   new { rideId = Guid.NewGuid().ToString() }))
        {
            Assert.Equal(1, (await DispatchHarness.ReadJsonAsync(second)).GetProperty("level").GetInt32());
        }

        // Level 1 is the floor: US-6A.8 makes it a loss of privileges, not a ban, so there is
        // nothing below it to fall to.
        using (var third = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show",
                   new { rideId = Guid.NewGuid().ToString() }))
        {
            Assert.Equal(1, (await DispatchHarness.ReadJsonAsync(third)).GetProperty("level").GetInt32());
        }
    }

    /// <summary>Without the shared secret the whole internal family is invisible, not merely refused.</summary>
    [Fact]
    public async Task The_internal_no_show_route_is_unreachable_without_the_key()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.InternalAsync(
            HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show", new { }, apiKey: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stored = await harness.ReadDriverLevelAsync(driver.DriverId);
        Assert.Null(stored);
    }

    /// <summary>US-6A.14's three numbers.</summary>
    [Fact]
    public async Task Stats_report_acceptance_no_shows_and_points()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var passengerId = await harness.CreatePassengerAsync();

        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 3);

        using (var noShow = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/drivers/{driver.DriverId}/no-show",
                   new { rideId = Guid.NewGuid().ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, noShow.StatusCode);
        }

        using var response = await harness.GetAsync($"/v1/drivers/{driver.DriverId}/stats", driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await DispatchHarness.ReadJsonAsync(response);

        Assert.Equal(15, body.GetProperty("points").GetInt32());
        Assert.Equal(1, body.GetProperty("noShows").GetInt32());

        // A driver who has never been offered anything has declined nothing. 0 would describe a
        // refusal that never happened.
        Assert.Equal(1d, body.GetProperty("acceptanceRate").GetDouble());
    }

    [Fact]
    public async Task A_driver_cannot_read_another_drivers_level_or_stats()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();
        var other = await harness.CreateDriverAsync();

        using var level = await harness.GetAsync($"/v1/drivers/{other.DriverId}/level", driver.Bearer);
        Assert.Equal(HttpStatusCode.Forbidden, level.StatusCode);

        using var stats = await harness.GetAsync($"/v1/drivers/{other.DriverId}/stats", driver.Bearer);
        Assert.Equal(HttpStatusCode.Forbidden, stats.StatusCode);

        // Support staff may: it is the first thing asked on a "why did I stop getting rides" call.
        using var support = await harness.GetAsync(
            $"/v1/drivers/{other.DriverId}/level", harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, support.StatusCode);
    }

    /// <summary>
    /// US-14.12: the level parameters are admin-tunable at runtime, and the change is live for the
    /// next request rather than for the next deployment.
    /// </summary>
    [Fact]
    public async Task Admin_configuration_changes_the_threshold_and_the_job_board_floor()
    {
        await using var harness = await StartAsync();

        var admin = harness.Tokens.Admin(Guid.NewGuid());

        using (var response = await Put(harness, admin, new { levelUpThreshold = 100, jobBoardMinLevel = 3 }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await DispatchHarness.ReadJsonAsync(response);
            Assert.Equal(100, body.GetProperty("levelUpThreshold").GetInt32());
            Assert.Equal(3, body.GetProperty("jobBoardMinLevel").GetInt32());
        }

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var passengerId = await harness.CreatePassengerAsync();

        await harness.SetDriverLevelAsync(driver.DriverId, 1);
        await harness.RateDriverAsync(driver.DriverId, passengerId, stars: 5, times: 20);

        // 20 × 5 = 100 points, which is one level at the new threshold and none at the old one.
        var level = await ReadLevelAsync(harness, driver);
        Assert.Equal(2, level.GetProperty("level").GetInt32());
        Assert.Equal(100, level.GetProperty("levelUpThreshold").GetInt32());

        // …and at jobBoardMinLevel = 3, Level 2 no longer reaches the board.
        using var board = await harness.JobBoardAsync(driver, Nearest);
        Assert.Equal(HttpStatusCode.Forbidden, board.StatusCode);
    }

    [Fact]
    public async Task A_driver_cannot_configure_the_level_system()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateDriverAsync();

        using var response = await Put(harness, driver.Bearer, new { levelUpThreshold = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_impossible_threshold_is_refused()
    {
        await using var harness = await StartAsync();

        using var response = await Put(
            harness, harness.Tokens.Admin(Guid.NewGuid()), new { levelUpThreshold = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<System.Text.Json.JsonElement> ReadLevelAsync(
        DispatchHarness harness, SeededDriver driver)
    {
        using var response = await harness.GetAsync($"/v1/drivers/{driver.DriverId}/level", driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await DispatchHarness.ReadJsonAsync(response);
    }

    private static async Task<int> SweepAsync(DispatchHarness harness)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<MageRide.Dispatch.Levels.IDriverLevelService>()
            .SweepAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> Put(DispatchHarness harness, string bearer, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/admin/drivers/level-config")
        {
            Content = System.Net.Http.Json.JsonContent.Create(body),
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        return harness.Client.SendAsync(request);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
