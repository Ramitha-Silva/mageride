using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// AL-50's four named document slots: <c>docs.uploads</c>, <c>registry.documents</c> and
/// <c>registry.document_fields</c> for one of the org's vehicles (SCR-FP-004).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are registry-svc's tables and this service is their second writer.</b> That is not an
/// accident of convenience: AL-27 makes Mode A/B vehicles and their permits the Fleet Portal's
/// ("Mode A/B vehicles + permits are onboarded in the Fleet Portal, not here" — D3', restated in
/// registry-svc's own CLAUDE.md), and registry-svc's onboarding route refuses a Mode A/B vehicle
/// outright. The two writers do not overlap: registry-svc owns the Mode C wizard's documents, keyed
/// by <c>driver_id</c>; this service owns a fleet's, keyed by <c>fleet_id</c>. <b>The database
/// keeps them apart</b> — <c>ck_documents_owner</c> is an XOR, so a row cannot carry both.
/// </para>
/// <para>
/// <b>Reads go through the RLS-scoped relations, writes through the base tables.</b>
/// <c>registry.documents</c> carries <c>fleet_id</c> and gets a policy (migration 1807);
/// <c>registry.document_fields</c> carries no org at all and is reached only through
/// <c>registry.document_fields_fleet</c>, which inherits the scope through its join.
/// </para>
/// </remarks>
public interface IVehicleDocumentRepository
{
    /// <summary>Every non-rejected document the org holds for this vehicle, newest first.</summary>
    Task<IReadOnlyList<VehicleDocument>> ListForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>Every extracted field of the documents named, for the SCR-FP-004 chips.</summary>
    Task<IReadOnlyList<VehicleDocumentField>> ListFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the bytes in <c>docs.uploads</c> (D-36's pointer table).
    /// </summary>
    /// <param name="ownerId">
    /// The person who uploaded it, because <c>docs.uploads.owner_id</c> references
    /// <c>iam.users</c> and an organisation is not a user. Who the document is <em>about</em> is
    /// <c>registry.documents.fleet_id</c>, which is the row the officer's queue and the approval
    /// gate read.
    /// </param>
    Task<Guid> CreateUploadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid uploadId,
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan? retention,
        CancellationToken cancellationToken);

    /// <summary>The <c>docs.uploads</c> row's storage URL, so ocr-svc can be pointed at the bytes.</summary>
    Task<string?> FindUploadUrlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid uploadId,
        CancellationToken cancellationToken);

    /// <summary>Writes the <c>registry.documents</c> row for one slot.</summary>
    Task<VehicleDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string kind,
        string fileUrl,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Writes what ocr-svc read, verdicts included (AL-29).</summary>
    Task AddFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        IReadOnlyCollection<VehicleDocumentField> fields,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleDocumentRepository"/>
internal sealed class VehicleDocumentRepository : IVehicleDocumentRepository
{
    private const string DocumentColumns =
        "id, fleet_id, vehicle_id, kind, file_url, expires_at, status, created_at";

    public async Task<IReadOnlyList<VehicleDocument>> ListForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Every document of every kind, not the current batch per kind: the slot rule
        // (VehicleDocumentSlots) picks the current one, and it has to see the superseded ones to
        // know which is newest. Bounded by the four slots × however many times an operator
        // re-uploaded — a handful of rows per vehicle, so there is no page here.
        var documents = await connection.QueryAsync<VehicleDocument>(new CommandDefinition(
            $"""
             SELECT {DocumentColumns} FROM registry.documents
              WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId
              ORDER BY created_at DESC, id DESC;
             """,
            new { FleetId = fleetId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. documents];
    }

    public async Task<IReadOnlyList<VehicleDocumentField>> ListFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(documentIds);

        if (documentIds.Count == 0)
        {
            return [];
        }

        var fields = await connection.QueryAsync<VehicleDocumentField>(new CommandDefinition(
            """
            SELECT id, document_id, field_key, field_value, confidence, source, verify_status
              FROM registry.document_fields_fleet
             WHERE document_id = ANY(@DocumentIds)
             ORDER BY document_id, field_key;
            """,
            new { DocumentIds = documentIds.ToArray() },
            transaction,
            cancellationToken: cancellationToken));

        return [.. fields];
    }

    public async Task<Guid> CreateUploadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid uploadId,
        Guid ownerId,
        string storageUrl,
        byte[] sha256,
        string kind,
        TimeSpan? retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sha256);

        // The id is supplied rather than generated, because the bytes were already written under
        // it — same ordering as the payout document, and for the same reason: an orphan file is
        // swept by NFR-28's deadline, a row pointing at nothing is a document an officer is told
        // exists and cannot open.
        //
        // `captured_via = 'other'`. AL-43's provenance separates the in-app drag-crop scanner from
        // a phone gallery because a gallery pick is a fraud signal on an onboarding photograph. The
        // Fleet Portal is a browser file picker on a desktop and is neither; recording 'gallery'
        // would put that signal on every fleet document on the platform, and 'camera_dragcrop'
        // would claim a scanner ran. 'other' says what actually happened.
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO docs.uploads (id, owner_id, storage_url, sha256, kind, captured_via, auto_delete_at)
            VALUES (@UploadId, @OwnerId, @StorageUrl, @Sha256, @Kind, 'other', -- NULL retention means "never expire". A vehicle document is always raw evidence so this
                    -- is always set here; the nullable type keeps one shape across both upload paths.
                    CASE WHEN @Retention::interval IS NULL THEN NULL ELSE now() + @Retention END)
            RETURNING id;
            """,
            new
            {
                UploadId = uploadId,
                OwnerId = ownerId,
                StorageUrl = storageUrl,
                Sha256 = sha256,
                Kind = kind,
                Retention = retention,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<string?> FindUploadUrlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT storage_url FROM docs.uploads WHERE id = @UploadId;",
            new { UploadId = uploadId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<VehicleDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string kind,
        string fileUrl,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `driver_id` is not in the column list at all, rather than passed as NULL: the XOR is what
        // separates this service's rows from registry-svc's, and a column that is never named
        // cannot be filled in by a later edit that thought it was being helpful.
        //
        // `status` is left at its VALID default. E-03's sweep moves it to EXPIRING and EXPIRED from
        // `expires_at`, and a document uploaded already lapsed is caught by the slot rule reading
        // its expiry rather than by this service pre-judging the sweep.
        return await connection.QuerySingleAsync<VehicleDocument>(new CommandDefinition(
            $"""
             INSERT INTO registry.documents (fleet_id, vehicle_id, kind, file_url, expires_at)
             VALUES (@FleetId, @VehicleId, @Kind, @FileUrl, @ExpiresAt)
             RETURNING {DocumentColumns};
             """,
            new
            {
                FleetId = fleetId,
                VehicleId = vehicleId,
                Kind = kind,
                FileUrl = fileUrl,
                ExpiresAt = expiresAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task AddFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        IReadOnlyCollection<VehicleDocumentField> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO registry.document_fields
              (document_id, field_key, field_value, confidence, source, verify_status)
            VALUES (@DocumentId, @FieldKey, @FieldValue, @Confidence, @Source, @VerifyStatus);
            """,
            fields.Select(field => new
            {
                DocumentId = documentId,
                field.FieldKey,
                field.FieldValue,
                field.Confidence,
                field.Source,
                field.VerifyStatus,
            }).ToArray(),
            transaction,
            cancellationToken: cancellationToken));
    }
}
