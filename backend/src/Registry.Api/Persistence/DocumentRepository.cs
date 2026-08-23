using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>An upload waiting in <c>docs.uploads</c>, resolved before a document is written.</summary>
/// <param name="OwnerId">Who uploaded it. A driver may only attach their own uploads.</param>
public sealed record PendingUpload(Guid Id, Guid OwnerId, string StorageUrl, string? Kind);

/// <summary>A document that has passed one of E-03's notice thresholds and not been told about.</summary>
/// <param name="ThresholdDays">30, 7 or 1 for a reminder; 0 for expiry itself.</param>
/// <param name="VehicleIds">
/// Every vehicle the document covers. One for a per-vehicle document; for a vehicle-less driver
/// document it is the driver's whole fleet, because E-03 says expiry "flips <em>driver</em> to
/// DISPATCH_SUSPENDED" and the column lives on the vehicle.
/// </param>
public sealed record DueDocumentNotice(
    Guid DocumentId,
    Guid? DriverId,
    Guid? VehicleId,
    string Kind,
    DateTimeOffset ExpiresAt,
    int ThresholdDays,
    Guid[] VehicleIds)
{
    /// <summary>Whether this notice is expiry itself rather than one of the three reminders.</summary>
    public bool IsExpired => ThresholdDays == 0;
}

/// <summary>
/// <c>registry.documents</c>, <c>registry.document_fields</c> and <c>registry.document_notices</c>
/// (migrations 0305 and 0312).
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Resolves an upload id the client supplied. Returns <see langword="null"/> when no such
    /// upload exists, so the caller can answer <c>validation-failed</c> rather than write a
    /// document whose <c>file_url</c> points nowhere.
    /// </summary>
    Task<PendingUpload?> FindUploadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid uploadId, CancellationToken cancellationToken);

    Task<VehicleDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid? driverId,
        Guid? vehicleId,
        string kind,
        string fileUrl,
        DateTimeOffset? expiresAt,
        string status,
        CancellationToken cancellationToken);

    Task AddFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        IReadOnlyCollection<DocumentField> fields,
        CancellationToken cancellationToken);

    /// <summary>Every field of every document listed, oldest document first.</summary>
    Task<IReadOnlyList<DocumentField>> ListFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The <b>current</b> documents for a vehicle: the most recently saved batch of each kind,
    /// ignoring REJECTED ones. A renewal supersedes rather than replaces, so the expired
    /// predecessor must not keep the vehicle suspended (migration 0312).
    /// </summary>
    /// <remarks>
    /// The newest <em>batch</em>, not the newest row — a step can save two documents of one kind
    /// (the licence's two sides, the vehicle's two photos) and they are equally current. Both are
    /// inserted in one transaction, so <c>DEFAULT now()</c> — which is the transaction timestamp —
    /// gives them the same <c>created_at</c> and makes the batch exactly expressible.
    /// </remarks>
    Task<IReadOnlyList<VehicleDocument>> ListCurrentForVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The same read for the driver's vehicle-less identity documents (AL-27).</summary>
    Task<IReadOnlyList<VehicleDocument>> ListCurrentForDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Every current document this driver is entitled to look at (Δ MCS-28) — their own identity
    /// documents, and the documents of every vehicle they own or are assigned to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One query, and the entitlement is a join rather than a filter.</b> The alternative is to
    /// list the driver's vehicles and then read documents per vehicle, which is the same answer
    /// with a race in it: a vehicle deactivated between the two reads still yields its documents.
    /// </para>
    /// <para>
    /// <c>registry.driver_eligible_vehicles</c> is what "entitled to" means here, because it is
    /// what it means everywhere else — <c>select-live</c>, dispatch's standby gate and
    /// trip-state-svc all read it, and a fourth definition of the same question is how three of
    /// them end up disagreeing.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<VehicleDocument>> ListVisibleToDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// One document, but only if <paramref name="driverId"/> may see it (Δ MCS-28).
    /// </summary>
    /// <remarks>
    /// The ownership predicate is in the SQL rather than checked afterwards, so "not yours" and
    /// "does not exist" are the same query result. Telling them apart would let somebody holding a
    /// document id learn whose vehicle it names.
    /// </remarks>
    Task<VehicleDocument?> FindVisibleToDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>The documents a step uploaded, newest save first.</summary>
    Task<IReadOnlyList<VehicleDocument>> ListByVehicleAndKindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string kind,
        CancellationToken cancellationToken);

    Task<int> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        string status,
        CancellationToken cancellationToken);

    /// <summary>
    /// The E-03 sweep's work list: current documents whose expiry has crossed a threshold nobody
    /// has been told about (ADD §1 E-03).
    /// </summary>
    Task<IReadOnlyList<DueDocumentNotice>> ClaimDueNoticesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="thresholdDays"/> was emitted, plus every looser threshold the
    /// same sweep skipped past. Returns <see langword="false"/> when another replica got there
    /// first, which is what stops a driver being pushed the same reminder twice.
    /// </summary>
    Task<bool> RecordNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        int thresholdDays,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDocumentRepository"/>
public sealed class DocumentRepository : IDocumentRepository
{
    private const string Columns =
        "id, driver_id, fleet_id, vehicle_id, kind, file_url, issued_at, expires_at, status, created_at";

    private const string FieldColumns =
        "id, document_id, field_key, field_value, confidence, source, verify_status, confirmed_by, confirmed_at";

    /// <summary>E-03's three reminders plus expiry itself, tightest first.</summary>
    private static readonly int[] Thresholds = [0, 1, 7, 30];

    public Task<PendingUpload?> FindUploadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid uploadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PendingUpload>(new CommandDefinition(
            "SELECT id, owner_id, storage_url, kind FROM docs.uploads WHERE id = @UploadId;",
            new { UploadId = uploadId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<VehicleDocument> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid? driverId,
        Guid? vehicleId,
        string kind,
        string fileUrl,
        DateTimeOffset? expiresAt,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<VehicleDocument>(new CommandDefinition(
            $"""
             INSERT INTO registry.documents (driver_id, vehicle_id, kind, file_url, expires_at, status)
             VALUES (@DriverId, @VehicleId, @Kind, @FileUrl, @ExpiresAt, @Status)
             RETURNING {Columns};
             """,
            new { DriverId = driverId, VehicleId = vehicleId, Kind = kind, FileUrl = fileUrl, ExpiresAt = expiresAt, Status = status },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task AddFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        IReadOnlyCollection<DocumentField> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count == 0)
        {
            return;
        }

        // Dapper's list expansion sends one INSERT per element on the same command; the rows are
        // small and there are at most four per document.
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

    public async Task<IReadOnlyList<DocumentField>> ListFieldsAsync(
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

        var fields = await connection.QueryAsync<DocumentField>(new CommandDefinition(
            $"""
             SELECT {FieldColumns}
               FROM registry.document_fields
              WHERE document_id = ANY(@DocumentIds)
              ORDER BY created_at, id;
             """,
            new { DocumentIds = documentIds.ToArray() },
            transaction,
            cancellationToken: cancellationToken));

        return [.. fields];
    }

    public async Task<IReadOnlyList<VehicleDocument>> ListCurrentForVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var documents = await connection.QueryAsync<VehicleDocument>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM (
               SELECT {Columns}, max(created_at) OVER (PARTITION BY kind) AS newest
                 FROM registry.documents
                WHERE vehicle_id = @VehicleId AND status <> '{DocumentStatuses.Rejected}'
             ) current WHERE created_at = newest ORDER BY kind, id;
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. documents];
    }

    public async Task<IReadOnlyList<VehicleDocument>> ListCurrentForDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var documents = await connection.QueryAsync<VehicleDocument>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM (
               SELECT {Columns}, max(created_at) OVER (PARTITION BY kind) AS newest
                 FROM registry.documents
                WHERE driver_id = @DriverId AND vehicle_id IS NULL AND status <> '{DocumentStatuses.Rejected}'
             ) current WHERE created_at = newest ORDER BY kind, id;
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. documents];
    }

    /// <summary>The entitlement both MCS-28 reads share, as a WHERE clause.</summary>
    /// <remarks>
    /// A driver sees their own vehicle-less identity documents, and the documents of any vehicle
    /// <c>driver_eligible_vehicles</c> says they may operate. Rejected documents are excluded from
    /// the list for the same reason the other two reads exclude them: a superseded upload is
    /// audit-trail, not something to show a driver as their insurance.
    /// </remarks>
    private const string VisibleToDriver =
        """
        (
          (driver_id = @DriverId AND vehicle_id IS NULL)
          OR vehicle_id IN (SELECT vehicle_id FROM registry.driver_eligible_vehicles WHERE driver_id = @DriverId)
        )
        """;

    public async Task<IReadOnlyList<VehicleDocument>> ListVisibleToDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The same newest-batch-per-kind rule the other two reads use, partitioned by vehicle as
        // well as kind so one vehicle's renewed insurance does not hide another's.
        var documents = await connection.QueryAsync<VehicleDocument>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM (
               SELECT {Columns}, max(created_at) OVER (PARTITION BY vehicle_id, kind) AS newest
                 FROM registry.documents
                WHERE {VisibleToDriver} AND status <> '{DocumentStatuses.Rejected}'
             ) current WHERE created_at = newest ORDER BY vehicle_id NULLS FIRST, kind, id;
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. documents];
    }

    public Task<VehicleDocument?> FindVisibleToDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<VehicleDocument>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.documents WHERE id = @DocumentId AND {VisibleToDriver};",
            new { DriverId = driverId, DocumentId = documentId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<VehicleDocument>> ListByVehicleAndKindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var documents = await connection.QueryAsync<VehicleDocument>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM registry.documents
              WHERE vehicle_id = @VehicleId AND kind = @Kind
              ORDER BY created_at DESC, id DESC;
             """,
            new { VehicleId = vehicleId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));

        return [.. documents];
    }

    public Task<int> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE registry.documents SET status = @Status WHERE id = @DocumentId AND status <> @Status;",
            new { DocumentId = documentId, Status = status },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DueDocumentNotice>> ClaimDueNoticesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Only CURRENT documents. A superseded certificate expiring is not news — the vehicle's
        // renewal is already on file — and suspending on it would punish the driver for renewing
        // early (migration 0312).
        //
        // `threshold_days` is the tightest crossed threshold with no notice row: 0 once the
        // document has actually expired, else the smallest of 1/7/30 that is inside the window.
        // Emitting only that one is what stops a job that was down for a fortnight sending three
        // pushes about the same certificate.
        var rows = await connection.QueryAsync<DueNoticeRow>(
            new CommandDefinition(
                $"""
                 WITH current_documents AS (
                   -- The newest saved batch per (owner, kind); see ListCurrentForVehicleAsync.
                   -- The window covers every non-rejected document, not only the ones carrying an
                   -- expiry, so a renewal whose expiry could not be read still supersedes its
                   -- predecessor instead of leaving the lapsed one looking current.
                   SELECT id, driver_id, vehicle_id, kind, expires_at FROM (
                     SELECT d.id, d.driver_id, d.vehicle_id, d.kind, d.expires_at, d.created_at,
                            max(d.created_at) OVER (
                              PARTITION BY COALESCE(d.vehicle_id, d.driver_id), d.kind) AS newest
                       FROM registry.documents d
                      WHERE d.status <> '{DocumentStatuses.Rejected}'
                   ) c
                    WHERE c.created_at = c.newest AND c.expires_at IS NOT NULL
                 ),
                 due AS (
                   SELECT c.*,
                          (SELECT min(t.threshold)
                             FROM (VALUES (0), (1), (7), (30)) AS t(threshold)
                            WHERE c.expires_at <= @Now + make_interval(days => t.threshold)
                              AND NOT EXISTS (SELECT 1 FROM registry.document_notices n
                                               WHERE n.document_id = c.id AND n.threshold_days = t.threshold))
                            AS threshold_days
                     FROM current_documents c
                 )
                 SELECT due.id            AS document_id,
                        due.driver_id,
                        due.vehicle_id,
                        due.kind,
                        due.expires_at,
                        due.threshold_days,
                        -- The vehicles the notice covers. A per-vehicle document names one; a
                        -- vehicle-less driving licence names every vehicle the driver owns,
                        -- because E-03 suspends "the driver" and dispatch_state is per vehicle.
                        --
                        -- As JSON, not as uuid[]: Dapper materialises a record through its
                        -- constructor and an array column presents to it as System.Array, which
                        -- matches no Guid[] parameter — the record then fails to materialise with
                        -- a message about the constructor rather than about the column.
                        array_to_json(COALESCE(
                          CASE WHEN due.vehicle_id IS NOT NULL THEN ARRAY[due.vehicle_id]
                               ELSE ARRAY(SELECT v.id FROM registry.vehicles v
                                           WHERE v.owner_id = due.driver_id
                                             AND v.status <> '{RegistrationStatuses.Deactivated}')
                          END, ARRAY[]::uuid[]))::text AS vehicle_ids_json
                   FROM due
                  WHERE due.threshold_days IS NOT NULL
                  ORDER BY due.expires_at
                  LIMIT @BatchSize;
                 """,
                new { Now = now, BatchSize = batchSize },
                transaction,
                cancellationToken: cancellationToken));

        return [.. rows.Select(row => row.ToNotice())];
    }

    public async Task<bool> RecordNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid documentId,
        int thresholdDays,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Every looser threshold is recorded with the one being emitted: they were crossed too,
        // and a reminder about "30 days left" sent to somebody who has one day left is worse than
        // no reminder. The RETURNING count tells the caller whether this replica is the one that
        // won the race and should therefore be the one to publish.
        var inserted = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            WITH inserted AS (
              INSERT INTO registry.document_notices (document_id, threshold_days)
              SELECT @DocumentId, t.threshold
                FROM unnest(@Thresholds::smallint[]) AS t(threshold)
              ON CONFLICT (document_id, threshold_days) DO NOTHING
              RETURNING threshold_days)
            SELECT count(*)::int FROM inserted WHERE threshold_days = @ThresholdDays;
            """,
            new
            {
                DocumentId = documentId,
                ThresholdDays = (short)thresholdDays,
                Thresholds = Thresholds.Where(threshold => threshold >= thresholdDays).Select(threshold => (short)threshold).ToArray(),
            },
            transaction,
            cancellationToken: cancellationToken));

        return inserted == 1;
    }

    /// <summary>The due row as read, before <c>vehicle_ids</c> is turned back into an array.</summary>
    private sealed record DueNoticeRow(
        Guid DocumentId,
        Guid? DriverId,
        Guid? VehicleId,
        string Kind,
        DateTimeOffset ExpiresAt,
        int ThresholdDays,
        string? VehicleIdsJson)
    {
        public DueDocumentNotice ToNotice() =>
            new(DocumentId,
                DriverId,
                VehicleId,
                Kind,
                ExpiresAt,
                ThresholdDays,
                VehicleIdsJson is null
                    ? []
                    : System.Text.Json.JsonSerializer.Deserialize<Guid[]>(VehicleIdsJson) ?? []);
    }
}
