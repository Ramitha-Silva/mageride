using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.AdminBff.Persistence;

/// <summary>One page of a queue, positioned by its last row (submitted-at, subject id).</summary>
public sealed record VerificationQueueQuery(
    string? Search, string? Status, DateTimeOffset? CursorAt, Guid? CursorId, int Limit);

/// <summary>
/// The registry tables the Verification Officer reads, and the two columns their decision writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership in a queue is "has a pending field", and nothing else.</b> AL-27's fence — "the
/// officer sees only PENDING items; auto-verified documents never enter the queue" — is a property
/// of <c>registry.document_fields.verify_status</c>, which ocr-svc and registry-svc already set for
/// exactly this purpose (AL-29's low-confidence, driver-typed and plate-mismatch rules, one clause
/// each). Deriving the queue from a status column instead would be a second opinion about the same
/// rows, and the partial index <c>ix_document_fields_pending</c> exists to be this query's index.
/// </para>
/// <para>
/// <b>The <c>status</c> a row carries is the subject's, not the queue's.</b> Every member is by
/// construction awaiting review, so a "status" that repeated that would say nothing; what the
/// officer needs to see is whether this applicant was already approved (a renewal whose scan came
/// back doubtful) or already rejected (a resubmission), which is what SCR-AP-003's status filter
/// filters on.
/// </para>
/// <para>
/// <b>Two columns are written here and both were left for this component by their owners.</b>
/// registry-svc's own file says the rejection path — <c>registry.vehicles.rejection_reason</c>,
/// US-2.15 — "is C062's too; nothing here writes the column", and no service anywhere exposes a
/// route that decides a driver's identity submission, so <c>registry.driver_profiles.verified_at</c>
/// and its new <c>rejection_reason</c> (migration 0315) are written here as well. Everything else a
/// decision implies is forwarded: the Mode C recompute to registry-svc, the AL-50 gate and the org
/// to fleet-svc.
/// </para>
/// </remarks>
public interface IVerificationRepository
{
    /// <summary>Drivers whose licence submission has a flagged field (SCR-AP-003 tab 1).</summary>
    Task<IReadOnlyList<DriverQueueRow>> DriverQueueAsync(
        VerificationQueueQuery query, CancellationToken cancellationToken);

    /// <summary>Vehicles whose documents have a flagged field — Mode C and fleet alike (tab 2).</summary>
    Task<IReadOnlyList<VehicleQueueRow>> VehicleQueueAsync(
        VerificationQueueQuery query, CancellationToken cancellationToken);

    /// <summary>Which of the three kinds of applicant this id names, or null for none of them.</summary>
    Task<VerificationSubject?> FindSubjectAsync(Guid subjectId, CancellationToken cancellationToken);

    /// <summary>Every extracted or typed field attached to the subject, newest document first.</summary>
    Task<IReadOnlyList<VerificationField>> FieldsAsync(
        VerificationSubject subject, CancellationToken cancellationToken);

    /// <summary>Every document attached to the subject, newest first.</summary>
    Task<IReadOnlyList<VerificationDocumentRow>> DocumentsAsync(
        VerificationSubject subject, CancellationToken cancellationToken);

    /// <summary>AL-30's four saved steps. Empty for a fleet vehicle, which never had a wizard.</summary>
    Task<IReadOnlyList<VerificationStepRow>> StepsAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>How many of the subject's fields are still <c>pending</c>. Zero is the approval gate.</summary>
    Task<int> PendingFieldCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        VerificationSubject subject,
        CancellationToken cancellationToken);

    /// <summary>The subject's rows for one field key, whatever their status.</summary>
    Task<IReadOnlyList<VerificationField>> FieldsByKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        VerificationSubject subject,
        string fieldKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms every row of <paramref name="fieldKey"/> the subject holds, optionally replacing
    /// the value. Returns how many rows moved.
    /// </summary>
    Task<int> ConfirmFieldAsync(
        IUnitOfWork unitOfWork,
        VerificationSubject subject,
        string fieldKey,
        string? value,
        Guid officerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Reads a vehicle's current <c>status</c> / <c>onboarding_status</c> pair.</summary>
    Task<(string Status, string OnboardingStatus, string? RejectionReason)?> VehicleStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>US-2.15: refuses a vehicle and records why. Returns false when the id is unknown.</summary>
    Task<bool> RejectVehicleAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a REJECTED vehicle back to PENDING so registry-svc's AL-30 rule can run over it again.
    /// </summary>
    Task<bool> ClearVehicleRejectionAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The driver's identity verdict, or null when they never submitted a profile.</summary>
    Task<(DateTimeOffset? VerifiedAt, string? RejectionReason)?> DriverStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>Marks the driver's identity checked (US-2.4a). False when there is no profile row.</summary>
    Task<bool> ApproveDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Refuses the driver's identity submission and records why (migration 0315).</summary>
    Task<bool> RejectDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, string reason, CancellationToken cancellationToken);

    /// <summary>The document behind a doc id, from whichever of the two tables holds it.</summary>
    Task<StoredDocument?> FindDocumentAsync(Guid docId, CancellationToken cancellationToken);

    /// <summary>How many vehicles each of these organisations has on its roster.</summary>
    Task<IReadOnlyDictionary<Guid, int>> FleetVehicleCountsAsync(
        IReadOnlyCollection<Guid> fleetIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVerificationRepository"/>
internal sealed class VerificationRepository(INpgsqlConnectionFactory connections) : IVerificationRepository
{
    // ---------------------------------------------------------------------------------------
    // Queues
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The flagged fields are aggregated rather than listed: SCR-AP-003's row shows *which* checks
    /// failed, and a driver with a doubtful NIC on both sides of one licence is one row with one
    /// <c>nic_no</c> chip, not two rows.
    /// </remarks>
    private const string DriverQueueSql =
        """
        WITH flagged AS (
          SELECT d.driver_id, d.created_at, f.field_key
            FROM registry.documents d
            JOIN registry.document_fields f ON f.document_id = d.id
           WHERE d.driver_id  IS NOT NULL
             AND d.vehicle_id IS NULL
             AND d.kind = 'driving_license'
             AND f.verify_status = 'pending'
        ), rows AS (
          SELECT fl.driver_id                                      AS "DriverId",
                 COALESCE(pr.display_name, u.first_name, '')       AS "Name",
                 max(fl.created_at)                                AS "SubmittedAt",
                 array_agg(DISTINCT fl.field_key)                  AS "FlaggedFields",
                 CASE WHEN pr.verified_at      IS NOT NULL THEN 'APPROVED'
                      WHEN pr.rejection_reason IS NOT NULL THEN 'REJECTED'
                      ELSE 'PENDING' END                           AS "Status"
            FROM flagged fl
            JOIN iam.users u                      ON u.id = fl.driver_id
            LEFT JOIN registry.driver_profiles pr ON pr.driver_id = fl.driver_id
           GROUP BY fl.driver_id, pr.display_name, u.first_name, pr.verified_at, pr.rejection_reason
        )
        SELECT "DriverId", "Name", "SubmittedAt", "FlaggedFields", "Status"
          FROM rows
         WHERE (@Status::text IS NULL OR "Status" = @Status)
           AND (@Search::text IS NULL OR "Name" ILIKE @Search OR "DriverId"::text ILIKE @Search)
           AND (@CursorAt::timestamptz IS NULL OR ("SubmittedAt", "DriverId") < (@CursorAt, @CursorId))
         ORDER BY "SubmittedAt" DESC, "DriverId" DESC
         LIMIT @Limit;
        """;

    /// <remarks>
    /// <b>Both kinds of vehicle, one query.</b> A Mode C wizard document and a Fleet Portal document
    /// slot write the same <c>registry.documents</c> / <c>document_fields</c> pair (AL-50 reuses
    /// AL-29's pipeline in as many words), so the officer's queue does not need to know which
    /// surface uploaded them. What differs is who owns the *decision*, and that is read from
    /// <c>mode</c> when the officer acts.
    /// <c>DEACTIVATED</c> is excluded: a retired registration is not an application.
    /// </remarks>
    private const string VehicleQueueSql =
        """
        WITH flagged AS (
          SELECT d.vehicle_id, d.created_at, f.field_key
            FROM registry.documents d
            JOIN registry.document_fields f ON f.document_id = d.id
           WHERE d.vehicle_id IS NOT NULL
             AND f.verify_status = 'pending'
        ), rows AS (
          SELECT v.id                             AS "VehicleId",
                 v.registration_number            AS "RegNo",
                 v.owner_id                       AS "OwnerDriverId",
                 max(fl.created_at)               AS "SubmittedAt",
                 array_agg(DISTINCT fl.field_key) AS "FlaggedFields",
                 v.status                         AS "Status",
                 v.driver_name                    AS "DriverName"
            FROM flagged fl
            JOIN registry.vehicles v ON v.id = fl.vehicle_id
           WHERE v.status <> 'DEACTIVATED'
           GROUP BY v.id
        )
        SELECT "VehicleId", "RegNo", "OwnerDriverId", "SubmittedAt", "FlaggedFields", "Status"
          FROM rows
         WHERE (@Status::text IS NULL OR "Status" = @Status)
           AND (@Search::text IS NULL
                OR "RegNo" ILIKE @Search OR "DriverName" ILIKE @Search OR "VehicleId"::text ILIKE @Search)
           AND (@CursorAt::timestamptz IS NULL OR ("SubmittedAt", "VehicleId") < (@CursorAt, @CursorId))
         ORDER BY "SubmittedAt" DESC, "VehicleId" DESC
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<DriverQueueRow>> DriverQueueAsync(
        VerificationQueueQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DriverQueueDto>(
            new CommandDefinition(DriverQueueSql, Parameters(query), cancellationToken: cancellationToken));

        return [.. rows.Select(row => new DriverQueueRow(
            row.DriverId, row.Name, row.SubmittedAt, row.FlaggedFields ?? [], row.Status))];
    }

    public async Task<IReadOnlyList<VehicleQueueRow>> VehicleQueueAsync(
        VerificationQueueQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VehicleQueueDto>(
            new CommandDefinition(VehicleQueueSql, Parameters(query), cancellationToken: cancellationToken));

        return [.. rows.Select(row => new VehicleQueueRow(
            row.VehicleId, row.RegNo, row.OwnerDriverId, row.SubmittedAt, row.FlaggedFields ?? [], row.Status))];
    }

    private static object Parameters(VerificationQueueQuery query) => new
    {
        // Substring rather than prefix: an officer searching a queue has half a plate or half a
        // name, and the queue is bounded by how many applications are outstanding rather than by
        // the size of the platform.
        Search = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{Escape(query.Search.Trim())}%",
        query.Status,
        query.CursorAt,
        query.CursorId,
        query.Limit,
    };

    /// <summary>Neutralises the two <c>LIKE</c> wildcards so a search for "10%" means "10%".</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------
    // The subject
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// One round trip rather than three sequential probes. The three id spaces do not overlap —
    /// every one is a v4/v7 UUID from a different table — so at most one branch can match, and the
    /// ordering only decides which would win if somebody arranged a collision.
    /// </para>
    /// <para>
    /// The projection is in <see cref="VerificationSubject"/>'s constructor order because Dapper's
    /// <c>DefaultTypeMap</c> matches a record constructor <b>positionally</b>: a column list in a
    /// different order fails to materialise at run time rather than at compile time.
    /// </para>
    /// </remarks>
    private const string FindSubjectSql =
        """
        SELECT v.id                   AS "Id",
               'vehicle'::text        AS "Type",
               v.registration_number  AS "DisplayName",
               v.status               AS "Status",
               v.mode::text           AS "Mode",
               fv.fleet_id            AS "FleetId"
          FROM registry.vehicles v
          LEFT JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
         WHERE v.id = @Id
        UNION ALL
        SELECT f.id, 'org', f.name, f.status, NULL::text, f.id
          FROM registry.fleets f
         WHERE f.id = @Id
        UNION ALL
        SELECT u.id,
               'driver',
               COALESCE(p.display_name, u.first_name, ''),
               CASE WHEN p.verified_at      IS NOT NULL THEN 'APPROVED'
                    WHEN p.rejection_reason IS NOT NULL THEN 'REJECTED'
                    ELSE 'PENDING' END,
               NULL::text,
               NULL::uuid
          FROM iam.users u
          LEFT JOIN registry.driver_profiles p ON p.driver_id = u.id
         WHERE u.id = @Id
           AND (p.driver_id IS NOT NULL
                OR EXISTS (SELECT 1 FROM iam.user_roles r WHERE r.user_id = u.id AND r.role = 'driver'))
        LIMIT 1;
        """;

    public async Task<VerificationSubject?> FindSubjectAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VerificationSubject>(
            new CommandDefinition(FindSubjectSql, new { Id = subjectId }, cancellationToken: cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Fields, documents, steps
    // ---------------------------------------------------------------------------------------

    private const string FieldColumns =
        """
        f.document_id  AS DocumentId,
        f.field_key    AS FieldKey,
        f.field_value  AS FieldValue,
        f.source       AS Source,
        f.confidence   AS Confidence,
        f.verify_status AS VerifyStatus
        """;

    public async Task<IReadOnlyList<VerificationField>> FieldsAsync(
        VerificationSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            // A payout profile is typed by the owner and read by eye — bank, branch, account
            // number, holder name — and nothing extracts fields from a bank statement. There is no
            // `registry.document_fields` row to show, so the org rail is the KYC itself.
            return [];
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VerificationField>(new CommandDefinition(
            $"""
             SELECT {FieldColumns}
               FROM registry.document_fields f
               JOIN registry.documents d ON d.id = f.document_id
              WHERE {SubjectPredicate(subject)}
              ORDER BY d.created_at DESC, f.field_key;
             """,
            new { Id = subject.Id },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<VerificationDocumentRow>> DocumentsAsync(
        VerificationSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        // LATERAL rather than a plain join: `docs.uploads.storage_url` is the only link back to the
        // upload (registry.documents keeps the URL, not the upload id), and a join on it would
        // duplicate the document row if two uploads ever shared a path.
        var rows = await connection.QueryAsync<VerificationDocumentRow>(new CommandDefinition(
            $"""
             SELECT d.id         AS DocId,
                    d.kind       AS Kind,
                    d.file_url   AS StorageUrl,
                    u.captured_via AS CapturedVia,
                    d.created_at AS CreatedAt
               FROM registry.documents d
               LEFT JOIN LATERAL (
                    SELECT up.captured_via
                      FROM docs.uploads up
                     WHERE up.storage_url = d.file_url
                     ORDER BY up.created_at DESC
                     LIMIT 1) u ON true
              WHERE {SubjectPredicate(subject)}
              ORDER BY d.created_at DESC, d.id DESC;
             """,
            new { Id = subject.Id },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<VerificationStepRow>> StepsAsync(
        Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VerificationStepRow>(new CommandDefinition(
            "SELECT step AS Step, status AS Status FROM registry.onboarding_steps WHERE vehicle_id = @Id;",
            new { Id = vehicleId },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<int> PendingFieldCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        VerificationSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            return 0;
        }

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
             SELECT count(*)
               FROM registry.document_fields f
               JOIN registry.documents d ON d.id = f.document_id
              WHERE f.verify_status = 'pending' AND {SubjectPredicate(subject)};
             """,
            new { Id = subject.Id },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<VerificationField>> FieldsByKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        VerificationSubject subject,
        string fieldKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.Type == VerificationSubjectTypes.Org)
        {
            return [];
        }

        var rows = await connection.QueryAsync<VerificationField>(new CommandDefinition(
            $"""
             SELECT {FieldColumns}
               FROM registry.document_fields f
               JOIN registry.documents d ON d.id = f.document_id
              WHERE f.field_key = @FieldKey AND {SubjectPredicate(subject)}
              ORDER BY d.created_at DESC, f.id DESC;
             """,
            new { Id = subject.Id, FieldKey = fieldKey },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// <para>
    /// <b>Every row of the key, not one row.</b> A licence is two documents and the officer confirms
    /// "the NIC number", not "the NIC number as read off the back". Deciding one row would leave the
    /// other pending and the subject unapprovable for a field the officer believes they cleared.
    /// </para>
    /// <para>
    /// <b>An edit reaches a field that is not flagged; a bare confirm does not.</b> AL-29's whole
    /// premise is that OCR can be confidently wrong, so correcting an <c>auto_verified</c> value is
    /// a legitimate act — while confirming one that nobody flagged would be a decision with no
    /// question in front of it, and re-confirming an already-confirmed field on a double click must
    /// change nothing.
    /// </para>
    /// <para>
    /// <b>An edited value becomes <c>manual</c> with no confidence.</b> It is no longer what the
    /// extractor read, and <c>ck_document_fields_manual_confidence</c> refuses a hand-entered value
    /// carrying a score — a number invented for something nobody scanned would read as evidence.
    /// </para>
    /// </remarks>
    public Task<int> ConfirmFieldAsync(
        IUnitOfWork unitOfWork,
        VerificationSubject subject,
        string fieldKey,
        string? value,
        Guid officerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(subject);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE registry.document_fields f
                SET field_value   = COALESCE(@Value, f.field_value),
                    source        = CASE WHEN @Value::text IS NULL THEN f.source ELSE 'manual' END,
                    confidence    = CASE WHEN @Value::text IS NULL THEN f.confidence ELSE NULL END,
                    verify_status = 'confirmed',
                    confirmed_by  = @OfficerId,
                    confirmed_at  = @Now
               FROM registry.documents d
              WHERE d.id = f.document_id
                AND f.field_key = @FieldKey
                AND (f.verify_status = 'pending' OR @Value::text IS NOT NULL)
                AND {SubjectPredicate(subject)};
             """,
            new { Id = subject.Id, FieldKey = fieldKey, Value = value, OfficerId = officerId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Which documents belong to this subject. Never interpolated from user input — the two
    /// branches are constants chosen by the resolved subject type.
    /// </summary>
    private static string SubjectPredicate(VerificationSubject subject) =>
        subject.Type == VerificationSubjectTypes.Vehicle
            ? "d.vehicle_id = @Id"
            // A driving licence is captured at Profile Setup and is vehicle-less (migration 0305's
            // own comment), which is exactly what separates a driver's documents from their
            // vehicles'.
            : "d.driver_id = @Id AND d.vehicle_id IS NULL";

    // ---------------------------------------------------------------------------------------
    // Decisions
    // ---------------------------------------------------------------------------------------

    public async Task<(string Status, string OnboardingStatus, string? RejectionReason)?> VehicleStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<VehicleStateDto>(new CommandDefinition(
            """
            SELECT status            AS Status,
                   onboarding_status AS OnboardingStatus,
                   rejection_reason  AS RejectionReason
              FROM registry.vehicles
             WHERE id = @Id;
            """,
            new { Id = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return row is null ? null : (row.Status, row.OnboardingStatus, row.RejectionReason);
    }

    public async Task<bool> RejectVehicleAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // DEACTIVATED is excluded rather than overwritten: a retired registration has already left
        // D-37's live set, and moving it to REJECTED would rewrite the end of its history.
        var rows = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.vehicles
               SET status = 'REJECTED', rejection_reason = @Reason
             WHERE id = @Id AND status <> 'DEACTIVATED';
            """,
            new { Id = vehicleId, Reason = reason },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    /// <remarks>
    /// The officer overturning their own refusal, and the only path by which it can happen:
    /// registry-svc's <c>ApplyApprovalAsync</c> declines to auto-approve a REJECTED vehicle
    /// ("a Verification Officer's decision that four green steps do not overturn"), so without this
    /// a resubmission after a rejection could never reach APPROVED. PENDING is inside
    /// <c>ux_vehicles_regno_active</c>'s predicate, so a plate taken in the meantime surfaces here
    /// as a unique-violation and is answered as a conflict rather than swallowed.
    /// </remarks>
    public async Task<bool> ClearVehicleRejectionAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var rows = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.vehicles
               SET status = 'PENDING', rejection_reason = NULL
             WHERE id = @Id AND status = 'REJECTED';
            """,
            new { Id = vehicleId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    public async Task<(DateTimeOffset? VerifiedAt, string? RejectionReason)?> DriverStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<DriverStateDto>(new CommandDefinition(
            """
            SELECT verified_at AS VerifiedAt, rejection_reason AS RejectionReason
              FROM registry.driver_profiles
             WHERE driver_id = @Id;
            """,
            new { Id = driverId },
            transaction,
            cancellationToken: cancellationToken));

        return row is null ? null : (row.VerifiedAt, row.RejectionReason);
    }

    public async Task<bool> ApproveDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // The refusal is cleared in the same statement: a profile that is both verified and carrying
        // a rejection reason is two answers to one question, and the driver's app would render the
        // stale one.
        var rows = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.driver_profiles
               SET verified_at = @Now, rejection_reason = NULL
             WHERE driver_id = @Id;
            """,
            new { Id = driverId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    public async Task<bool> RejectDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var rows = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.driver_profiles
               SET rejection_reason = @Reason, verified_at = NULL
             WHERE driver_id = @Id;
            """,
            new { Id = driverId, Reason = reason },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    // ---------------------------------------------------------------------------------------
    // The viewer
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// Two tables because there are two kinds of document and only one of them is an onboarding
    /// document. AL-49's payout evidence — a bank statement, a passbook page, a LankaQR image —
    /// lives in <c>docs.uploads</c> and never gets a <c>registry.documents</c> row, and the officer
    /// opens it from the same lightbox (SCR-AP-003b).
    /// </remarks>
    private const string FindDocumentSql =
        """
        SELECT d.id                    AS "DocId",
               d.kind                  AS "Kind",
               d.file_url              AS "StorageUrl",
               'registry.documents'::text AS "Source",
               d.driver_id             AS "OwnerId",
               d.fleet_id              AS "FleetId",
               d.vehicle_id            AS "VehicleId"
          FROM registry.documents d
         WHERE d.id = @Id
        UNION ALL
        SELECT u.id, COALESCE(u.kind, ''), u.storage_url, 'docs.uploads', u.owner_id, NULL::uuid, NULL::uuid
          FROM docs.uploads u
         WHERE u.id = @Id
        LIMIT 1;
        """;

    public async Task<StoredDocument?> FindDocumentAsync(Guid docId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<StoredDocument>(
            new CommandDefinition(FindDocumentSql, new { Id = docId }, cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>vehicleCount</c> is on <c>admin-bff.yaml</c>'s <c>OrgQueueRow</c> and is not on the row
    /// fleet-svc's internal queue answers with. Counted here rather than added there because it is
    /// a fact about <c>registry.fleet_vehicles</c>, which this service already reads for the other
    /// two queues, and because a per-row count on fleet-svc's side would be a query per
    /// organisation. One statement for the whole page.
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, int>> FleetVehicleCountsAsync(
        IReadOnlyCollection<Guid> fleetIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fleetIds);

        if (fleetIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(Guid FleetId, long Count)>(new CommandDefinition(
            """
            SELECT fleet_id AS FleetId, count(*) AS Count
              FROM registry.fleet_vehicles
             WHERE fleet_id = ANY(@FleetIds)
             GROUP BY fleet_id;
            """,
            new { FleetIds = fleetIds.ToArray() },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.FleetId, row => (int)row.Count);
    }

    // The two queue rows materialise through settable properties rather than a record constructor.
    // Dapper's DefaultTypeMap.FindConstructor matches parameters positionally AND by reader field
    // type, and Npgsql reports `text[]` as `System.Array` — so a `string[]` constructor parameter
    // makes the match fail at run time with "a parameterless default constructor ... is required".
    // The property path converts, which is what the aggregated `flaggedFields` needs.
    private sealed class DriverQueueDto
    {
        public Guid DriverId { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset SubmittedAt { get; set; }

        public string[]? FlaggedFields { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed class VehicleQueueDto
    {
        public Guid VehicleId { get; set; }

        public string RegNo { get; set; } = string.Empty;

        public Guid? OwnerDriverId { get; set; }

        public DateTimeOffset SubmittedAt { get; set; }

        public string[]? FlaggedFields { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed record VehicleStateDto(string Status, string OnboardingStatus, string? RejectionReason);

    private sealed record DriverStateDto(DateTimeOffset? VerifiedAt, string? RejectionReason);
}
