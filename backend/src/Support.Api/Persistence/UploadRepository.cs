using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Support.Persistence;

/// <summary>A <c>docs.uploads</c> row (D-36, migration 1301).</summary>
public sealed record SupportUpload(
    Guid Id, Guid OwnerId, string StorageUrl, string? Kind, DateTimeOffset? AutoDeleteAt);

/// <summary>
/// <c>docs.uploads</c> — the pointer table D-36 puts in front of object storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Postgres holds the pointer; the bytes are somewhere else.</b> That split is the whole point of
/// the table, and it is why the ticket links an id rather than a URL: the storage location can move
/// (a directory today, an SSE-KMS bucket when C125 lands) without rewriting a single ticket row, and
/// no URL that would outlive its access control is ever put in front of a user.
/// </para>
/// <para>
/// <b>support-svc is the first writer of this table.</b> registry-svc resolves ids it did not create
/// and says so; provisioning-svc and ride-svc keep their artefacts elsewhere. US-16.2's "button to
/// attach a screenshot" needed an upload surface and there is none anywhere on the platform, so this
/// service writes the row for the one <c>kind</c> it owns. It reads none it did not write.
/// </para>
/// </remarks>
public interface IUploadRepository
{
    /// <summary>Records a stored screenshot and returns its id.</summary>
    /// <param name="retention">
    /// NFR-28's window, applied as <c>now() + interval</c> rather than as an instant computed here:
    /// every other timestamp on the row comes from the database, and a deletion deadline that
    /// disagreed with the <c>created_at</c> it is measured from would be a retention promise nobody
    /// could check.
    /// </param>
    Task<SupportUpload> CreateAsync(
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan retention,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an upload the client named. <see langword="null"/> when there is no such row, so the
    /// caller can answer <c>validation-failed</c> rather than write a ticket pointing nowhere.
    /// </summary>
    Task<SupportUpload?> FindAsync(Guid uploadId, CancellationToken cancellationToken);

    /// <summary>Whether any ticket already links this upload.</summary>
    /// <remarks>
    /// One screenshot belongs to one complaint. Without this an id could be attached to a second
    /// ticket — including, if it ever leaked, somebody else's — and the two would then share an
    /// artefact whose deletion deadline belongs to neither.
    /// </remarks>
    Task<bool> IsAttachedAsync(Guid uploadId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IUploadRepository"/>
internal sealed class UploadRepository(INpgsqlConnectionFactory connections) : IUploadRepository
{
    public async Task<SupportUpload> CreateAsync(
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);
        ArgumentNullException.ThrowIfNull(sha256);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // `captured_via` is left NULL. AL-43's provenance is about onboarding documents, where a
        // gallery pick is the fraud signal the verification queue sorts on; a support screenshot is
        // by definition a picture of a screen and has no camera path to distinguish. Recording
        // 'gallery' for all of them would put a fraud signal on every ticket on the platform.
        return await connection.QuerySingleAsync<SupportUpload>(new CommandDefinition(
            """
            INSERT INTO docs.uploads (owner_id, storage_url, sha256, kind, auto_delete_at)
            VALUES (@OwnerId, @StorageUrl, @Sha256, @Kind, now() + @Retention)
            RETURNING id, owner_id, storage_url, kind, auto_delete_at;
            """,
            new
            {
                OwnerId = ownerId,
                StorageUrl = storageUrl,
                Sha256 = sha256,
                Kind = kind,
                Retention = retention,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<SupportUpload?> FindAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<SupportUpload>(new CommandDefinition(
            "SELECT id, owner_id, storage_url, kind, auto_delete_at FROM docs.uploads WHERE id = @UploadId;",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> IsAttachedAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM support.tickets WHERE screenshot_upload_id = @UploadId);",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }
}
