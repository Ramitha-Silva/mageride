using MageRide.TestKit;
using MageRide.Transit.Endpoints;
using MageRide.Transit.Tests.Infrastructure;

namespace MageRide.Transit.Tests.Integration;

/// <summary>
/// <b>Definition of done: "activating a new feed version refreshes the cache within 60 s without a
/// restart"</b> and <b>"with no active feed the endpoint degrades rather than erroring."</b>
/// </summary>
[Collection(TransitCollection.Name)]
public sealed class FeedLifecycleTests(PostgresFixture postgres)
{
    private const string FortToKottawa =
        "/v1/transit/options?fromLat=6.9344&fromLng=79.8428&toLat=6.8410&toLng=79.9653";

    [Fact]
    public async Task Activating_a_feed_refreshes_the_cache_without_a_restart()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync(feedInfoVersion: "2026-07-01");

        var first = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.FeedVersion == "2026-07-01");

        Assert.NotEmpty(first.Options);

        // A second feed, activated the way C057 does it: status swap plus NOTIFY, one transaction.
        // The same process, still running, must serve it.
        await harness.Seed.ActivateAsync(feedInfoVersion: "2026-08-01");

        var second = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.FeedVersion == "2026-08-01", TimeSpan.FromSeconds(60));

        Assert.NotEmpty(second.Options);
    }

    [Fact]
    public async Task A_notification_that_never_arrives_is_covered_by_the_safety_net_poll()
    {
        // The reason the ≤ 60 s bound is a guarantee rather than a hope: LISTEN is delivered to
        // sessions connected at the moment it fires, so a reconnect window loses it. Here the
        // NOTIFY is deliberately not sent at all.
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync(feedInfoVersion: "2026-07-01");

        await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.FeedVersion == "2026-07-01");

        var second = await harness.Seed.LoadAsync(feedInfoVersion: "2026-09-01");

        await harness.Seed.ActivateAsync(second, notify: false);

        var answer = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.FeedVersion == "2026-09-01", TimeSpan.FromSeconds(60));

        Assert.NotEmpty(answer.Options);
    }

    [Fact]
    public async Task With_no_active_feed_the_endpoint_degrades_rather_than_erroring()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        await harness.WaitForAsync<TransitOptionsResponse>(FortToKottawa, result => result.Options.Count > 0);

        await harness.Seed.ArchiveAllAsync();

        var degraded = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.Coverage == TransitEndpoints.CoverageNoFeed);

        // 200 with an empty list, so SCR-PA-009 keeps its live buses and private tiers and hides
        // route matching (AL-55) — rather than an error, which is a screen nobody can use.
        Assert.Empty(degraded.Options);
        Assert.Null(degraded.FeedVersion);
    }

    [Fact]
    public async Task Before_the_first_import_the_answer_is_degraded_and_not_an_error()
    {
        // AL-55's "pre-first-import state", which is what a fresh deployment is in.
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ArchiveAllAsync();

        var answer = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.Coverage == TransitEndpoints.CoverageNoFeed);

        Assert.Empty(answer.Options);
    }

    [Fact]
    public async Task No_coverage_and_no_route_on_the_corridor_are_different_answers()
    {
        // The whole reason `coverage` exists. Both are an empty list; only one of them means the
        // platform cannot tell, and SCR-PA-009 renders them differently.
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync();

        // Two points in the middle of the Indian Ocean: a live feed, and no bus goes there.
        var noRoute = await harness.WaitForAsync<TransitOptionsResponse>(
            "/v1/transit/options?fromLat=5.0&fromLng=82.0&toLat=5.1&toLng=82.1",
            result => result.Coverage == TransitEndpoints.CoverageActive);

        Assert.Empty(noRoute.Options);
        Assert.NotNull(noRoute.FeedVersion);

        await harness.Seed.ArchiveAllAsync();

        var noFeed = await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.Coverage == TransitEndpoints.CoverageNoFeed);

        Assert.Empty(noFeed.Options);
        Assert.Null(noFeed.FeedVersion);
    }

    [Fact]
    public async Task A_feed_that_is_only_validated_is_not_served()
    {
        // Exactly one feed is active (ux_gtfs_feed_one_active); a validated-but-not-activated
        // upload is a candidate an operator is still looking at in SCR-AP-016.
        await using var harness = await TransitHarness.StartAsync(postgres);

        await harness.Seed.ActivateAsync(feedInfoVersion: "2026-07-01");

        await harness.WaitForAsync<TransitOptionsResponse>(
            FortToKottawa, result => result.FeedVersion == "2026-07-01");

        await harness.Seed.LoadAsync(feedInfoVersion: "2026-10-01");

        // The load truncated and rewrote the GTFS tables — which is what the importer's staging
        // swap does on activate, not before — so this also pins that the cache is not re-read
        // per request: the answer is still the feed that was loaded.
        var answer = await harness.GetAsync<TransitOptionsResponse>(FortToKottawa);

        Assert.Equal("2026-07-01", answer.FeedVersion);
    }
}
