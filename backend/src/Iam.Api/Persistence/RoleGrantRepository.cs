using Dapper;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>One row of <c>iam.user_roles</c>, with the provenance AL-06 requires.</summary>
public sealed record RoleGrant(string Role, Guid? GrantedBy, DateTimeOffset GrantedAt);

/// <summary>One row of <c>iam.roles</c> — the admin-readable catalog.</summary>
public sealed record RoleCatalogEntry(string Role, string Label, bool IsInternal);

/// <summary>
/// The RBAC write side: <c>iam.roles</c>, <c>iam.user_roles</c> (AL-06).
/// </summary>
/// <remarks>
/// Grants only. The <em>permission</em> half of "assign roles, define permissions" is
/// <see cref="Rbac.PermissionMatrix"/>, which is compiled in and read-only for the reason argued
/// there: the principal who would edit it is the principal it constrains.
/// </remarks>
public interface IRoleGrantRepository
{
    Task<IReadOnlyList<RoleCatalogEntry>> CatalogAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleGrant>> GrantsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>Grants a role. Idempotent — the primary key settles a repeat, and a repeat re-stamps nothing.</summary>
    Task GrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string role,
        Guid grantedBy,
        CancellationToken cancellationToken);

    /// <summary><see langword="true"/> when a grant row was removed.</summary>
    Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string role,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRoleGrantRepository"/>
public sealed class RoleGrantRepository : IRoleGrantRepository
{
    public async Task<IReadOnlyList<RoleCatalogEntry>> CatalogAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Ordered by scope then name: the three end-user roles first, then the six the ADD calls
        // internal, which is the order URD §2.1 numbers them in.
        var rows = await connection.QueryAsync<RoleCatalogEntry>(new CommandDefinition(
            "SELECT role, label, is_internal FROM iam.roles ORDER BY is_internal, role;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<RoleGrant>> GrantsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<RoleGrant>(new CommandDefinition(
            "SELECT role, granted_by, granted_at FROM iam.user_roles WHERE user_id = @UserId ORDER BY role;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task GrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string role,
        Guid grantedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // DO NOTHING, not DO UPDATE: re-granting a role somebody already holds must not rewrite
        // granted_by and granted_at. Those two columns are the only record of who let this
        // account in and when, and an idempotent retry is not a new decision.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.user_roles (user_id, role, granted_by)
            VALUES (@UserId, @Role, @GrantedBy)
            ON CONFLICT (user_id, role) DO NOTHING;
            """,
            new { UserId = userId, Role = role, GrantedBy = grantedBy },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var removed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM iam.user_roles WHERE user_id = @UserId AND role = @Role;",
            new { UserId = userId, Role = role },
            transaction,
            cancellationToken: cancellationToken));

        return removed > 0;
    }
}
