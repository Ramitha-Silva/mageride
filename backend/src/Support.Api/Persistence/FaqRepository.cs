using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Support.Persistence;

/// <summary>One FAQ article, in one language (<c>content.faq_articles</c>, US-16.1).</summary>
public sealed record FaqRow(Guid Id, string Category, string Title, string Body, string Language, int SortOrder);

/// <summary>
/// <c>content.faq_articles</c>, read-only — the source behind <c>GET /v1/support/faq</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface is the C053 fence, expressed as a type.</b> "FAQ content is owned by
/// content-svc; support-svc serves and filters it, it does not author it" — so every method here is
/// a <c>SELECT</c> and there is no code path in this service that can write, edit or delete an
/// article. A reviewer does not have to remember the rule; there is no method to call.
/// </para>
/// <para>
/// <b>Read directly rather than over HTTP to content-svc.</b> The platform's established shape for a
/// cross-context *read* — safety-svc reads <c>iam.users</c>, subscription-svc reads
/// <c>registry.vehicles</c>, ride-svc reads <c>rides.rides</c> — and the reason is availability: the
/// FAQ is the screen a user opens when something has already gone wrong, and putting a second
/// service between them and it means a content-svc outage takes out the help page. The outbox rule
/// in CLAUDE.md governs cross-service *state changes*, and nothing here changes state. content-svc
/// keeps ownership of the writes, which is the half the fence is about.
/// </para>
/// </remarks>
public interface IFaqRepository
{
    /// <summary>
    /// Every article in one language, ordered as the app renders them.
    /// </summary>
    /// <param name="limit">
    /// <c>Support:MaxFaqItems</c> + 1 is passed, so the caller can tell a full page from a truncated
    /// one and log it. Nothing here caps silently.
    /// </param>
    Task<IReadOnlyList<FaqRow>> ListAsync(string language, int limit, CancellationToken cancellationToken);

    /// <summary>One article by id, whatever language it happens to be written in.</summary>
    Task<FaqRow?> FindAsync(Guid articleId, CancellationToken cancellationToken);

    /// <summary>
    /// The siblings of an article — the same article in the other languages.
    /// </summary>
    /// <remarks>
    /// <b><c>content.faq_articles</c> has no key linking the three translations of one article</b>:
    /// they are sibling rows with a generated UUID each. C045 found the same hole and left it open
    /// on purpose ("adding an <c>article_key</c> and an editor is a decision about a screen and a
    /// column"), so this derives the link from <c>(category, sort_order)</c> — the pair 1902's seed
    /// makes unique per language, and the pair <c>ix_faq_articles_lookup</c> already leads with.
    /// It is a derivation and it is stated as one: the micro-change-set for a real
    /// <c>article_key</c> is raised again in the C053 handoff, and this method is the one place
    /// that changes when it lands.
    /// </remarks>
    Task<IReadOnlyList<FaqRow>> ListTranslationsAsync(
        string category, int sortOrder, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFaqRepository"/>
internal sealed class FaqRepository(INpgsqlConnectionFactory connections) : IFaqRepository
{
    private const string Columns = "id, category, title, body, language, sort_order";

    public async Task<IReadOnlyList<FaqRow>> ListAsync(
        string language, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FaqRow>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM content.faq_articles
                  WHERE language = @Language
                  ORDER BY sort_order, category
                  LIMIT @Limit;
                 """,
                new { Language = language, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FaqRow?> FindAsync(Guid articleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FaqRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM content.faq_articles WHERE id = @ArticleId;",
                new { ArticleId = articleId },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FaqRow>> ListTranslationsAsync(
        string category, int sortOrder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // ORDER BY id is not decoration: `(category, sort_order)` is a derived key, so it can in
        // principle match two rows in one language, and an unordered read would serve a different
        // one on each request — the article's body would change under a reader who did nothing.
        var rows = await connection.QueryAsync<FaqRow>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM content.faq_articles
                  WHERE category = @Category AND sort_order = @SortOrder
                  ORDER BY language, id;
                 """,
                new { Category = category, SortOrder = sortOrder },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}
