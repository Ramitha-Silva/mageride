using System.Net;
using System.Net.Http.Json;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>POST /v1/admin/content/broadcasts</c> and <c>GET /v1/content/broadcasts</c> — US-14.8's in-app
/// announcement banner.
/// </summary>
[Collection<ContentCollection>]
public sealed class BroadcastTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_published_broadcast_is_served_in_the_requested_language()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var published = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new
            {
                messageByLang = new
                {
                    si = "සේවා යාවත්කාලීන කිරීමක්",
                    ta = "சேவை புதுப்பிப்பு",
                    en = "Service update",
                },
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var created = (await published.Content.ReadFromJsonAsync<BroadcastResponse>())!;

        Assert.NotEqual(Guid.Empty, created.BroadcastId);
        Assert.Equal("Service update", created.Message);

        // Omitted startsAt means "now", on the service's clock — not whatever `created_at` defaulted to.
        Assert.Equal(harness.Clock.GetUtcNow(), created.StartsAt);
        Assert.Null(created.EndsAt);

        var passenger = harness.Tokens.Passenger(Guid.NewGuid());

        foreach (var (language, expected) in new[]
                 {
                     ("si", "සේවා යාවත්කාලීන කිරීමක්"),
                     ("ta", "சேவை புதுப்பிப்பு"),
                     ("en", "Service update"),
                 })
        {
            var listed = await harness.GetAsync<BroadcastsResponse>(
                $"/v1/content/broadcasts?lang={language}", passenger);

            var banner = Assert.Single(listed.Items, item => item.BroadcastId == created.BroadcastId);

            Assert.Equal(expected, banner.Message);
        }
    }

    /// <summary>The trilingual fence applies to a broadcast as much as to a template.</summary>
    [Fact]
    public async Task A_broadcast_missing_a_language_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new { messageByLang = new { en = "Service update", si = "සේවා යාවත්කාලීන" } },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.True(problem.GetProperty("errors").TryGetProperty("messageByLang.ta", out _));
    }

    /// <summary>
    /// The window is applied per request against the clock, so a scheduled banner appears when its
    /// start time arrives — without waiting for the cache TTL.
    /// </summary>
    [Fact]
    public async Task A_scheduled_broadcast_appears_when_its_window_opens_and_leaves_when_it_closes()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();
        var passenger = harness.Tokens.Passenger(Guid.NewGuid());
        var now = harness.Clock.GetUtcNow();

        using var published = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new
            {
                messageByLang = new { si = "පසුව", ta = "பின்னர்", en = "Later" },
                startsAt = now.AddMinutes(30),
                endsAt = now.AddMinutes(90),
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var created = (await published.Content.ReadFromJsonAsync<BroadcastResponse>())!;

        Assert.Equal(now.AddMinutes(30), created.StartsAt);
        Assert.Equal(now.AddMinutes(90), created.EndsAt);

        // Before the window: not served, and this read *is* from the cache the publish just filled —
        // which is the half that proves the window is applied per request rather than at load time.
        Assert.Empty((await harness.GetAsync<BroadcastsResponse>("/v1/content/broadcasts", passenger)).Items);

        // Inside it: served, with no purge. Thirty-one minutes is past the 300 s TTL, so this read
        // reloads — which is the other thing worth proving, because the load reaches only one TTL into
        // the future and a banner starting later has to be picked up by a later reload.
        harness.Clock.Advance(TimeSpan.FromMinutes(31));

        var live = await harness.GetAsync<BroadcastsResponse>("/v1/content/broadcasts", passenger);

        Assert.Equal(created.BroadcastId, Assert.Single(live.Items).BroadcastId);

        // After it: gone. A banner with an end that never came down would be the alternative.
        harness.Clock.Advance(TimeSpan.FromMinutes(60));

        Assert.Empty((await harness.GetAsync<BroadcastsResponse>("/v1/content/broadcasts", passenger)).Items);
    }

    /// <summary>A window that runs backwards would show nothing, so it is refused rather than stored.</summary>
    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();
        var now = harness.Clock.GetUtcNow();

        using var response = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new
            {
                messageByLang = new { si = "a", ta = "b", en = "c" },
                startsAt = now.AddHours(2),
                endsAt = now.AddHours(1),
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (_, problem) = await ContentHarness.ProblemAsync(response);

        Assert.True(problem.GetProperty("errors").TryGetProperty("endsAt", out _));
    }

    /// <summary>
    /// An audience selector is evaluated against the bearer's whole role set (AL-06) and its app.
    /// </summary>
    [Fact]
    public async Task An_audience_selector_is_evaluated_against_the_bearer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var published = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new
            {
                messageByLang = new { si = "රියදුරන්ට", ta = "ஓட்டுநர்களுக்கு", en = "For drivers" },
                audience = new { role = "driver" },
            },
            admin.Bearer);

        var created = (await published.Content.ReadFromJsonAsync<BroadcastResponse>())!;

        Assert.Equal("driver", created.Audience?.Role);

        var forDriver = await harness.GetAsync<BroadcastsResponse>(
            "/v1/content/broadcasts", harness.Tokens.Driver(Guid.NewGuid()));

        Assert.Equal(created.BroadcastId, Assert.Single(forDriver.Items).BroadcastId);

        var forPassenger = await harness.GetAsync<BroadcastsResponse>(
            "/v1/content/broadcasts", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Empty(forPassenger.Items);

        // Effective permissions are the union of every role held (AL-06), so somebody who drives and
        // also books rides sees the driver announcement.
        var both = harness.Tokens.Issue(Guid.NewGuid(), ["passenger", "driver"], "passenger");

        Assert.Single((await harness.GetAsync<BroadcastsResponse>("/v1/content/broadcasts", both)).Items);
    }

    /// <summary>
    /// A selector this platform cannot evaluate is refused at publish time rather than ignored at read
    /// time — the alternative is a banner an admin believes is targeted and the whole island receives.
    /// </summary>
    [Theory]
    [InlineData("reseller", null)]
    [InlineData(null, "web")]
    public async Task An_unevaluable_audience_is_refused(string? role, string? app)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new
            {
                messageByLang = new { si = "a", ta = "b", en = "c" },
                audience = new { role, app },
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);
        var errors = problem.GetProperty("errors");

        Assert.Equal("validation-failed", code);
        Assert.True(
            errors.TryGetProperty("audience.role", out _) || errors.TryGetProperty("audience.app", out _));
    }

    /// <summary>The banner is a bearer surface; the publish is an admin one.</summary>
    [Fact]
    public async Task Reading_needs_a_bearer_and_publishing_needs_an_admin()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var anonymous = await harness.GetAsync("/v1/content/broadcasts");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var byDriver = await harness.PostAsync(
            "/v1/admin/content/broadcasts",
            new { messageByLang = new { si = "a", ta = "b", en = "c" } },
            harness.Tokens.Driver(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, byDriver.StatusCode);
    }
}
