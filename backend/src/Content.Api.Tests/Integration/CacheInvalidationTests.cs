using System.Net;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// This component's third definition of done: <b>a template change is visible to notification-svc
/// within the documented cache TTL</b> — D7' §4.2's <c>Cache__Ttl</c>=300.
/// </summary>
/// <remarks>
/// Two independently built services against one Postgres and one Redis, which is the deployment shape
/// the claim is about: the replica that published the change is not the replica notification-svc
/// happens to call. Both halves are asserted — the purge that makes it immediate, and the TTL that
/// bounds it when the purge is switched off.
/// </remarks>
[Collection<ContentCollection>]
public sealed class CacheInvalidationTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The usual case: a publish on one replica reaches the other over
    /// <c>RedisKeys.ContentInvalidationChannel</c>, well inside the TTL.
    /// </summary>
    [Fact]
    public async Task A_publish_on_one_replica_is_seen_by_another_immediately()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var author = await ContentHarness.StartAsync(postgres, redis);
        await using var renderer = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await author.SeedTemplateAsync(key, body: "Version one");

        // The renderer reads first, so its cache genuinely holds the old version — otherwise the test
        // would pass with no invalidation at all.
        var before = await renderer.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(1, before.Version);

        var admin = await author.CreateAdminAsync();

        using var draft = await author.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = TemplateReadTests.Trilingual("Version two") },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        using var approved = await author.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 2 }, admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // Pub/sub delivery is asynchronous, so this polls — but the clock never advances, so a pass
        // proves the *purge* did it and not the TTL.
        var served = await EventuallyAsync(
            renderer,
            key,
            version: 2,
            because: "the approval should have purged the other replica's cache over Redis pub/sub");

        Assert.Equal(2, served.Version);
        Assert.StartsWith("Version two", served.Body);
    }

    /// <summary>
    /// The worst case, and the one the definition of done is written against: with the cross-replica
    /// purge off, the change is invisible until the entry expires — and visible the moment it does.
    /// </summary>
    [Fact]
    public async Task With_the_purge_off_the_change_lands_within_the_documented_ttl()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var author = await ContentHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Content:InvalidationEnabled"] = "false" });

        await using var renderer = await ContentHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Content:InvalidationEnabled"] = "false" });

        var key = ContentHarness.NextTemplateKey();
        await author.SeedTemplateAsync(key, body: "Version one");

        var before = await renderer.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(1, before.Version);

        var admin = await author.CreateAdminAsync();

        using var draft = await author.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = TemplateReadTests.Trilingual("Version two") },
            admin.Bearer);

        using var approved = await author.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 2 }, admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // Still the old version: this is the behaviour the TTL bounds rather than eliminates.
        var stale = await renderer.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(1, stale.Version);

        // One second short of the documented TTL, the promise is not yet due.
        renderer.Clock.Advance(TimeSpan.FromSeconds(299));

        var justBefore = await renderer.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(1, justBefore.Version);

        // At the TTL, it is.
        renderer.Clock.Advance(TimeSpan.FromSeconds(2));

        var fresh = await renderer.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(2, fresh.Version);
        Assert.StartsWith("Version two", fresh.Body);
    }

    /// <summary>
    /// <c>Cache__Ttl</c> — D7' §4.2 spells the TTL unprefixed and <c>.env.app.example</c> ships it that
    /// way, so an operator who set the documented variable must not be setting a key nothing reads.
    /// </summary>
    [Fact]
    public async Task The_d7_spelling_of_the_ttl_is_honoured()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Cache:Ttl"] = "60" });

        using var response = await harness.GetAsync("/v1/config/cities");

        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.CacheControl?.MaxAge);
    }

    /// <summary>The service's own key wins over the D7' spelling when both are set.</summary>
    [Fact]
    public async Task The_services_own_key_wins_over_the_d7_spelling()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Cache:Ttl"] = "60",
                ["Content:CacheTtl"] = "00:00:45",
            });

        using var response = await harness.GetAsync("/v1/config/cities");

        Assert.Equal(TimeSpan.FromSeconds(45), response.Headers.CacheControl?.MaxAge);
    }

    /// <summary>
    /// With the cache off, every read is a database round trip — correct, and the load the cache exists
    /// to absorb. Asserted because it is a switch, and a switch nothing tests is a switch that rots.
    /// </summary>
    [Fact]
    public async Task With_the_cache_off_a_change_is_seen_on_the_next_read()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Content:CacheEnabled"] = "false" });

        using var before = await harness.GetAsync("/v1/config/cities");

        var launched = await harness.CreateCityAsync(sortOrder: 97);

        var after = await harness.GetAsync<CitiesResponse>("/v1/config/cities");

        Assert.Contains(launched, after.Cities.Select(city => city.Code));
    }

    private static async Task<NotificationTemplateResponse> EventuallyAsync(
        ContentHarness harness, string key, int version, string because)
    {
        NotificationTemplateResponse? last = null;

        // 5 s is three orders of magnitude above a local Redis round trip and two below the TTL, so a
        // timeout here is a broken purge rather than a slow one.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            last = await harness.GetAsync<NotificationTemplateResponse>(
                $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

            if (last.Version == version)
            {
                return last;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Template {key} was still version {last?.Version} after 5 s: {because}.");

        return last!;
    }
}
