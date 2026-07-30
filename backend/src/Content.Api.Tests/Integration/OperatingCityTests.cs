using System.Net;
using System.Net.Http.Json;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Net.Http.Headers;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>GET /v1/config/cities</c> — AL-27's launch-city list, this component's second fence ("only
/// active operating cities are served publicly") and its second definition of done.
/// </summary>
/// <remarks>
/// Each test starts its own service, so its cache begins empty and a row inserted before the first
/// read is a row the first read sees. That is also why nothing here has to reset the seeded cities:
/// test rows are namespaced by <c>ContentHarness.NextCityCode</c> and the assertions are relative.
/// </remarks>
[Collection<ContentCollection>]
public sealed class OperatingCityTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The definition of done, in one test: active rows only, ordered by <c>sort_order</c>, with
    /// <c>name_si</c>/<c>name_ta</c>/<c>name_en</c>.
    /// </summary>
    [Fact]
    public async Task Active_rows_only_ordered_by_sort_order_with_all_three_names()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        // Two more cities beyond the seeded three: one live, one switched off — and the dark one sorts
        // *ahead* of the live one, so serving it would show up in the ordering as well as the set.
        var dark = await harness.CreateCityAsync(active: false, sortOrder: 90);
        var live = await harness.CreateCityAsync(active: true, sortOrder: 91);

        var response = await harness.GetAsync<CitiesResponse>("/v1/config/cities");
        var codes = response.Cities.Select(city => city.Code).ToArray();

        Assert.Contains(live, codes);
        Assert.DoesNotContain(dark, codes);

        Assert.Equal(
            response.Cities.Select(city => city.SortOrder).Order().ToArray(),
            response.Cities.Select(city => city.SortOrder).ToArray());

        // The §20 seed, asserted as the real Sinhala and Tamil rather than a fixture: Colombo is
        // first and is also the map centroid default (D4' §17b, §19).
        var colombo = Assert.Single(response.Cities, city => city.Code == "colombo");

        Assert.Equal("colombo", response.Cities[0].Code);
        Assert.Equal(0, colombo.SortOrder);
        Assert.Equal("Colombo", colombo.NameEn);
        Assert.Equal("කොළඹ", colombo.NameSi);
        Assert.Equal("கொழும்பு", colombo.NameTa);
        Assert.Equal(6.9271, colombo.Centroid.Lat, 4);
        Assert.Equal(79.8612, colombo.Centroid.Lng, 4);

        // Every city, seeded or not, carries all three labels — nothing here can serve a name in
        // fewer than three languages (D-26).
        Assert.All(response.Cities, city =>
        {
            Assert.False(string.IsNullOrWhiteSpace(city.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(city.NameSi));
            Assert.False(string.IsNullOrWhiteSpace(city.NameTa));
        });
    }

    /// <summary>
    /// Public: the screen this list is drawn on precedes sign-in, so a bearer is neither required nor
    /// rejected.
    /// </summary>
    [Fact]
    public async Task The_list_is_public_and_a_bearer_makes_no_difference()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var anonymous = await harness.GetAsync("/v1/config/cities");
        using var authenticated = await harness.GetAsync(
            "/v1/config/cities", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(anonymous.Headers.ETag?.Tag, authenticated.Headers.ETag?.Tag);
    }

    /// <summary>
    /// The caching half of AL-27: a strong validator, a <c>max-age</c> equal to the service's own TTL,
    /// and a 304 for a client that already has the list.
    /// </summary>
    [Fact]
    public async Task An_unchanged_list_revalidates_to_304()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var first = await harness.GetAsync("/v1/config/cities");

        var etag = first.Headers.ETag?.Tag;

        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.False(first.Headers.ETag!.IsWeak);
        Assert.True(first.Headers.CacheControl?.Public);

        // max-age equals Content:CacheTtl (D7' §4.2's Cache__Ttl=300), so an intermediary caches for
        // the window this service does rather than a guess of its own.
        Assert.Equal(TimeSpan.FromSeconds(300), first.Headers.CacheControl?.MaxAge);

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/v1/config/cities");
        revalidate.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, etag);

        using var second = await harness.Client.SendAsync(revalidate);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        // RFC 9110: the 304 carries the validator that matched, so the client has something to
        // revalidate with next time.
        Assert.Equal(etag, second.Headers.ETag?.Tag);

        // And the comparison is the weak one this header requires: an intermediary that re-encodes the
        // body weakens the validator it passes on, and a strict compare would leave every client
        // behind such a proxy refetching the whole list for ever.
        using var weak = new HttpRequestMessage(HttpMethod.Get, "/v1/config/cities");
        weak.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, $"W/{etag}");

        using var weakResponse = await harness.Client.SendAsync(weak);

        Assert.Equal(HttpStatusCode.NotModified, weakResponse.StatusCode);
    }

    /// <summary>
    /// The validator tracks the payload, so launching a city changes it — and the purge route is what
    /// makes that visible before the TTL, for the one dataset this service does not own.
    /// </summary>
    [Fact]
    public async Task Launching_a_city_changes_the_validator_after_a_purge()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var before = await harness.GetAsync("/v1/config/cities");
        var firstEtag = before.Headers.ETag?.Tag;

        // What admin-bff's `POST /v1/admin/config/cities` does (C065) — a write in another service.
        var launched = await harness.CreateCityAsync(sortOrder: 95);

        // Unpurged, the answer is the cached one. This is the TTL-only behaviour, and it is exactly
        // why the purge route exists.
        var stale = await harness.GetAsync<CitiesResponse>("/v1/config/cities");
        Assert.DoesNotContain(launched, stale.Cities.Select(city => city.Code));

        using var purge = await harness.PostAsync(
            "/v1/internal/content/cache/purge",
            new { datasets = new[] { "cities" } },
            internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.Accepted, purge.StatusCode);

        using var after = await harness.GetAsync("/v1/config/cities");
        var fresh = await after.Content.ReadFromJsonAsync<CitiesResponse>();

        Assert.Contains(launched, fresh!.Cities.Select(city => city.Code));
        Assert.NotEqual(firstEtag, after.Headers.ETag?.Tag);
    }

    /// <summary>The purge plane is internal: no key, no route.</summary>
    [Fact]
    public async Task The_purge_route_is_refused_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var anonymous = await harness.PostAsync("/v1/internal/content/cache/purge", body: null);
        using var wrongKey = await harness.PostAsync(
            "/v1/internal/content/cache/purge", body: null, internalKey: "not-the-key");

        // 404, not 401: what the gateway answers for /v1/internal/** (C008). A caller who is not
        // entitled to the internal plane should not be able to map it.
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        Assert.Equal("not-found", (await ContentHarness.ProblemAsync(wrongKey)).Code);
    }

    /// <summary>An unknown dataset name is a 400, not a silently ignored no-op.</summary>
    [Fact]
    public async Task An_unknown_dataset_is_rejected_rather_than_ignored()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.PostAsync(
            "/v1/internal/content/cache/purge",
            new { datasets = new[] { "city" } },
            internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, body) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.Contains("city", body.GetProperty("errors").GetProperty("datasets")[0].GetString());
    }
}
