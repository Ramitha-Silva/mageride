using Dapper;
using MageRide.Fleet.Domain;
using MageRide.Shared.Auth;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>iam.fleet_members</c> — the org-scoped Owner/Manager/Viewer sub-roles (AL-03, migration 0302).
/// </summary>
/// <remarks>
/// <para>
/// <b>fleet-svc writes an <c>iam</c> table, and that is deliberate.</b> US-13.A5 makes sub-users
/// the Fleet Owner's to provision — explicitly <em>not</em> subject to the "internal roles are
/// provisioned only by Super Admin" rule — so the decision belongs on the Fleet Portal's service,
/// and the C058 deliverable names the table. iam-svc <em>reads</em> the same rows to mint the
/// <c>fleet_role</c> claim (C027, <c>UserRepository.FleetMembershipAsync</c>); the two do not
/// overlap.
/// </para>
/// <para>
/// <b>A membership read is the authority; the token's claim is not.</b> A person may belong to
/// several organisations and the token carries one pair — iam-svc picks the most privileged. So
/// every request resolves the caller's role <em>for the org in the path</em> from here, and the
/// claim is used for nothing but the deny-by-default policy that got the request through
/// authorization.
/// </para>
/// </remarks>
public interface IFleetMemberRepository
{
    /// <summary>
    /// The caller's sub-role in one organisation, or <see langword="null"/> when they hold none.
    /// </summary>
    /// <remarks>
    /// Reads the base table rather than <c>iam.fleet_members_fleet</c> on purpose: this is the
    /// call that <em>establishes</em> the scope, so it runs before there is one.
    /// </remarks>
    Task<string?> RoleForAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>The organisation's team, read through the fleet-scoped view.</summary>
    Task<IReadOnlyList<FleetMember>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        int limit,
        CancellationToken cancellationToken);

    Task<int> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Grants a sub-role. Returns <see langword="null"/> when the person already holds one here.
    /// </summary>
    Task<FleetMembership?> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid userId,
        string fleetRole,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetMemberRepository"/>
internal sealed class FleetMemberRepository : IFleetMemberRepository
{
    public async Task<string?> RoleForAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT fleet_role FROM iam.fleet_members
             WHERE fleet_id = @FleetId AND user_id = @UserId;
            """,
            new { FleetId = fleetId, UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FleetMember>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Through the view, so the join onto iam.users is the only reach this service's read role
        // has into that table (migration 1806). Owner first, then manager, then viewer: the list
        // renders as an org chart, and `created_at` alone would put whoever was added first at the
        // top whatever their seat.
        var rows = await connection.QueryAsync<FleetMember>(new CommandDefinition(
            """
            SELECT fleet_id, user_id, fleet_role, email, first_name AS name, is_blocked, created_at
              FROM iam.fleet_members_fleet
             WHERE fleet_id = @FleetId
             ORDER BY CASE fleet_role WHEN 'owner' THEN 0 WHEN 'manager' THEN 1 ELSE 2 END,
                      created_at, user_id
             LIMIT @Limit;
            """,
            new { FleetId = fleetId, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<int> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM iam.fleet_members WHERE fleet_id = @FleetId;",
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<FleetMembership?> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid userId,
        string fleetRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!FleetRoles.All.Contains(fleetRole))
        {
            throw new ArgumentException($"'{fleetRole}' is not a fleet sub-role (AL-03).", nameof(fleetRole));
        }

        // DO NOTHING rather than DO UPDATE: changing somebody's seat is a decision with its own
        // audit story, and a POST that silently promoted a Viewer to Manager because the caller
        // re-sent the form is not that decision. RETURNING then gives no row, which the endpoint
        // turns into a 409.
        //
        // `created_at` comes back from the row rather than being stamped in the service: it is the
        // instant the seat existed, and every other timestamp this service returns is Postgres's.
        return await connection.QuerySingleOrDefaultAsync<FleetMembership>(new CommandDefinition(
            """
            INSERT INTO iam.fleet_members (fleet_id, user_id, fleet_role)
            VALUES (@FleetId, @UserId, @FleetRole)
            ON CONFLICT (fleet_id, user_id) DO NOTHING
            RETURNING fleet_id, user_id, fleet_role, created_at;
            """,
            new { FleetId = fleetId, UserId = userId, FleetRole = fleetRole },
            transaction,
            cancellationToken: cancellationToken));
    }
}
