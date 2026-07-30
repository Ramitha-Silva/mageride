using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Content.Endpoints;
using MageRide.Content.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Content.Tests.Integration;

/// <summary>
/// <c>PUT /v1/admin/content/{key}</c>, <c>POST …/approve</c> and <c>GET …</c> — the D3' "versioned
/// template edit (approval workflow)", and this component's first fence and first definition of done:
/// <b>publishing a template missing a language is rejected with a clear error</b>.
/// </summary>
[Collection<ContentCollection>]
public sealed class TemplatePublishTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The first definition of done. Each language in turn is the missing one, and the rejection names
    /// it in a field a form can attach the message to.
    /// </summary>
    [Theory]
    [InlineData("si")]
    [InlineData("ta")]
    [InlineData("en")]
    public async Task Publishing_without_one_language_is_rejected_naming_that_language(string missing)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        var body = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["si"] = "සිංහල",
            ["ta"] = "தமிழ்",
            ["en"] = "English",
        };

        body.Remove(missing);

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}", new { bodyByLang = body }, admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);

        var errors = problem.GetProperty("errors");
        var message = errors.GetProperty($"bodyByLang.{missing}")[0].GetString();

        Assert.Contains("required", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D-26", message);

        // Nothing was written: a rejected publish leaves the previous version current.
        await using var connection = await harness.OpenAsync();

        var versions = await connection.ExecuteScalarAsync<int>(
            "SELECT coalesce(max(version), 0) FROM content.notification_templates WHERE template_key = @Key;",
            new { Key = key });

        Assert.Equal(1, versions);
    }

    /// <summary>
    /// A blank string is a missing language. It passes every shape check there is and produces a
    /// notification with no text in exactly one language.
    /// </summary>
    [Fact]
    public async Task A_whitespace_only_language_counts_as_missing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = new { si = "   ", ta = "தமிழ்", en = "English" } },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (_, problem) = await ContentHarness.ProblemAsync(response);

        Assert.True(problem.GetProperty("errors").TryGetProperty("bodyByLang.si", out _));
    }

    /// <summary>
    /// Half a title is refused. The field is optional as a whole, which is what makes a two-language
    /// title the easiest D-26 violation to commit.
    /// </summary>
    [Fact]
    public async Task A_title_supplied_in_two_languages_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new
            {
                bodyByLang = TemplateReadTests.Trilingual("Body"),
                titleByLang = new { si = "සිංහල", en = "English" },
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (_, problem) = await ContentHarness.ProblemAsync(response);

        Assert.True(problem.GetProperty("errors").TryGetProperty("titleByLang.ta", out _));
    }

    /// <summary>
    /// The three languages of one template interpolate the same variables, or the publish is refused.
    /// The Sinhala SMS that lost <c>{{link}}</c> in translation is the failure this prevents.
    /// </summary>
    [Fact]
    public async Task A_language_that_drops_a_placeholder_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new
            {
                bodyByLang = new
                {
                    si = "ඔබේ පාර්සලය මාර්ගයේ ය.",
                    ta = "உங்கள் பொதி வழியில் உள்ளது: {{link}}",
                    en = "Your package is on the way: {{link}}",
                },
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);

        var message = problem.GetProperty("errors").GetProperty("bodyByLang.si")[0].GetString();

        Assert.Contains("{{link}}", message);
    }

    /// <summary>A placeholder no other language has is refused too: nothing would supply a value.</summary>
    [Fact]
    public async Task A_language_that_invents_a_placeholder_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new
            {
                bodyByLang = new
                {
                    si = "සිංහල {{driverName}}",
                    ta = "தமிழ்",
                    en = "English",
                },
            },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (_, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Contains(
            "{{driverName}}",
            problem.GetProperty("errors").GetProperty("bodyByLang.si")[0].GetString());
    }

    /// <summary>
    /// The whole workflow: a draft, the history that makes it visible, the approval that publishes it,
    /// and both actors on the row.
    /// </summary>
    [Fact]
    public async Task A_draft_is_visible_then_approved_and_becomes_current()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key, body: "Version one {{name}}");

        var author = await harness.CreateAdminAsync();
        var approver = await harness.CreateAdminAsync(superAdmin: true);

        using var draft = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new
            {
                bodyByLang = TemplateReadTests.Trilingual("Version two {{name}}"),
                titleByLang = TemplateReadTests.Trilingual("A title"),
            },
            author.Bearer);

        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        var written = (await draft.Content.ReadFromJsonAsync<TemplateVersionRefResponse>())!;

        Assert.Equal(2, written.Version);
        Assert.Equal("draft", written.Status);
        Assert.Null(written.ApprovedAt);

        // The history is what makes the draft approvable: newest first, `current` still version 1.
        var history = await harness.GetAsync<TemplateHistoryResponse>(
            $"/v1/admin/content/{key}", author.Bearer);

        Assert.Equal(1, history.Current);
        Assert.Equal(2, history.Versions.Count);
        Assert.Equal(2, history.Versions[0].Version);
        Assert.Equal("draft", history.Versions[0].Status);
        Assert.Equal("Version two {{name}}", history.Versions[0].BodyByLang.En);
        Assert.Equal(["name"], history.Versions[0].Placeholders);

        using var approved = await harness.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 2 }, approver.Bearer);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var published = (await approved.Content.ReadFromJsonAsync<TemplateVersionRefResponse>())!;

        Assert.Equal("published", published.Status);
        Assert.Equal(harness.Clock.GetUtcNow(), published.ApprovedAt);

        // The render path now serves version 2 — in all three languages, and immediately, because the
        // approval purged the cache it had already filled.
        foreach (var language in new[] { "si", "ta", "en" })
        {
            var served = await harness.GetAsync<NotificationTemplateResponse>(
                $"/v1/content/templates/{key}?lang={language}",
                internalKey: ContentHarness.InternalApiKey);

            Assert.Equal(2, served.Version);
            Assert.StartsWith("Version two", served.Body);
            Assert.NotNull(served.Title);
        }

        // D-35's four eyes: the author drafted it, the approver published it, and the row records both.
        await using var connection = await harness.OpenAsync();

        var rows = await connection.QueryAsync<(Guid CreatedBy, Guid ApprovedBy, string Status)>(
            """
            SELECT created_by, approved_by, status
              FROM content.notification_templates
             WHERE template_key = @Key AND version = 2;
            """,
            new { Key = key });

        Assert.Equal(3, rows.Count());
        Assert.All(rows, row =>
        {
            Assert.Equal(author.Id, row.CreatedBy);
            Assert.Equal(approver.Id, row.ApprovedBy);
            Assert.Equal("published", row.Status);
        });
    }

    /// <summary>
    /// One approver per version: a second approval is a 409 rather than a silent overwrite of who
    /// signed it off.
    /// </summary>
    [Fact]
    public async Task Approving_a_published_version_twice_is_a_conflict()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var draft = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = TemplateReadTests.Trilingual("Version two") },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        using var first = await harness.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 2 }, admin.Bearer);
        using var second = await harness.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 2 }, admin.Bearer);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("conflict", (await ContentHarness.ProblemAsync(second)).Code);
    }

    /// <summary>Approving a version that does not exist is a 404, told apart from the 409 above.</summary>
    [Fact]
    public async Task Approving_a_missing_version_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PostAsync(
            $"/v1/admin/content/{key}/approve", new { version = 99 }, admin.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A key no migration created is a 404, not a new template. A template key only means something
    /// if a service renders it, and that pairing ships with the service (C005's own note on the four
    /// seeded keys).
    /// </summary>
    [Fact]
    public async Task An_unknown_key_cannot_be_created_through_the_admin_route()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{ContentHarness.NextTemplateKey()}",
            new { bodyByLang = TemplateReadTests.Trilingual("Brand new") },
            admin.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var (_, problem) = await ContentHarness.ProblemAsync(response);

        Assert.Contains("migration", problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>Content:PublishOnEdit</c> collapses the workflow into one step, and the response says so
    /// rather than leaving a portal to assume.
    /// </summary>
    [Fact]
    public async Task PublishOnEdit_makes_an_edit_live_immediately()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Content:PublishOnEdit"] = "true" });

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key, body: "Version one");

        var admin = await harness.CreateAdminAsync();

        using var response = await harness.PutAsync(
            $"/v1/admin/content/{key}",
            new { bodyByLang = TemplateReadTests.Trilingual("Version two") },
            admin.Bearer);

        var written = (await response.Content.ReadFromJsonAsync<TemplateVersionRefResponse>())!;

        Assert.Equal("published", written.Status);
        Assert.Equal(harness.Clock.GetUtcNow(), written.ApprovedAt);

        var served = await harness.GetAsync<NotificationTemplateResponse>(
            $"/v1/content/templates/{key}?lang=en", internalKey: ContentHarness.InternalApiKey);

        Assert.Equal(2, served.Version);
    }

    /// <summary>
    /// The authoring surface is Admin and Super Admin only. The other four back-office roles have no
    /// editorial cell in URD §2.3's content row, and a CSR rewriting every push on the platform is not
    /// a permission any spec grants.
    /// </summary>
    [Fact]
    public async Task Only_admin_and_super_admin_may_author()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ContentHarness.StartAsync(postgres, redis);

        var key = ContentHarness.NextTemplateKey();
        await harness.SeedTemplateAsync(key);

        var body = new { bodyByLang = TemplateReadTests.Trilingual("Rewritten") };

        var refused = new[]
        {
            harness.Tokens.SupportCsr(Guid.NewGuid()),
            harness.Tokens.Driver(Guid.NewGuid()),
            harness.Tokens.Passenger(Guid.NewGuid()),
        };

        foreach (var bearer in refused)
        {
            using var response = await harness.PutAsync($"/v1/admin/content/{key}", body, bearer);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/admin/content/{key}")
        {
            Content = JsonContent.Create(body),
        };

        using var anonymous = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
