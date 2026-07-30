using System.Net;
using Dapper;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>GET /v1/content/templates/{key}</c> — the D-26 render path notification-svc calls (D6' I-29.2).
/// </summary>
[Collection<ContentCollection>]
public sealed class TemplateReadTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The four seeded keys resolve in all three languages, with the real Sinhala and Tamil the
    /// §20 / I-29.2 seed carries.
    /// </summary>
    [Theory]
    [InlineData("ride_offer", "si", "නව ගමන් ඉල්ලීමක්")]
    [InlineData("ride_offer", "ta", "புதிய பயண கோரிக்கை")]
    [InlineData("ride_offer", "en", "New ride request")]
    [InlineData("package_on_the_way", "si", "ඔබේ පාර්සලය")]
    [InlineData("proxy_ride_link", "ta", "உங்களுக்காக ஒரு பயணம்")]
    [InlineData("pickup_confirm_link", "en", "Confirm your pickup location")]
    public async Task A_seeded_template_resolves_in_every_language(string key, string language, string expected)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var template = await harness.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang={language}", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(key, template.Key);
        Assert.Equal(language, template.Language);
        Assert.Equal(1, template.Version);
        Assert.Contains(expected, template.Body + template.Title);
    }

    /// <summary>
    /// The placeholder set is reported, so notification-svc can check it has every variable before it
    /// renders — and the three languages of a seeded template carry the same one.
    /// </summary>
    [Fact]
    public async Task The_placeholders_are_reported_and_agree_across_languages()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        // §20's ride_offer interpolates the two the dispatch push needs.
        var english = await harness.GetAsync<NotificationTemplateResponse>(
            "/v1/content/templates/ride_offer?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(["pickup", "dropoff"], english.Placeholders);

        foreach (var language in new[] { "si", "ta" })
        {
            var other = await harness.GetAsync<NotificationTemplateResponse>(
                $"/v1/content/templates/ride_offer?lang={language}",
                internalKey: ContentHarness.InternalApiKey);

            Assert.Equal(english.Placeholders.Order(), other.Placeholders.Order());
        }

        // I-29.2's three SMS templates each carry the tracking link, and the Sinhala and Tamil bodies
        // carry it too — the case TemplatePlaceholders.RequireConsistent exists to protect.
        var sms = await harness.GetAsync<NotificationTemplateResponse>(
            "/v1/content/templates/pickup_confirm_link?lang=si",
            internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(["link"], sms.Placeholders);
    }

    /// <summary>
    /// An unsupported or absent <c>lang</c> falls back to English rather than 404ing, which is what
    /// <c>content.yaml</c> promises. A device locale (<c>si-LK</c>) is honoured, not fallen back from.
    /// </summary>
    [Theory]
    [InlineData("", "en")]
    [InlineData("fr", "en")]
    [InlineData("SI", "si")]
    [InlineData("si-LK", "si")]
    [InlineData("ta_IN", "ta")]
    public async Task An_unsupported_language_falls_back_and_a_device_locale_resolves(
        string requested, string expected)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var template = await harness.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/ride_offer?lang={requested}",
            internalKey: ContentHarness.InternalApiKey);

        // The response says which language it actually served, so the caller never has to assume.
        Assert.Equal(expected, template.Language);
    }

    /// <summary>An unknown key is a 404 — there is nothing to fall back to.</summary>
    [Fact]
    public async Task An_unknown_key_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync(
            "/v1/content/templates/no_such_template", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not-found", (await ContentHarness.ProblemAsync(response)).Code);
    }

    /// <summary>
    /// The internal plane is guarded here rather than at the edge, because D3' prints this path under
    /// <c>/v1/content</c> and the gateway forwards that prefix.
    /// </summary>
    [Fact]
    public async Task The_render_path_is_refused_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        using var noKey = await harness.GetAsync("/v1/content/templates/ride_offer");
        using var wrongKey = await harness.GetAsync(
            "/v1/content/templates/ride_offer", internalKey: "not-the-key");

        // Even a valid bearer is not a substitute: this is a service-to-service surface.
        using var bearer = await harness.GetAsync(
            "/v1/content/templates/ride_offer", harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bearer.StatusCode);
    }

    /// <summary>
    /// A draft is invisible to the render path: the published version stays current until it is
    /// approved.
    /// </summary>
    [Fact]
    public async Task A_draft_never_shadows_the_published_version()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key, body: "Version one for {{name}}");

        var admin = await harness.CreateAdminAsync();

        using var draft = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = Trilingual("Version two for {{name}}") },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        var served = await harness.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(1, served.Version);
        Assert.Equal("Version one for {{name}}", served.Body);

        // And the draft really is version 2 in the database, unapproved.
        await using var connection = await harness.OpenAsync();

        var statuses = await connection.QueryAsync<string>(
            "SELECT status FROM content.notification_templates WHERE template_key = @Key AND version = 2;",
            new { Key = key });

        Assert.Equal(3, statuses.Count());
        Assert.All(statuses, status => Assert.Equal("draft", status));
    }

    internal static object Trilingual(string text) => new
    {
        si = $"{text} [si]",
        ta = $"{text} [ta]",
        en = text,
    };
}
