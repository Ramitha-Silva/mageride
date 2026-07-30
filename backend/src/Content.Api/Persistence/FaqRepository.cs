using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Content.Persistence;

/// <summary>One FAQ article, in one language (<c>content.faq_articles</c>, US-16.1).</summary>
internal sealed record FaqRow(Guid Id, string Category, string Title, string Body, int SortOrder);

/// <summary>
/// <c>content.faq_articles</c> — the authored source behind `GET /v1/support/faq`.
/// </summary>
/// <remarks>
/// Read-only here. C053's fence is "FAQ content is owned by content-svc; support-svc serves and
/// filters it, it does not author it", and the authoring half has no screen in D2' and no route in
/// D3' — the day-0 set is migration 1902's twelve rows. Named in the C045 handoff rather than
/// invented: an editor endpoint with no screen and no key linking the three translations of one
/// article (the table has none) would be a guess at two things at once.
/// </remarks>
internal interface IFaqRepository
{
    /// <summary>
    /// Every article in one language, ordered as the app renders them.
    /// </summary>
    /// <remarks>
    /// <b>Not filtered by category here.</b> The whole language is read and cached, and
    /// <c>ContentQueries</c> applies the category to the cached rows — a cache keyed by a
    /// caller-supplied string is a cache a caller can collide or grow at will, and three entries hold
    /// every FAQ article on the platform. <c>ix_faq_articles_lookup</c> (1304) leads with
    /// <c>language</c>, so this is the same index scan the filtered query would have used.
    /// </remarks>
    /// <param name="limit">
    /// <c>Content:MaxFaqItems</c> + 1 is passed, so the caller can tell a full page from a truncated
    /// one and log it. Nothing here caps silently.
    /// </param>
    Task<IReadOnlyList<FaqRow>> ReadAsync(string language, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFaqRepository"/>
internal sealed class FaqRepository(INpgsqlConnectionFactory connections) : IFaqRepository
{
    public async Task<IReadOnlyList<FaqRow>> ReadAsync(
        string language, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FaqRow>(
            new CommandDefinition(
                """
                SELECT id, category, title, body, sort_order
                  FROM content.faq_articles
                 WHERE language = @Language
                 ORDER BY sort_order, category
                 LIMIT @Limit;
                """,
                new { Language = language, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}
