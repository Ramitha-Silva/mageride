using Dapper;
using MageRide.Support.Endpoints;
using MageRide.Support.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Support.Tests.Integration;

/// <summary>
/// US-16.1 and the first definition of done: "FAQ returns articles in the requested language with a
/// documented fallback order".
/// </summary>
/// <remarks>
/// <b>Asserted against migration 1902's real seeded strings</b>, not against fixtures this suite
/// wrote. A fallback test over invented rows proves the test's own arrangement; these four topics —
/// wallet top-up, daily fee, vehicle registration, ride booking — are the day-0 content the app
/// actually serves, in the three scripts D-26 requires.
/// </remarks>
[Collection<SupportCollection>]
public sealed class FaqTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Each_language_is_served_in_its_own_script()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        var sinhala = await harness.GetAsync<FaqListResponse>("/v1/support/faq?lang=si", bearer);
        var tamil = await harness.GetAsync<FaqListResponse>("/v1/support/faq?lang=ta", bearer);
        var english = await harness.GetAsync<FaqListResponse>("/v1/support/faq?lang=en", bearer);

        Assert.Equal(4, sinhala.Items.Count);
        Assert.Equal(4, tamil.Items.Count);
        Assert.Equal(4, english.Items.Count);

        // Every item says which language it is, and it is the one asked for.
        Assert.All(sinhala.Items, item => Assert.Equal("si", item.Language));
        Assert.All(tamil.Items, item => Assert.Equal("ta", item.Language));

        // The seeded Sinhala and Tamil titles, verbatim — the point of the trilingual rule is that
        // these are real translations rather than the English string in three rows.
        Assert.Contains(sinhala.Items, item => item.Title.Contains("පසුම්බියට", StringComparison.Ordinal));
        Assert.Contains(tamil.Items, item => item.Title.Contains("பணப்பையை", StringComparison.Ordinal));
        Assert.Contains(english.Items, item => item.Title == "How do I top up my wallet?");

        // Three ids per topic, one per language — the three translations are sibling rows, not
        // columns (migration 1304's own table comment).
        Assert.Empty(sinhala.Items.Select(i => i.ArticleId).Intersect(tamil.Items.Select(i => i.ArticleId)));
    }

    [Theory]
    [InlineData("si-LK", "si")]
    [InlineData("ta_IN", "ta")]
    [InlineData("SI", "si")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    public async Task A_device_locale_resolves_to_a_language_or_to_English(string requested, string expected)
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        var answer = await harness.GetAsync<FaqListResponse>($"/v1/support/faq?lang={requested}", bearer);

        Assert.All(answer.Items, item => Assert.Equal(expected, item.Language));
    }

    [Fact]
    public async Task A_language_with_no_article_falls_back_and_says_which_one_it_served()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        // A topic that exists in English only — what a newly authored article looks like before its
        // translations land.
        var category = "c053_untranslated";

        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO content.faq_articles (category, language, title, body, sort_order)
                VALUES (@Category, 'en', 'How do I contact support?', 'Raise a ticket from Help.', 90);
                """,
                new { Category = category });
        }

        var tamil = await harness.GetAsync<FaqListResponse>(
            $"/v1/support/faq?lang=ta&category={category}", bearer);

        var article = Assert.Single(tamil.Items);

        // The documented order is requested → en → si → ta, and the answer says English rather than
        // pretending it served Tamil.
        Assert.Equal("en", article.Language);
        Assert.Equal("How do I contact support?", article.Title);
    }

    [Fact]
    public async Task An_article_read_in_another_language_serves_its_translation()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        var english = await harness.GetAsync<FaqListResponse>("/v1/support/faq?lang=en&category=wallet", bearer);
        var wallet = Assert.Single(english.Items);

        // The id names an English row; `?lang=si` has to find its Sinhala sibling.
        var sinhala = await harness.GetAsync<FaqArticleResponse>(
            $"/v1/support/faq/{wallet.ArticleId}?lang=si", bearer);

        Assert.Equal("si", sinhala.Language);
        Assert.Equal("wallet", sinhala.Category);
        Assert.Contains("පසුම්බිය", sinhala.Body, StringComparison.Ordinal);

        // The id returned is the row actually served, so a client that bookmarks the answer
        // bookmarks what it read.
        Assert.NotEqual(wallet.ArticleId, sinhala.ArticleId);
    }

    [Fact]
    public async Task An_article_read_in_its_own_language_is_served_unchanged()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        var list = await harness.GetAsync<FaqListResponse>("/v1/support/faq?lang=ta&category=daily_fee", bearer);
        var listed = Assert.Single(list.Items);

        var article = await harness.GetAsync<FaqArticleResponse>(
            $"/v1/support/faq/{listed.ArticleId}?lang=ta", bearer);

        Assert.Equal(listed.ArticleId, article.ArticleId);
        Assert.Equal("ta", article.Language);
        Assert.NotEmpty(article.Body);
    }

    [Fact]
    public async Task An_unknown_article_is_404_and_an_anonymous_read_is_401()
    {
        await using var harness = await SupportHarness.StartAsync(postgres);
        var (_, bearer) = await harness.CreatePassengerAsync();

        using var missing = await harness.GetAsync($"/v1/support/faq/{Guid.CreateVersion7()}", bearer);
        var (_, code, _) = await SupportHarness.ProblemAsync(missing);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not-found", code);

        using var anonymous = await harness.GetAsync("/v1/support/faq");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Nothing_in_this_service_can_write_an_article()
    {
        // The C053 fence, asserted as a property of the type rather than of a code path: FAQ content
        // is content-svc's, and `IFaqRepository` offers no way to change it. A method added here
        // later — an editor, a "seed if missing", a cache warm that upserts — fails this test.
        var methods = typeof(MageRide.Support.Persistence.IFaqRepository).GetMethods();

        Assert.Equal(3, methods.Length);
        Assert.All(methods, method =>
            Assert.True(
                method.Name is "ListAsync" or "FindAsync" or "ListTranslationsAsync",
                $"IFaqRepository.{method.Name} is not a read. FAQ content is content-svc's (C045)."));

        await Task.CompletedTask;
    }
}
