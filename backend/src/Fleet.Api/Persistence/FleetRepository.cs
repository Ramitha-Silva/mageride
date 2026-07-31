using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>registry.fleets</c> — the organisation itself (migrations 0301 and 0313).
/// </summary>
/// <remarks>
/// Every method takes the connection and transaction it runs on rather than opening its own. Reads
/// are handed a scope from <see cref="IFleetScopedReader"/>, so migration 1806's policies decide
/// what they can see; writes are handed a unit of work, so the organisation row and the membership
/// row that makes its owner an owner commit together.
/// </remarks>
public interface IFleetRepository
{
    Task<FleetOrganisation> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerId,
        string name,
        string businessReg,
        string contactPhone,
        string? contactEmail,
        string? address,
        CancellationToken cancellationToken);

    /// <summary>The organisation, or <see langword="null"/> when the caller's scope cannot see it.</summary>
    Task<FleetOrganisation?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether a live organisation already claims <paramref name="businessReg"/>.
    /// </summary>
    /// <remarks>
    /// A pre-check, not the guarantee: <c>ux_fleets_business_reg_active</c> (migration 0313) is
    /// what actually holds under concurrency. This exists so the common case answers 409 with a
    /// sentence rather than a unique-violation.
    /// </remarks>
    Task<bool> BusinessRegistrationIsTakenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string businessReg,
        CancellationToken cancellationToken);

    /// <summary>Applies an officer's decision. Returns <see langword="null"/> when there is no such org.</summary>
    Task<FleetOrganisation?> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        string status,
        string? rejectionReason,
        CancellationToken cancellationToken);

    /// <summary>The Verification Officer's fleet-org queue, oldest application first (AL-39).</summary>
    Task<IReadOnlyList<FleetQueueRow>> ListQueueAsync(
        NpgsqlConnection connection,
        string? status,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetRepository"/>
internal sealed class FleetRepository : IFleetRepository
{
    private const string Columns = """
        id, owner_id, name, business_reg, contact_phone, contact_email, address,
        status, rejection_reason, created_at, updated_at
        """;

    public async Task<FleetOrganisation> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerId,
        string name,
        string businessReg,
        string contactPhone,
        string? contactEmail,
        string? address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `status` is left to the column default rather than written: 0301 defaults it to
        // 'PENDING', and an INSERT that names the status is one refactor away from being able to
        // create an organisation that is already approved (US-13.A7).
        return await connection.QuerySingleAsync<FleetOrganisation>(new CommandDefinition(
            $"""
             INSERT INTO registry.fleets (owner_id, name, business_reg, contact_phone, contact_email, address)
             VALUES (@OwnerId, @Name, @BusinessReg, @ContactPhone, @ContactEmail, @Address)
             RETURNING {Columns};
             """,
            new { OwnerId = ownerId, Name = name, BusinessReg = businessReg, ContactPhone = contactPhone, ContactEmail = contactEmail, Address = address },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<FleetOrganisation?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<FleetOrganisation>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.fleets WHERE id = @FleetId;",
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> BusinessRegistrationIsTakenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string businessReg,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // lower() on both sides, matching ux_fleets_business_reg_active — a case-sensitive
        // pre-check would pass and then hit the index, which is the 500 this method exists to
        // avoid.
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
              SELECT 1 FROM registry.fleets
               WHERE lower(business_reg) = lower(@BusinessReg)
                 AND status IN ('PENDING','APPROVED'));
            """,
            new { BusinessReg = businessReg },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<FleetOrganisation?> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        string status,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The reason is cleared on approval rather than left behind: an APPROVED org still
        // carrying the sentence that rejected it last month is what SCR-FP-002 would render.
        return await connection.QuerySingleOrDefaultAsync<FleetOrganisation>(new CommandDefinition(
            $"""
             UPDATE registry.fleets
                SET status = @Status,
                    rejection_reason = CASE WHEN @Status = 'REJECTED' THEN @RejectionReason ELSE NULL END
              WHERE id = @FleetId
             RETURNING {Columns};
             """,
            new { FleetId = fleetId, Status = status, RejectionReason = rejectionReason },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FleetQueueRow>> ListQueueAsync(
        NpgsqlConnection connection,
        string? status,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Oldest first: a queue is worked in the order people applied, and `ix_fleets_status`
        // (0301) is partial on PENDING, which is the filter the officer opens with.
        //
        // The payout status is the LATEST version's, not "is there a verified one": an org whose
        // verified profile has just been edited must appear on the queue as pending, which is the
        // whole of AL-49's "any edit re-triggers verification".
        var rows = await connection.QueryAsync<FleetQueueRow>(new CommandDefinition(
            """
            SELECT f.id AS fleet_id, f.name, f.business_reg, f.contact_phone, f.status,
                   p.status AS payout_profile_status,
                   (SELECT count(*) FROM docs.uploads u
                     WHERE u.id IN (p.proof_upload_id, p.lankaqr_upload_id))::int AS document_count,
                   f.created_at
              FROM registry.fleets f
              LEFT JOIN LATERAL (
                    SELECT pp.status, pp.proof_upload_id, pp.lankaqr_upload_id
                      FROM registry.fleet_payout_profiles pp
                     WHERE pp.fleet_id = f.id AND pp.status <> 'superseded'
                     ORDER BY pp.created_at DESC, pp.id DESC
                     LIMIT 1) p ON true
             WHERE (@Status::text IS NULL OR f.status = @Status)
             ORDER BY f.created_at, f.id
             LIMIT @Limit;
            """,
            new { Status = status, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
