using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// R-14 / D3' §0 on this service's POSTs: `content.command_log` and the one route that needs it.
/// </summary>
/// <remarks>
/// `POST /v1/admin/content/broadcasts` is the reason the log exists. An approve is self-limiting (a
/// second one is a `409` by the version's own status) and a purge is idempotent by nature; a retried
/// publish would put a **second identical banner** in front of every user on the platform, and no
/// natural key would collide.
/// </remarks>
[Collection<ContentCollection>]
public sealed class IdempotencyTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_retried_broadcast_publish_replays_instead_of_publishing_twice()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { messageByLang = new { si = "එකක්", ta = "ஒன்று", en = "Once" } };

        using var first = await harness.PostWithKeyAsync(
            "/v1/admin/content/broadcasts", body, admin.Bearer, key);
        using var retry = await harness.PostWithKeyAsync(
            "/v1/admin/content/broadcasts", body, admin.Bearer, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);

        // The replay is byte for byte, and it says so on the wire so an operator can tell the two apart.
        Assert.Equal(
            await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.True(retry.Headers.Contains(IdempotencyMiddleware.ReplayHeader));

        // One banner, not two — which is the whole point.
        var listed = await harness.GetAsync<BroadcastsResponse>(
            "/v1/content/broadcasts", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Single(listed.Items);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM content.broadcasts;"));

        // The reservation is in this service's own table, not rides.command_log.
        var logged = await connection.QuerySingleAsync<(string Command, short Status, Guid ActorId)>(
            """
            SELECT command, response_status, actor_id
              FROM content.command_log
             WHERE idempotency_key = @Key;
            """,
            new { Key = key });

        Assert.Equal(201, logged.Status);
        Assert.Equal(admin.Id, logged.ActorId);
        Assert.Contains("broadcasts", logged.Command, StringComparison.Ordinal);
    }

    /// <summary>The same key with a different body is a 409, not a replay of the wrong answer.</summary>
    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();
        var key = Guid.NewGuid().ToString();

        using var first = await harness.PostWithKeyAsync(
            "/v1/admin/content/broadcasts",
            new { messageByLang = new { si = "එකක්", ta = "ஒன்று", en = "Once" } },
            admin.Bearer,
            key);

        using var different = await harness.PostWithKeyAsync(
            "/v1/admin/content/broadcasts",
            new { messageByLang = new { si = "දෙකක්", ta = "இரண்டு", en = "Twice" } },
            admin.Bearer,
            key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("idempotency-key-reuse", (await ContentHarness.ProblemAsync(different)).Code);
    }

    /// <summary>A POST with no key at all is refused — D3' §0 makes the header mandatory.</summary>
    [Fact]
    public async Task A_publish_without_a_key_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/content/broadcasts")
        {
            Content = JsonContent.Create(
                new { messageByLang = new { si = "a", ta = "b", en = "c" } }, options: MageRideJson.Options),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Bearer);

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency-key-required", (await ContentHarness.ProblemAsync(response)).Code);
    }

    /// <summary>
    /// The purge route is exempt, matching its <c>x-idempotency-exempt</c>: dropping an
    /// already-dropped cache is the same operation and there is no response to replay.
    /// </summary>
    [Fact]
    public async Task The_purge_route_needs_no_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/content/cache/purge");
        request.Headers.TryAddWithoutValidation(ContentEndpoints.ApiKeyHeader, ContentHarness.InternalApiKey);

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var purged = await response.Content.ReadFromJsonAsync<PurgeCacheResponse>();

        // No datasets named = all of them.
        Assert.Equal(
            ["cities", "templates", "faq", "broadcasts", "onboarding"], purged!.Purged);
    }
}
