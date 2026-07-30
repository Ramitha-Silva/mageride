using System.Net;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>GET /v1/content/faq</c> — the authored source behind `GET /v1/support/faq` (US-16.1, C053).
/// </summary>
[Collection<ContentCollection>]
public sealed class FaqTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// US-16.1's four topics, in each of the three languages, ordered as the app renders them.
    /// </summary>
    [Theory]
    [InlineData("en", "How do I top up my wallet?")]
    [InlineData("si", "මගේ පසුම්බියට මුදල් ඇතුළත් කරන්නේ කෙසේද?")]
    [InlineData("ta", "எனது பணப்பையை எப்படி நிரப்புவது?")]
    public async Task The_seeded_faq_is_served_in_every_language(string language, string firstTitle)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync<FaqResponse>(
            $"/v1/content/faq?lang={language}", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Equal(language, response.Language);

        // The four topics US-16.1 names, and nothing invented beside them (1902).
        Assert.Equal(
            ["wallet", "daily_fee", "vehicle_registration", "booking"],
            response.Items.Select(item => item.Category).ToArray());

        Assert.Equal(firstTitle, response.Items[0].Title);

        Assert.Equal(
            response.Items.Select(item => item.SortOrder).Order().ToArray(),
            response.Items.Select(item => item.SortOrder).ToArray());

        Assert.All(response.Items, item =>
        {
            Assert.NotEqual(Guid.Empty, item.ArticleId);
            Assert.False(string.IsNullOrWhiteSpace(item.Body));
        });
    }

    [Fact]
    public async Task A_category_filter_narrows_the_answer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync<FaqResponse>(
            "/v1/content/faq?lang=en&category=daily_fee", harness.Tokens.Driver(Guid.NewGuid()));

        var article = Assert.Single(response.Items);

        Assert.Equal("daily_fee", article.Category);
        Assert.Contains("first trip of the day is free", article.Body);
    }

    /// <summary>An unknown language falls back to English, and the answer says which it served.</summary>
    [Fact]
    public async Task An_unknown_language_falls_back_and_says_so()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync<FaqResponse>(
            "/v1/content/faq?lang=de", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Equal("en", response.Language);
        Assert.NotEmpty(response.Items);
    }

    /// <summary>An unknown category is an empty list, not a 404: it is a filter, not a resource.</summary>
    [Fact]
    public async Task An_unknown_category_is_empty()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync<FaqResponse>(
            "/v1/content/faq?category=no_such_category", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Empty(response.Items);
    }

    /// <summary>
    /// A category that looks like a wildcard is still just a category, and it cannot poison the
    /// unfiltered answer.
    /// </summary>
    /// <remarks>
    /// The cache is keyed by language alone and the category is applied to the cached rows, so no
    /// caller-supplied string reaches a cache key. Keyed by <c>(language, category)</c>, `?category=*`
    /// and "no category" would have collided on the sentinel and served every reader an empty FAQ for
    /// a whole TTL.
    /// </remarks>
    [Fact]
    public async Task A_wildcard_looking_category_does_not_poison_the_unfiltered_read()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var passenger = harness.Tokens.Passenger(Guid.NewGuid());

        var wildcard = await harness.GetAsync<FaqResponse>("/v1/content/faq?category=*", passenger);

        Assert.Empty(wildcard.Items);

        var everything = await harness.GetAsync<FaqResponse>("/v1/content/faq", passenger);

        Assert.Equal(4, everything.Items.Count);
    }

    /// <summary>The contract's <c>maxLength: 60</c> is enforced, not assumed.</summary>
    [Fact]
    public async Task An_over_long_category_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync(
            $"/v1/content/faq?category={new string('x', 61)}", harness.Tokens.Passenger(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.True(problem.GetProperty("errors").TryGetProperty("category", out _));
    }

    [Fact]
    public async Task The_faq_needs_a_bearer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync("/v1/content/faq");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
