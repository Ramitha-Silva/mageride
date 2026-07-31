using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Transit.Gtfs;

/// <summary>The six values <c>ck_gtfs_feed_versions_status</c> admits (server_db_schema §27).</summary>
public static class FeedStatuses
{
    public const string Uploaded = "uploaded";
    public const string Validating = "validating";
    public const string Validated = "validated";
    public const string Failed = "failed";
    public const string Active = "active";
    public const string Archived = "archived";

    /// <summary>
    /// BR-32.2: only a <c>validated</c> version, or an <c>archived</c> one for rollback, may be
    /// activated. A <c>failed</c> version is kept for its report and can never go live (BR-32.3).
    /// </summary>
    public static bool IsActivatable(string status) =>
        status is Validated or Archived;
}

/// <summary>One row of <c>transit.gtfs_feed_versions</c>.</summary>
/// <param name="CountsJson">Raw <c>jsonb</c>; the per-file row counts SCR-AP-016's preview grid renders.</param>
/// <param name="ValidationReportJson">Raw <c>jsonb</c>, or null before validation has run.</param>
public sealed record FeedVersionRow(
    Guid FeedVersionId,
    string FileName,
    long FileSizeBytes,
    string Sha256,
    string? FeedInfoVersion,
    DateOnly? ServiceStart,
    DateOnly? ServiceEnd,
    string CountsJson,
    string Status,
    string? ValidationReportJson,
    string StorageKey,
    Guid UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ArchivedAt)
{
    public IReadOnlyDictionary<string, long> Counts() =>
        string.IsNullOrWhiteSpace(CountsJson)
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, long>>(CountsJson, GtfsJson.Counts)
              ?? new Dictionary<string, long>(StringComparer.Ordinal);

    public FeedValidationReport Report() =>
        string.IsNullOrWhiteSpace(ValidationReportJson)
            ? FeedValidationReport.Empty
            : JsonSerializer.Deserialize<FeedValidationReport>(ValidationReportJson, MageRideJson.StorageOptions)
              ?? FeedValidationReport.Empty;
}

/// <summary>The SCR-AP-016 ledger: <c>transit.gtfs_feed_versions</c> (AL-54, §27).</summary>
public interface IGtfsFeedVersionRepository
{
    /// <summary>BR-32.1's dedupe: the version an identical upload already produced, if any.</summary>
    Task<FeedVersionRow?> FindBySha256Async(string sha256, CancellationToken cancellationToken);

    Task<FeedVersionRow?> FindAsync(Guid feedVersionId, CancellationToken cancellationToken);

    /// <summary>
    /// Records an accepted upload as <c>uploaded</c>, awaiting validation. Takes the caller's
    /// transaction so the row and its <c>audit.events</c> entry commit together (D-35).
    /// </summary>
    Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeedVersionRow row,
        CancellationToken cancellationToken);

    /// <summary>Newest first, over-fetched by one so the caller can tell whether a page follows.</summary>
    Task<IReadOnlyList<FeedVersionRow>> ListAsync(
        PageRequest page, (DateTimeOffset UploadedAt, Guid FeedVersionId)? after, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the next feed awaiting validation, marking it <c>validating</c>. Null when there is
    /// none.
    /// </summary>
    Task<FeedVersionRow?> ClaimForValidationAsync(TimeSpan staleAfter, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the verdict, the counts, the report and the preview fields (BR-32.1).
    /// </summary>
    /// <remarks>
    /// Takes the caller's connection and transaction so the verdict and its <c>audit.events</c>
    /// row commit together — a status that moved with no audit row behind it is a state change
    /// nobody can explain (D-35).
    /// </remarks>
    Task CompleteValidationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedVersionId,
        FeedValidationResult result,
        CancellationToken cancellationToken);

    /// <summary>The ids the live feed uses, for BR-32.1's stable-id warnings.</summary>
    Task<ActiveFeedIdentity> ActiveIdentityAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Every write here is a whole-row statement against one table; the interesting concurrency is in
/// <see cref="GtfsActivationService"/>, which owns the swap. The one exception is
/// <see cref="ClaimForValidationAsync"/> — a claim, not a read, so two replicas running the
/// validation worker cannot validate one upload twice.
/// </remarks>
internal sealed class GtfsFeedVersionRepository(INpgsqlConnectionFactory connections) : IGtfsFeedVersionRepository
{
    /// <summary>Every column, in the order <see cref="FeedVersionRow"/> declares them.</summary>
    internal const string Columns = """
        feed_version_id AS FeedVersionId, file_name AS FileName, file_size_bytes AS FileSizeBytes,
        sha256 AS Sha256, feed_info_version AS FeedInfoVersion, service_start AS ServiceStart,
        service_end AS ServiceEnd, counts::text AS CountsJson, status AS Status,
        validation_report::text AS ValidationReportJson, storage_key AS StorageKey,
        uploaded_by AS UploadedBy, uploaded_at AS UploadedAt, activated_at AS ActivatedAt,
        archived_at AS ArchivedAt
        """;

    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<FeedVersionRow?> FindBySha256Async(string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FeedVersionRow>(new CommandDefinition(
            $"SELECT {Columns} FROM transit.gtfs_feed_versions WHERE sha256 = @Sha256;",
            new { Sha256 = sha256 },
            cancellationToken: cancellationToken));
    }

    public async Task<FeedVersionRow?> FindAsync(Guid feedVersionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FeedVersionRow>(new CommandDefinition(
            $"SELECT {Columns} FROM transit.gtfs_feed_versions WHERE feed_version_id = @FeedVersionId;",
            new { FeedVersionId = feedVersionId },
            cancellationToken: cancellationToken));
    }

    public async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeedVersionRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(row);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO transit.gtfs_feed_versions
              (feed_version_id, file_name, file_size_bytes, sha256, counts, status, storage_key,
               uploaded_by, uploaded_at)
            VALUES
              (@FeedVersionId, @FileName, @FileSizeBytes, @Sha256, '{}'::jsonb, @Status, @StorageKey,
               @UploadedBy, @UploadedAt);
            """,
            new
            {
                row.FeedVersionId,
                row.FileName,
                row.FileSizeBytes,
                row.Sha256,
                row.Status,
                row.StorageKey,
                row.UploadedBy,
                row.UploadedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FeedVersionRow>> ListAsync(
        PageRequest page, (DateTimeOffset UploadedAt, Guid FeedVersionId)? after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Keyed on (uploaded_at, feed_version_id) rather than on uploaded_at alone: two uploads in
        // the same millisecond would otherwise straddle a page boundary and one of them would
        // never be shown.
        var rows = await connection.QueryAsync<FeedVersionRow>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM transit.gtfs_feed_versions
              WHERE @After::timestamptz IS NULL
                 OR (uploaded_at, feed_version_id) < (@After::timestamptz, @AfterId::uuid)
              ORDER BY uploaded_at DESC, feed_version_id DESC
              LIMIT @Limit;
             """,
            new
            {
                After = after?.UploadedAt,
                AfterId = after?.FeedVersionId,
                Limit = page.OverfetchLimit,
            },
            cancellationToken: cancellationToken));

        return rows.AsList();
    }

    public async Task<FeedVersionRow?> ClaimForValidationAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // FOR UPDATE SKIP LOCKED so two replicas take two different uploads rather than blocking
        // on one. The `validating` arm reclaims a feed whose validator died: there is no
        // `validation_started_at` column to age it by, so it is aged by `uploaded_at`, which is
        // never later than the claim and is therefore a safe over-estimate of how long it has run.
        return await connection.QuerySingleOrDefaultAsync<FeedVersionRow>(new CommandDefinition(
            $"""
             UPDATE transit.gtfs_feed_versions
                SET status = '{FeedStatuses.Validating}'
              WHERE feed_version_id = (
                    SELECT feed_version_id
                      FROM transit.gtfs_feed_versions
                     WHERE status = '{FeedStatuses.Uploaded}'
                        OR (status = '{FeedStatuses.Validating}' AND uploaded_at < now() - @StaleAfter::interval)
                     ORDER BY uploaded_at
                     LIMIT 1
                       FOR UPDATE SKIP LOCKED)
             RETURNING {Columns};
             """,
            new { StaleAfter = staleAfter },
            cancellationToken: cancellationToken));
    }

    public async Task CompleteValidationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedVersionId,
        FeedValidationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(result);

        await using var command = new NpgsqlCommand(
            """
            UPDATE transit.gtfs_feed_versions
               SET status = $2,
                   counts = $3::jsonb,
                   validation_report = $4::jsonb,
                   feed_info_version = $5,
                   service_start = $6,
                   service_end = $7
             WHERE feed_version_id = $1
               AND status = 'validating';
            """,
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = feedVersionId });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = result.Failed ? FeedStatuses.Failed : FeedStatuses.Validated,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = JsonSerializer.Serialize(result.Counts, GtfsJson.Counts),
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = JsonSerializer.Serialize(result.Report, MageRideJson.StorageOptions),
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)result.FeedInfoVersion ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Date,
            Value = result.ServiceStart is { } start ? start : (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Date,
            Value = result.ServiceEnd is { } end ? end : (object)DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ActiveFeedIdentity> ActiveIdentityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Read from the live tables rather than from the previous version's stored zip: BR-32.1
        // compares against "the currently active feed version", and the live tables *are* it —
        // re-parsing a 200 MB archive to learn a set of ids the database already holds would be
        // the same answer, slower and one swap out of date.
        //
        // Gated on a feed actually being active, because BR-32.1 says "the currently active feed
        // version" and AL-55's no-coverage state has none. Without the gate, the first upload
        // after every feed was archived would be warned that every id in a dataset nobody is
        // serving had "disappeared".
        var routes = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT route_id FROM transit.gtfs_routes
             WHERE EXISTS (SELECT 1 FROM transit.gtfs_feed_versions WHERE status = 'active');
            """,
            cancellationToken: cancellationToken));

        var stops = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT stop_id FROM transit.gtfs_stops
             WHERE EXISTS (SELECT 1 FROM transit.gtfs_feed_versions WHERE status = 'active');
            """,
            cancellationToken: cancellationToken));

        return new ActiveFeedIdentity(
            new HashSet<string>(routes, StringComparer.Ordinal),
            new HashSet<string>(stops, StringComparer.Ordinal));
    }
}
