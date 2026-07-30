using System.Net;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Net.Http.Headers;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>GET /v1/content/onboarding/{audience}</c> — AL-28 / BR-25.1 / US-1.2's feature carousel.
/// </summary>
[Collection<ContentCollection>]
public sealed class OnboardingCarouselTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// Three slides per audience, in pager order, each with a headline and a body in all three
    /// languages — and the real Sinhala and Tamil migration 1903 seeded.
    /// </summary>
    [Theory]
    [InlineData("driver", "onboarding/driver-vehicle")]
    [InlineData("passenger", "onboarding/passenger-map")]
    public async Task Three_slides_per_audience_in_all_three_languages(string audience, string firstRef)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync<OnboardingResponse>($"/v1/content/onboarding/{audience}");

        Assert.Equal(3, response.Slides.Count);
        Assert.Equal([1, 2, 3], response.Slides.Select(slide => slide.Slot).ToArray());
        Assert.Equal(firstRef, response.Slides[0].IllustrationRef);

        Assert.All(response.Slides, slide =>
        {
            Assert.False(string.IsNullOrWhiteSpace(slide.Title.Si));
            Assert.False(string.IsNullOrWhiteSpace(slide.Title.Ta));
            Assert.False(string.IsNullOrWhiteSpace(slide.Title.En));
            Assert.False(string.IsNullOrWhiteSpace(slide.Body.Si));
            Assert.False(string.IsNullOrWhiteSpace(slide.Body.Ta));
            Assert.False(string.IsNullOrWhiteSpace(slide.Body.En));
        });
    }

    /// <summary>The driver deck covers US-1.2a's themes; the passenger deck is its own.</summary>
    [Fact]
    public async Task The_driver_deck_carries_the_themes_the_story_names()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var driver = await harness.GetAsync<OnboardingResponse>("/v1/content/onboarding/driver");
        var passenger = await harness.GetAsync<OnboardingResponse>("/v1/content/onboarding/passenger");

        // US-1.2a: vehicle onboarding, 15 s dispatch (with Directional Travel), wallet & daily fee.
        Assert.Contains("four steps", driver.Slides[0].Title.En);
        Assert.Contains("15 seconds", driver.Slides[1].Title.En);
        Assert.Contains("Directional Travel", driver.Slides[1].Body.En);
        Assert.Contains("daily fee", driver.Slides[2].Title.En);

        // The two decks are different content, not one deck served twice.
        Assert.NotEqual(
            driver.Slides.Select(slide => slide.Title.En).ToArray(),
            passenger.Slides.Select(slide => slide.Title.En).ToArray());

        // The Sinhala wallet vocabulary is the FAQ seed's, so two screens do not name it differently.
        Assert.Contains("පසුම්බිය", driver.Slides[2].Title.Si);
    }

    /// <summary>
    /// Public: the carousel sits above the language picker on the first-run screen, so there is no
    /// account yet — and no <c>lang</c> parameter, because the picker is on the same screen.
    /// </summary>
    [Fact]
    public async Task The_carousel_is_public_and_carries_every_language_at_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync("/v1/content/onboarding/driver");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromSeconds(300), response.Headers.CacheControl?.MaxAge);

        var etag = response.Headers.ETag?.Tag;

        Assert.False(string.IsNullOrWhiteSpace(etag));

        // A `lang` parameter is ignored rather than honoured: all three ship in one answer.
        using var withLang = new HttpRequestMessage(HttpMethod.Get, "/v1/content/onboarding/driver?lang=ta");
        using var langged = await harness.Client.SendAsync(withLang);

        Assert.Equal(etag, langged.Headers.ETag?.Tag);

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/v1/content/onboarding/driver");
        revalidate.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, etag);

        using var notModified = await harness.Client.SendAsync(revalidate);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    /// <summary>
    /// An unknown audience is a 400 naming the two that exist — <c>{audience}</c> is an enumeration, and
    /// a client sending a role or a platform there gets told so.
    /// </summary>
    [Theory]
    [InlineData("fleet_owner")]
    [InlineData("ios")]
    [InlineData("DRIVERS")]
    public async Task An_unknown_audience_is_rejected(string audience)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync($"/v1/content/onboarding/{audience}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.Contains(
            "driver, passenger",
            problem.GetProperty("errors").GetProperty("audience")[0].GetString());
    }

    /// <summary>Casing and surrounding whitespace are normalised rather than refused.</summary>
    [Fact]
    public async Task The_audience_segment_is_case_insensitive()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync("/v1/content/onboarding/Driver");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <c>Content:AssetBaseUrl</c> turns a bundled asset key into a URL, which is how the artwork moves
    /// to a CDN without an app release.
    /// </summary>
    [Fact]
    public async Task An_asset_base_url_absolutises_the_illustration_reference()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Content:AssetBaseUrl"] = "https://cdn.mageride.lk/img/" });

        var response = await harness.GetAsync<OnboardingResponse>("/v1/content/onboarding/passenger");

        Assert.Equal(
            "https://cdn.mageride.lk/img/onboarding/passenger-map", response.Slides[0].IllustrationRef);
    }
}
