using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.AdminBff.Persistence;

/// <summary>What an erasure actually removed, so the compliance artifact can say so.</summary>
/// <param name="EmergencyContacts">Rows deleted from <c>iam.emergency_contacts</c>.</param>
/// <param name="SavedAddresses">Rows deleted from <c>iam.saved_addresses</c>.</param>
/// <param name="PhoneLookups">Rows deleted from <c>iam.phone_lookups</c> — the keyed digest of their number.</param>
/// <param name="SessionsRevoked">Live <c>iam.sessions</c> ended, so the erased account cannot go on being used.</param>
/// <param name="DriverProfile">Whether a <c>registry.driver_profiles</c> row was anonymised too.</param>
public sealed record ErasureOutcome(
    int EmergencyContacts,
    int SavedAddresses,
    int PhoneLookups,
    int SessionsRevoked,
    bool DriverProfile);

/// <summary>
/// <c>pdpa.requests</c>, <c>pdpa.fulfillment_artifacts</c>, and the two reads an E-06 decision needs
/// before it can be made (§16, US-1.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>iam-svc records the request; this fulfils it, and the split is deliberate.</b> iam-svc's
/// <c>DELETE /v1/users/me</c> writes a <c>pdpa.requests</c> row and "touches nothing else" — its own
/// file says so, and the reason is that erasure may be refused or held, so an account whose request
/// is rejected must be found exactly as its owner left it. Everything that changes the account is
/// here, behind an RBAC gate and an audit row.
/// </para>
/// <para>
/// <b>The anonymisation is a soft one and the row survives.</b> <c>DELETE FROM iam.users</c> is not
/// an option and never was: <c>rides.rides.passenger_id</c>, <c>billing.accounts.owner_id</c>,
/// <c>audit.events.actor_id</c> and a dozen more reference it, and a cascade would take the
/// financial history and the audit trail with it — the two things the statutory hold list exists to
/// keep. So the identifying columns are overwritten and <c>anonymised_at</c> (migration 0110)
/// records that it happened.
/// </para>
/// </remarks>
public interface IPdpaRepository
{
    /// <summary>The subject's still-open request of this kind, if any. Two clocks for one obligation is a bug.</summary>
    Task<PdpaRequestRow?> FindOpenAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string kind,
        CancellationToken cancellationToken);

    Task<PdpaRequestRow> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string kind,
        CancellationToken cancellationToken);

    Task<PdpaRequestRow?> FindAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Locks the request for a decision, so two operators cannot fulfil one obligation twice.</summary>
    Task<PdpaRequestRow?> LockAsync(IUnitOfWork unitOfWork, Guid requestId, CancellationToken cancellationToken);

    /// <summary>The admin queue: open requests by deadline, or decided ones newest first.</summary>
    Task<IReadOnlyList<PdpaRequestRow>> QueueAsync(
        string? status, int limit, CancellationToken cancellationToken);

    Task<PdpaRequestRow> DecideAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string status,
        Guid decidedBy,
        string? holdReason,
        string? decisionReason,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<PdpaArtifactRow> AddArtifactAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string kind,
        string storageUrl,
        byte[]? sha256,
        DateTimeOffset signedAt,
        CancellationToken cancellationToken);

    /// <summary>The newest artifact of a request, which is what a download link points at.</summary>
    Task<PdpaArtifactRow?> FindArtifactAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Whether this account has already been erased (migration 0110).</summary>
    Task<DateTimeOffset?> AnonymisedAtAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Every statutory hold that applies to <paramref name="userId"/> right now.</summary>
    Task<IReadOnlyList<StatutoryHold>> HoldsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>Overwrites the identifying columns and revokes the live sessions.</summary>
    Task<ErasureOutcome> AnonymiseAsync(
        IUnitOfWork unitOfWork, Guid userId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>One dataset of the export archive, as JSON rows straight from Postgres.</summary>
    Task<IReadOnlyList<string>> ExportDatasetAsync(
        string sql, Guid userId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPdpaRepository"/>
internal sealed class PdpaRepository(INpgsqlConnectionFactory connections) : IPdpaRepository
{
    private const string Columns =
        """
        id AS "Id", user_id AS "UserId", kind AS "Kind", status AS "Status",
        requested_at AS "RequestedAt", due_by AS "DueBy", fulfilled_at AS "FulfilledAt",
        hold_reason AS "HoldReason", decided_by AS "DecidedBy", decision_reason AS "DecisionReason"
        """;

    public Task<PdpaRequestRow?> FindOpenAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PdpaRequestRow>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM pdpa.requests
              WHERE user_id = @UserId AND kind = @Kind AND status = ANY(@Open)
              ORDER BY requested_at
              LIMIT 1;
             """,
            new { UserId = userId, Kind = kind, Open = PdpaStatuses.Open.ToArray() },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>due_by</c> is left to the column's own <c>now() + INTERVAL '30 days'</c> default (migration
    /// 1306) rather than computed here — the same choice iam-svc made, and the reason the two agree
    /// about a statutory deadline without either of them owning it.
    /// </remarks>
    public Task<PdpaRequestRow> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<PdpaRequestRow>(new CommandDefinition(
            $"INSERT INTO pdpa.requests (user_id, kind) VALUES (@UserId, @Kind) RETURNING {Columns};",
            new { UserId = userId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PdpaRequestRow?> FindAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PdpaRequestRow>(new CommandDefinition(
            $"SELECT {Columns} FROM pdpa.requests WHERE id = @Id;",
            new { Id = requestId },
            cancellationToken: cancellationToken));
    }

    public Task<PdpaRequestRow?> LockAsync(
        IUnitOfWork unitOfWork, Guid requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<PdpaRequestRow>(new CommandDefinition(
            $"SELECT {Columns} FROM pdpa.requests WHERE id = @Id FOR UPDATE;",
            new { Id = requestId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// Two orderings for two questions, and the <c>CASE</c> is what lets one index serve each: open
    /// requests come back by deadline (<c>ix_pdpa_requests_due</c>) because the SLA is the whole
    /// point of that list, and decided ones newest first (<c>ix_pdpa_requests_decided</c>) because
    /// that list is a history.
    /// </remarks>
    public async Task<IReadOnlyList<PdpaRequestRow>> QueueAsync(
        string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PdpaRequestRow>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM pdpa.requests
              WHERE (@Status::text IS NULL AND status = ANY(@Open))
                 OR (@Status::text IS NOT NULL AND status = @Status)
              ORDER BY CASE WHEN status = ANY(@Open) THEN due_by END ASC NULLS LAST,
                       fulfilled_at DESC NULLS LAST
              LIMIT @Limit;
             """,
            new { Status = status, Open = PdpaStatuses.Open.ToArray(), Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<PdpaRequestRow> DecideAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string status,
        Guid decidedBy,
        string? holdReason,
        string? decisionReason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleAsync<PdpaRequestRow>(new CommandDefinition(
            $"""
             UPDATE pdpa.requests
                SET status          = @Status,
                    fulfilled_at    = @At,
                    decided_by      = @DecidedBy,
                    hold_reason     = @HoldReason,
                    decision_reason = @DecisionReason
              WHERE id = @Id
             RETURNING {Columns};
             """,
            new
            {
                Id = requestId,
                Status = status,
                At = at,
                DecidedBy = decidedBy,
                HoldReason = holdReason,
                DecisionReason = decisionReason,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PdpaArtifactRow> AddArtifactAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string kind,
        string storageUrl,
        byte[]? sha256,
        DateTimeOffset signedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleAsync<PdpaArtifactRow>(new CommandDefinition(
            """
            INSERT INTO pdpa.fulfillment_artifacts (request_id, kind, storage_url, sha256, signed_at)
            VALUES (@RequestId, @Kind, @StorageUrl, @Sha256, @SignedAt)
            RETURNING id AS "Id", request_id AS "RequestId", kind AS "Kind",
                      storage_url AS "StorageUrl", sha256 AS "Sha256", signed_at AS "SignedAt";
            """,
            new
            {
                RequestId = requestId,
                Kind = kind,
                StorageUrl = storageUrl,
                Sha256 = sha256,
                SignedAt = signedAt,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PdpaArtifactRow?> FindArtifactAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PdpaArtifactRow>(new CommandDefinition(
            """
            SELECT id AS "Id", request_id AS "RequestId", kind AS "Kind",
                   storage_url AS "StorageUrl", sha256 AS "Sha256", signed_at AS "SignedAt"
              FROM pdpa.fulfillment_artifacts
             WHERE request_id = @RequestId
             ORDER BY signed_at DESC NULLS LAST, id DESC
             LIMIT 1;
            """,
            new { RequestId = requestId },
            cancellationToken: cancellationToken));
    }

    public async Task<DateTimeOffset?> AnonymisedAtAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT anonymised_at FROM iam.users WHERE id = @Id;",
            new { Id = userId },
            cancellationToken: cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // The statutory hold list
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>One round trip, six counts, and each one names the statute or the operation it protects.</b>
    /// The three blocking holds are live operations anonymising the account would break; the three
    /// retention holds are records a statute requires be kept and are what turns <c>Fulfilled</c>
    /// into <c>FulfilledHold</c>.
    /// </para>
    /// <para>
    /// <b>An active ride is read from both writers.</b> <c>rides.rides</c> is Mode C and
    /// <c>trips.sessions</c> is Mode A/B; a subject may be the passenger of one and the driver of the
    /// other, and a hold list that looked at only one would anonymise a bus driver mid-route. The
    /// terminal-state list is spelled out rather than negated against a shorter one, because a state
    /// added to the enum later must default to "not terminal" and therefore to a hold.
    /// </para>
    /// </remarks>
    private const string HoldsSql =
        """
        SELECT 'active-ride'      AS "Code", true  AS "Blocking",
               ((SELECT count(*) FROM rides.rides r
                  WHERE (r.passenger_id = @Id OR r.booker_id = @Id OR r.accepted_driver_id = @Id)
                    AND r.terminal_at IS NULL)
              + (SELECT count(*) FROM trips.sessions s
                  WHERE s.driver_id = @Id AND s.state = 'ACTIVE'))::int AS "Count"
        UNION ALL
        SELECT 'open-dispute', true,
               (SELECT count(*)::int FROM support.tickets t
                 WHERE t.user_id = @Id AND t.status <> 'RESOLVED')
        UNION ALL
        SELECT 'unsettled-payment', true,
               ((SELECT count(*) FROM fares.ride_payments rp
                   JOIN rides.rides r ON r.id = rp.ride_id
                  WHERE (r.passenger_id = @Id OR r.booker_id = @Id OR rp.payer_user_id = @Id)
                    AND rp.state IN ('Initiated','Pending','CashOnDelivery','Overpaid',
                                     'QrClaimedByPassenger','Disputed'))
              + (SELECT count(*) FROM fares.refunds f
                   JOIN fares.ride_payments rp ON rp.id = f.ride_payment_id
                   JOIN rides.rides r          ON r.id = rp.ride_id
                  WHERE (r.passenger_id = @Id OR r.booker_id = @Id)
                    AND f.status IN ('Requested','Submitted')))::int
        UNION ALL
        SELECT 'wallet-balance', true,
               (SELECT count(*)::int FROM billing.accounts a
                 WHERE a.owner_id = @Id AND a.balance_minor <> 0)
        UNION ALL
        SELECT 'financial-records', false,
               (SELECT count(*)::int FROM billing.journal_postings jp
                  JOIN billing.accounts a ON a.id = jp.account_id
                 WHERE a.owner_id = @Id)
        UNION ALL
        SELECT 'audit-trail', false,
               (SELECT count(*)::int FROM audit.events e
                 WHERE e.actor_id = @Id OR e.entity_id = @Id);
        """;

    public async Task<IReadOnlyList<StatutoryHold>> HoldsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<StatutoryHold>(new CommandDefinition(
            HoldsSql, new { Id = userId }, transaction, cancellationToken: cancellationToken));

        // A hold with nothing behind it is not a hold. Filtered here rather than in SQL so the
        // query stays one shape and the counts stay auditable in a log.
        return [.. rows.Where(hold => hold.Count > 0)];
    }

    // ---------------------------------------------------------------------------------------
    // The erasure itself
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>Every column overwritten here is one that identifies a person; nothing operational is
    /// touched.</b> The account keeps its id, its role, its city and its created_at, so every ride,
    /// posting and audit row that points at it still resolves and every count that has ever included
    /// it still does.
    /// </para>
    /// <para>
    /// <b><c>phone</c> becomes NULL and <c>email</c> becomes a per-account <c>.invalid</c> address,
    /// which is not a cosmetic choice.</b> <c>ck_users_credential</c> requires one of the two to be
    /// non-null, so both cannot be cleared; both columns are UNIQUE, so a shared placeholder would
    /// let the first erasure block every one after it. RFC 2606 reserves <c>.invalid</c> precisely so
    /// that a value which must exist and must not resolve has somewhere to go — and no credential row
    /// survives, so it cannot be signed in with.
    /// </para>
    /// <para>
    /// <b><c>registry.driver_profiles.display_name</c> is emptied rather than nulled</b> because the
    /// column is <c>NOT NULL</c> (migration 0304), and an empty string is the only value that both
    /// satisfies that and carries no identity. The directories render it as a blank row whose status
    /// is <c>deleted</c>, which is the honest thing to show.
    /// </para>
    /// </remarks>
    private const string AnonymiseSql =
        """
        UPDATE iam.users
           SET phone                   = NULL,
               email                   = 'erased-' || id::text || '@pdpa.invalid',
               first_name              = NULL,
               photo_url               = NULL,
               emergency_contact_name  = NULL,
               emergency_contact_phone = NULL,
               notif_prefs             = '{}'::jsonb,
               anonymised_at           = @At
         WHERE id = @Id;

        UPDATE registry.driver_profiles
           SET display_name = '', photo_url = NULL, nic_no = NULL, allowed_vehicle_types = NULL
         WHERE driver_id = @Id;

        -- The C032 handoff named this by name for this component: `rides.rides.rider_phone_hash` is
        -- the number a booker typed for a proxy rider (P-03) and is a keyed digest, not reversible
        -- but still linkable. Cleared only where the SUBJECT is that rider — where they are the
        -- booker the hash is somebody else's number, and `ck_rides_proxy` requires one of the two
        -- columns to survive anyway.
        UPDATE rides.rides SET rider_phone_hash = NULL
         WHERE rider_id = @Id AND rider_phone_hash IS NOT NULL;
        """;

    public async Task<ErasureOutcome> AnonymiseAsync(
        IUnitOfWork unitOfWork, Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var connection = unitOfWork.Connection;
        var transaction = unitOfWork.Transaction;

        async Task<int> ExecuteAsync(string sql) => await connection.ExecuteAsync(new CommandDefinition(
            sql, new { Id = userId, At = at }, transaction, cancellationToken: cancellationToken));

        var contacts = await ExecuteAsync("DELETE FROM iam.emergency_contacts WHERE user_id = @Id;");
        var addresses = await ExecuteAsync("DELETE FROM iam.saved_addresses WHERE user_id = @Id;");

        // The keyed digest of their number. Left behind it would keep the account discoverable by
        // the one identifier the anonymisation just removed (C026's Auth:PhoneHashKey).
        var lookups = await ExecuteAsync("DELETE FROM iam.phone_lookups WHERE user_id = @Id;");

        var sessions = await ExecuteAsync(
            "UPDATE iam.sessions SET revoked_at = @At WHERE user_id = @Id AND revoked_at IS NULL;");

        var driverProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM registry.driver_profiles WHERE driver_id = @Id);",
            new { Id = userId },
            transaction,
            cancellationToken: cancellationToken));

        await ExecuteAsync(AnonymiseSql);

        return new ErasureOutcome(contacts, addresses, lookups, sessions, driverProfile);
    }

    /// <remarks>
    /// The dataset SQL is a compile-time constant from <c>PdpaExport</c> and never a caller's string;
    /// the only bound values are the subject and the cap. Each statement is written to return a
    /// single <c>jsonb</c> column so the archive carries what Postgres already knows how to render
    /// rather than a CLR shape that would have to be kept in step with a dozen other services'
    /// tables.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ExportDatasetAsync(
        string sql, Guid userId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            sql, new { Id = userId, Limit = limit }, cancellationToken: cancellationToken));

        return [.. rows];
    }
}
