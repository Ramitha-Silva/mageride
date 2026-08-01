using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>docs.uploads</c> — the pointer table D-36 puts in front of object storage (migration 1301).
/// </summary>
/// <remarks>
/// <para>
/// This service writes rows for the three AL-49 payout kinds and reads none it did not write. The
/// same table holds driving licences, insurance certificates and support screenshots; a read here
/// that was not filtered by <c>kind</c> would be a read of somebody's NIC.
/// </para>
/// <para>
/// <b>`docs.uploads.kind` is deliberately un-CHECKed</b> (1301: "the set grows with every
/// onboarding surface"), so <see cref="PayoutDocumentKinds"/> is what keeps the column from
/// becoming free text on this surface.
/// </para>
/// </remarks>
public interface IPayoutDocumentRepository
{
    /// <param name="retention">
    /// NFR-28's window, applied as <c>now() + interval</c> rather than as an instant computed in
    /// this process: the deadline is measured from the <c>created_at</c> the database stamps, and a
    /// retention promise nobody can check against the row it is on is not a promise.
    /// </param>
    Task<PayoutDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan? retention,
        CancellationToken cancellationToken);

    /// <summary>The payout documents attached to one profile version, for the officer's queue.</summary>
    Task<IReadOnlyList<PayoutDocument>> ListForProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutDocumentRepository"/>
internal sealed class PayoutDocumentRepository : IPayoutDocumentRepository
{
    public async Task<PayoutDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan? retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);

        // `captured_via` is left NULL. AL-43's provenance distinguishes the in-app drag-crop
        // scanner from a gallery pick because a gallery pick is a fraud signal on an onboarding
        // *photograph*; a bank statement is a PDF-shaped thing a person exports from a banking app
        // on a desktop browser, and recording 'gallery' for all of them would put a fraud signal on
        // every payout profile on the platform.
        return await connection.QuerySingleAsync<PayoutDocument>(new CommandDefinition(
            """
            INSERT INTO docs.uploads (owner_id, storage_url, sha256, kind, auto_delete_at)
            VALUES (@OwnerId, @StorageUrl, @Sha256, @Kind,
                    -- NULL retention means "never expire": a fleet owner's LankaQR is on the Mode B
                    -- pay sheet and is not raw evidence (AL-49). NFR-28 covers the statement only.
                    CASE WHEN @Retention::interval IS NULL THEN NULL ELSE now() + @Retention END)
            RETURNING id, owner_id, storage_url, kind, auto_delete_at, created_at;
            """,
            new { OwnerId = ownerId, StorageUrl = storageUrl, Sha256 = sha256, Kind = kind, Retention = retention },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PayoutDocument>> ListForProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Reached only from the profile, and filtered by kind on top of that: the join alone would
        // return whatever id happened to be in those two columns, and this table holds documents
        // no fleet portal should ever render.
        var rows = await connection.QueryAsync<PayoutDocument>(new CommandDefinition(
            """
            SELECT u.id, u.owner_id, u.storage_url, u.kind, u.auto_delete_at, u.created_at
              FROM registry.fleet_payout_profiles p
              JOIN docs.uploads u ON u.id IN (p.proof_upload_id, p.lankaqr_upload_id)
             WHERE p.id = @ProfileId
               AND u.kind IN ('bank_statement','passbook_first_page','lankaqr_code')
             ORDER BY u.created_at, u.id;
            """,
            new { ProfileId = profileId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
