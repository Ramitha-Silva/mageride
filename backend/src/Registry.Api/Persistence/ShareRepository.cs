using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary><c>registry.shares</c> — Mode B tracking grants (D-22, 0306).</summary>
public interface IShareRepository
{
    /// <summary>
    /// Creates a grant in <see cref="ShareStates.Pending"/>. Returns <see langword="null"/> when
    /// <c>ux_shares_active</c> already holds a live grant for the pair, rather than throwing — a
    /// duplicate is an expected answer here, not a fault.
    /// </summary>
    Task<ShareGrant?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid granteeUserId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    Task<ShareGrant?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid grantId,
        CancellationToken cancellationToken);

    /// <summary>US-4.3b: visibility begins at acceptance, not at grant creation.</summary>
    Task<ShareGrant?> AcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        Guid granteeUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a grant. Returns <see langword="null"/> when it was already REVOKED or EXPIRED, so
    /// a repeat cannot emit a second <c>share.revoked</c>.
    /// </summary>
    Task<ShareGrant?> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every live grant on a vehicle and returns them — what deactivation owes the people
    /// currently watching it (US-2.16).
    /// </summary>
    Task<IReadOnlyList<ShareGrant>> RevokeAllForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IShareRepository"/>
public sealed class ShareRepository : IShareRepository
{
    /// <summary>Unique-violation. Postgres reports every unique index breach as 23505.</summary>
    private const string UniqueViolation = "23505";

    private const string Columns =
        "id, vehicle_id, grantee_user_id, state, expires_at, accepted_at, revoked_at, created_at";

    /// <summary>The two states <c>ux_shares_active</c> treats as occupying the slot.</summary>
    private const string LiveStates = $"('{ShareStates.Pending}', '{ShareStates.Accepted}')";

    public async Task<ShareGrant?> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid granteeUserId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            // No pre-flight SELECT: two grants racing on one pair would both pass it and one would
            // still die on the index. The index is the check.
            return await connection.QuerySingleAsync<ShareGrant>(new CommandDefinition(
                $"""
                 INSERT INTO registry.shares (vehicle_id, grantee_user_id, expires_at)
                 VALUES (@VehicleId, @GranteeUserId, @ExpiresAt)
                 RETURNING {Columns};
                 """,
                new { VehicleId = vehicleId, GranteeUserId = granteeUserId, ExpiresAt = expiresAt },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == "ux_shares_active")
        {
            return null;
        }
    }

    public Task<ShareGrant?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Scoped by vehicle in the statement: a grant id from another vehicle's roster must not
        // resolve just because the caller owns *a* vehicle.
        return connection.QuerySingleOrDefaultAsync<ShareGrant>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.shares WHERE id = @GrantId AND vehicle_id = @VehicleId;",
            new { GrantId = grantId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<ShareGrant?> AcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        Guid granteeUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The grantee is a predicate, not a check the caller runs afterwards — only the person the
        // grant names may accept it (US-4.3b). Accepting an already-ACCEPTED grant returns the row
        // unchanged, so a retried request is a 200 rather than a 409.
        return connection.QuerySingleOrDefaultAsync<ShareGrant>(new CommandDefinition(
            $"""
             UPDATE registry.shares
                SET state = '{ShareStates.Accepted}',
                    accepted_at = COALESCE(accepted_at, @Now)
              WHERE id = @GrantId
                AND grantee_user_id = @GranteeUserId
                AND state IN {LiveStates}
             RETURNING {Columns};
             """,
            new { GrantId = grantId, GranteeUserId = granteeUserId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<ShareGrant?> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<ShareGrant>(new CommandDefinition(
            $"""
             UPDATE registry.shares
                SET state = '{ShareStates.Revoked}', revoked_at = @Now
              WHERE id = @GrantId AND state IN {LiveStates}
             RETURNING {Columns};
             """,
            new { GrantId = grantId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ShareGrant>> RevokeAllForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // RETURNING every row, because each one owes its grantee a directed removal (D-22) and the
        // caller writes one outbox event per grantee.
        var revoked = await connection.QueryAsync<ShareGrant>(new CommandDefinition(
            $"""
             UPDATE registry.shares
                SET state = '{ShareStates.Revoked}', revoked_at = @Now
              WHERE vehicle_id = @VehicleId AND state IN {LiveStates}
             RETURNING {Columns};
             """,
            new { VehicleId = vehicleId, Now = now },
            transaction,
            cancellationToken: cancellationToken));

        return [.. revoked];
    }
}
