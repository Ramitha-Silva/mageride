using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Transit.Gtfs;

/// <summary>US-28.1's upload: store the zip, dedupe it, and queue it for validation.</summary>
public interface IGtfsUploadService
{
    Task<FeedVersionRow> UploadAsync(
        string fileName, Stream content, Guid actorId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IGtfsUploadService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The file is stored before it is judged.</b> The sha256 that BR-32.1 dedupes on is a property
/// of the bytes as written, not of what the client said it was sending, so there is nothing to
/// compare until the upload is on disk. A duplicate's copy is then deleted — the original is
/// already retained under BR-32.3, and keeping a second one would make "retained ≥ 12 months" mean
/// two files for one feed.
/// </para>
/// <para>
/// <b>The unique index is what decides, not the lookup.</b> Two operators uploading the same file
/// at the same moment both find nothing and both insert; <c>transit.gtfs_feed_versions.sha256</c>
/// is <c>UNIQUE</c>, so one of them loses there and is answered with the version the other
/// created. Checking first is the fast path, not the guarantee.
/// </para>
/// </remarks>
internal sealed class GtfsUploadService(
    INpgsqlConnectionFactory connections,
    IGtfsFeedVersionRepository repository,
    IGtfsObjectStore objects,
    IGtfsAuditRepository audit,
    GtfsValidationSignal signal,
    IOptions<TransitOptions> options,
    TimeProvider clock,
    ILogger<GtfsUploadService> logger) : IGtfsUploadService
{
    /// <summary>Postgres' <c>unique_violation</c>.</summary>
    private const string UniqueViolation = "23505";

    private readonly TransitOptions.GtfsOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Gtfs;

    public async Task<FeedVersionRow> UploadAsync(
        string fileName, Stream content, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var feedVersionId = Guid.CreateVersion7();
        var stored = await objects.PutAsync(feedVersionId, content, _options.MaxUploadBytes, cancellationToken);

        var row = new FeedVersionRow(
            feedVersionId,
            SafeFileName(fileName),
            stored.Bytes,
            stored.Sha256,
            FeedInfoVersion: null,
            ServiceStart: null,
            ServiceEnd: null,
            CountsJson: "{}",
            FeedStatuses.Uploaded,
            ValidationReportJson: null,
            stored.StorageKey,
            actorId,
            clock.GetUtcNow(),
            ActivatedAt: null,
            ArchivedAt: null);

        if (await repository.FindBySha256Async(stored.Sha256, cancellationToken) is { } existing)
        {
            await RejectDuplicateAsync(stored.StorageKey, existing);
        }

        try
        {
            await InsertAsync(row, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            var winner = await repository.FindBySha256Async(stored.Sha256, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            await RejectDuplicateAsync(stored.StorageKey, winner);
        }

        logger.LogInformation(
            "GTFS feed {FeedVersionId} uploaded by {ActorId}: {FileName}, {Bytes} bytes, sha256 {Sha256}. Queued for validation.",
            feedVersionId, actorId, row.FileName, row.FileSizeBytes, row.Sha256);

        signal.Raise();

        return row;
    }

    private async Task InsertAsync(FeedVersionRow row, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await repository.InsertAsync(connection, transaction, row, cancellationToken);

        await audit.WriteAsync(
            connection,
            transaction,
            row.UploadedBy,
            GtfsAuditRepository.FeedUploaded,
            row.FeedVersionId,
            // Nothing changed state — a version came into existence — so there is no `before`,
            // which is the shape `AuditEvent.Observed` uses for the same reason.
            before: null,
            after: new
            {
                fileName = row.FileName,
                fileSizeBytes = row.FileSizeBytes,
                sha256 = row.Sha256,
                status = row.Status,
            },
            row.UploadedAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// BR-32.1's refusal, carrying the version an operator should go and look at instead.
    /// </summary>
    /// <remarks>
    /// The extension is what SCR-AP-016's inline error needs to say "This exact file is already
    /// uploaded (version N)" and link to it; a bare 409 leaves the operator with a message and
    /// nowhere to go.
    /// </remarks>
    private async Task RejectDuplicateAsync(string storageKey, FeedVersionRow existing)
    {
        await objects.DeleteAsync(storageKey, CancellationToken.None);

        throw new MageRideException(
                MageRideErrors.FeedDuplicate,
                $"This exact file was already uploaded on {existing.UploadedAt:yyyy-MM-dd} as '{existing.FileName}'.")
            .WithExtension("feedVersionId", existing.FeedVersionId)
            .WithExtension("feedInfoVersion", existing.FeedInfoVersion)
            .WithExtension("status", existing.Status);
    }

    /// <summary>
    /// The client's filename, kept for display and stripped of everything else.
    /// </summary>
    /// <remarks>
    /// It is shown in SCR-AP-016's history table and nowhere else — the object key is derived from
    /// the feed version id — so a path, a very long name or a control character is a rendering
    /// problem rather than a storage one, and is removed here rather than at every reader.
    /// </remarks>
    private static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return "feed.zip";
        }

        return name.Length > 255 ? name[..255] : name;
    }
}
