using MageRide.Shared.Errors;
using MageRide.Support.Configuration;
using MageRide.Support.Domain;
using MageRide.Support.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Support.Faq;

/// <summary>
/// A FAQ answer and the language it was actually served in.
/// </summary>
/// <param name="Language">
/// Not necessarily the language asked for. The response carries it so a client can say "shown in
/// English" rather than silently rendering the wrong script.
/// </param>
public sealed record FaqAnswer<T>(string Language, T Value);

/// <summary>US-16.1 — the in-app FAQ, served from content-svc's articles.</summary>
public interface IFaqService
{
    Task<FaqAnswer<IReadOnlyList<FaqRow>>> ListAsync(
        string? language, string? category, CancellationToken cancellationToken);

    Task<FaqAnswer<FaqRow>> GetAsync(Guid articleId, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IFaqService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The fallback order is requested → <c>en</c> → <c>si</c> → <c>ta</c>, and the answer always says
/// which one it served.</b> That is the definition of done's "documented fallback order", and it is
/// documented in three places that agree: <c>support.yaml</c>'s service description,
/// <see cref="Languages.FallbackOrder"/>, and here. English is first among the alternatives for
/// content-svc's reason — it is the language every operator, CSR and developer on this platform
/// reads — while <see cref="Languages.All"/> stays Sinhala-first for presentation (AL-26). Two
/// different questions; one shared order would answer one of them wrongly.
/// </para>
/// <para>
/// <b>The whole order is walked, not just the first alternative.</b> "Requested, else English" leaves
/// a Tamil reader with nothing at all when an article exists only in Sinhala — which is a help page
/// that fails exactly the user least likely to find help elsewhere.
/// </para>
/// <para>
/// <b>No cache.</b> content-svc caches these rows because it serves them on the notification render
/// path, where the budget is a ride offer; here the reader is a person who has just opened Help, the
/// query is an index scan over a table with twelve rows in it, and a second copy of the same cache
/// in another process would mean the same edit becoming visible at two different times on two
/// screens.
/// </para>
/// </remarks>
internal sealed class FaqService(
    IFaqRepository faq,
    IOptions<SupportOptions> options,
    ILogger<FaqService> logger) : IFaqService
{
    private readonly SupportOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FaqAnswer<IReadOnlyList<FaqRow>>> ListAsync(
        string? language, string? category, CancellationToken cancellationToken)
    {
        var requested = Languages.Resolve(language);
        var filter = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        // The fallback is evaluated against the *filtered* answer, not against the language as a
        // whole. A Tamil reader asking about wallet top-ups when only the English article exists
        // should be given the English one; falling back only when a language is entirely absent
        // would hand them an empty list and no explanation.
        foreach (var candidate in Languages.Preference(requested))
        {
            var rows = await faq.ListAsync(candidate, _options.MaxFaqItems + 1, cancellationToken);

            if (rows.Count > _options.MaxFaqItems)
            {
                // No silent caps: one row over the limit is asked for precisely so a full page can
                // be told from a truncated one without a second count query.
                logger.LogWarning(
                    "FAQ read for {Language} hit Support:MaxFaqItems ({Max}); the answer is truncated.",
                    candidate,
                    _options.MaxFaqItems);

                rows = [.. rows.Take(_options.MaxFaqItems)];
            }

            var matching = filter is null
                ? rows
                : [.. rows.Where(row => string.Equals(row.Category, filter, StringComparison.Ordinal))];

            if (matching.Count == 0)
            {
                continue;
            }

            if (!string.Equals(candidate, requested, StringComparison.Ordinal))
            {
                // Reaching this at all means content-svc's day-0 set is incomplete for a language
                // the platform promises (D-26), so it is a warning rather than a silent resolution.
                logger.LogWarning(
                    "No FAQ articles in {Requested} for category {Category}; served {Served} instead.",
                    requested,
                    filter ?? "(all)",
                    candidate);
            }

            return new FaqAnswer<IReadOnlyList<FaqRow>>(candidate, matching);
        }

        // Every language is empty for this filter. An empty list rather than a 404: the FAQ surface
        // is a list, and "there are no articles about that" is an answer a screen can render.
        logger.LogWarning(
            "No FAQ articles at all for category {Category} in any language.", filter ?? "(all)");

        return new FaqAnswer<IReadOnlyList<FaqRow>>(requested, []);
    }

    public async Task<FaqAnswer<FaqRow>> GetAsync(
        Guid articleId, string? language, CancellationToken cancellationToken)
    {
        var requested = Languages.Resolve(language);

        var article = await faq.FindAsync(articleId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, $"No FAQ article {articleId}.");

        if (string.Equals(article.Language, requested, StringComparison.Ordinal))
        {
            return new FaqAnswer<FaqRow>(requested, article);
        }

        // The id names one row in one language, so serving another language means finding the
        // sibling. `IFaqRepository.ListTranslationsAsync` derives the link from (category,
        // sort_order) and says why — `content.faq_articles` has no key joining the three
        // translations of one article, and adding one is content-svc's decision to make.
        var translations = await faq.ListTranslationsAsync(
            article.Category, article.SortOrder, cancellationToken);

        foreach (var candidate in Languages.Preference(requested))
        {
            var match = translations.FirstOrDefault(
                row => string.Equals(row.Language, candidate, StringComparison.Ordinal));

            if (match is not null)
            {
                if (!string.Equals(candidate, requested, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "FAQ article {ArticleId} has no {Requested} translation; served {Served} instead.",
                        articleId,
                        requested,
                        candidate);
                }

                return new FaqAnswer<FaqRow>(candidate, match);
            }
        }

        // The row that was found by id is not in the fallback order — which can only happen if a
        // fourth language reached the table past `ck_faq_articles_language`. Serve it rather than
        // 404: the reader asked for this article and it exists.
        return new FaqAnswer<FaqRow>(article.Language, article);
    }
}
