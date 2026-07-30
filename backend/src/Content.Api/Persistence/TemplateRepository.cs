using Dapper;
using MageRide.Content.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Content.Persistence;

/// <summary>One language of one template version.</summary>
internal sealed record TemplateText(int Version, string? Subject, string Body);

/// <summary>
/// The current published version of one template key, in every language it exists in.
/// </summary>
/// <remarks>
/// The whole key is read and cached together rather than one language at a time. Three reasons, and
/// the third is the one that matters: it is one query instead of three; a language fallback needs
/// the siblings anyway; and a cache keyed by (key, language) could hold Sinhala from before an edit
/// and English from after it, so a driver and a passenger on the same ride would be told different
/// things by "the same" template.
/// </remarks>
internal sealed record TemplateSet(string Key, IReadOnlyDictionary<string, TemplateText> ByLanguage);

/// <summary>One row of the admin version history.</summary>
internal sealed record TemplateHistoryRow(
    int Version,
    string Language,
    string Status,
    string? Subject,
    string Body,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset CreatedAt);

/// <summary>What an approval did.</summary>
/// <param name="Rows">How many language rows moved to <c>published</c>.</param>
internal sealed record ApprovalOutcome(int Version, DateTimeOffset ApprovedAt, int Rows);

/// <summary>
/// <c>content.notification_templates</c> — the D-26 template store.
/// </summary>
internal interface ITemplateRepository
{
    /// <summary>
    /// The current published version of <paramref name="key"/>, or <see langword="null"/> if the key
    /// has no published version at all.
    /// </summary>
    Task<TemplateSet?> ReadPublishedAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every version of <paramref name="key"/>, drafts included, oldest first.</summary>
    Task<IReadOnlyList<TemplateHistoryRow>> ReadHistoryAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the next version of <paramref name="key"/> in all three languages, in one transaction.
    /// </summary>
    /// <param name="published">
    /// <see langword="true"/> publishes it immediately (<c>Content:PublishOnEdit</c>);
    /// <see langword="false"/> leaves a draft for <c>POST …/approve</c>.
    /// </param>
    Task<ApprovalOutcome> InsertVersionAsync(
        string key,
        TrilingualText body,
        TrilingualText? title,
        Guid author,
        bool published,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a drafted version, or returns <see langword="null"/> when there is no such draft.
    /// </summary>
    Task<ApprovalOutcome?> ApproveAsync(
        string key, int version, Guid approver, CancellationToken cancellationToken);

    /// <summary>Whether the key exists in any status — the 404 half of the admin routes.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>The status of one version, or <see langword="null"/> if it does not exist.</summary>
    Task<string?> ReadVersionStatusAsync(string key, int version, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITemplateRepository"/>
/// <remarks>
/// <para>
/// <b>"Current" is the highest <c>published</c> version, resolved per (key, language).</b> Migration
/// 1307's deferred trigger guarantees a version exists in all three languages or in none, so the
/// three are normally the same number; resolving per language is what C005's
/// <c>ix_notification_templates_current</c> comment prescribes and it degrades honestly if a row is
/// ever written around this service.
/// </para>
/// <para>
/// <b>A draft is invisible to the render path by construction</b> — the <c>status = 'published'</c>
/// predicate is inside the CTE that picks the version, not a filter over its result, so an
/// unapproved edit cannot become the maximum and hide the published version behind it.
/// </para>
/// </remarks>
internal sealed class TemplateRepository(INpgsqlConnectionFactory connections, TimeProvider clock)
    : ITemplateRepository
{
    private const string PublishedSql =
        """
        WITH current AS (
          SELECT language, max(version) AS version
            FROM content.notification_templates
           WHERE template_key = @Key AND status = 'published'
           GROUP BY language)
        SELECT t.language, t.version, t.subject, t.body
          FROM content.notification_templates t
          JOIN current c ON c.language = t.language AND c.version = t.version
         WHERE t.template_key = @Key;
        """;

    public async Task<TemplateSet?> ReadPublishedAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(string Language, int Version, string? Subject, string Body)>(
            new CommandDefinition(PublishedSql, new { Key = key }, cancellationToken: cancellationToken));

        var byLanguage = rows
            .Where(row => Languages.IsKnown(row.Language))
            .ToDictionary(
                row => row.Language,
                row => new TemplateText(row.Version, row.Subject, row.Body),
                StringComparer.Ordinal);

        return byLanguage.Count == 0 ? null : new TemplateSet(key, byLanguage);
    }

    public async Task<IReadOnlyList<TemplateHistoryRow>> ReadHistoryAsync(
        string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<TemplateHistoryRow>(
            new CommandDefinition(
                """
                SELECT version, language, status, subject, body, approved_at, approved_by, created_at
                  FROM content.notification_templates
                 WHERE template_key = @Key
                 ORDER BY version, language;
                """,
                new { Key = key },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<ApprovalOutcome> InsertVersionAsync(
        string key,
        TrilingualText body,
        TrilingualText? title,
        Guid author,
        bool published,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(body);

        var approvedAt = published ? clock.GetUtcNow() : (DateTimeOffset?)null;

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The primary key is the arbiter, not this read: under READ COMMITTED two concurrent edits
        // can both see the same maximum and both aim at version n+1, and one of them then violates
        // `(template_key, language, version)`. That violation becomes a 409 below rather than a
        // rollback nobody was told about — a lost edit must never look like a saved one. Holding a
        // lock instead would serialise the whole key for the duration of three INSERTs to spare an
        // admin a retry they will see once a year.
        var version = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT coalesce(max(version), 0) + 1
                  FROM content.notification_templates
                 WHERE template_key = @Key;
                """,
                new { Key = key },
                transaction,
                cancellationToken: cancellationToken));

        var rows = 0;

        try
        {
            foreach (var language in Languages.All)
            {
                rows += await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO content.notification_templates
                              (template_key, language, subject, body, version, status,
                               approved_by, approved_at, created_by)
                        VALUES (@Key, @Language, @Subject, @Body, @Version, @Status,
                                @ApprovedBy, @ApprovedAt, @Author);
                        """,
                        new
                        {
                            Key = key,
                            Language = language,
                            Subject = title?[language],
                            Body = body[language],
                            Version = version,
                            Status = published ? TemplateStatuses.Published : TemplateStatuses.Draft,
                            ApprovedBy = published ? author : (Guid?)null,
                            ApprovedAt = approvedAt,
                            Author = author,
                        },
                        transaction,
                        cancellationToken: cancellationToken));
            }

            // The trilingual trigger is DEFERRABLE INITIALLY DEFERRED, so it fires here and not on
            // the first INSERT. A violation at this point means the three-language loop above wrote
            // fewer than three rows, which would be a bug in this method rather than in the request.
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Version {version} of '{key}' was created by another edit while this one was being "
                + "written. Re-read the version history and submit again.");
        }

        return new ApprovalOutcome(version, approvedAt ?? clock.GetUtcNow(), rows);
    }

    public async Task<ApprovalOutcome?> ApproveAsync(
        string key, int version, Guid approver, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var approvedAt = clock.GetUtcNow();

        await using var connection = await connections.OpenAsync(cancellationToken);

        // One statement, so the three languages move together or not at all — a version published in
        // two languages is exactly what the fence forbids, and the deferred trigger would refuse the
        // commit anyway.
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE content.notification_templates
                   SET status = 'published', approved_by = @Approver, approved_at = @ApprovedAt
                 WHERE template_key = @Key AND version = @Version AND status = 'draft';
                """,
                new { Key = key, Version = version, Approver = approver, ApprovedAt = approvedAt },
                cancellationToken: cancellationToken));

        return rows == 0 ? null : new ApprovalOutcome(version, approvedAt, rows);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM content.notification_templates WHERE template_key = @Key);",
                new { Key = key },
                cancellationToken: cancellationToken));
    }

    public async Task<string?> ReadVersionStatusAsync(
        string key, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                SELECT status
                  FROM content.notification_templates
                 WHERE template_key = @Key AND version = @Version
                 ORDER BY language
                 LIMIT 1;
                """,
                new { Key = key, Version = version },
                cancellationToken: cancellationToken));
    }
}

/// <summary>
/// The <c>status</c> vocabulary of <c>content.notification_templates</c> (migration 1307).
/// </summary>
/// <remarks>
/// Three spellings that have to agree: <c>ck_notification_templates_status</c>, the
/// <c>TemplateStatus</c> enum in <c>backend/contracts/content.yaml</c>, and this.
/// </remarks>
internal static class TemplateStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";
}
