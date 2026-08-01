using Dapper;
using MageRide.Iam.Domain;
using MageRide.Shared.Auth;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.users</c> and <c>iam.user_roles</c> (ADD §9.1, AL-06).</summary>
public interface IUserRepository
{
    Task<IamUser?> FindByPhoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, CancellationToken cancellationToken);

    Task<IamUser?> FindByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The portal identity for an address (AL-07). Case-insensitive: an email address is
    /// case-insensitive in practice and a sign-in that fails on a capital letter is a support
    /// ticket.
    /// </summary>
    Task<IamUser?> FindByEmailAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string email, CancellationToken cancellationToken);

    /// <summary>Creates the account a first sign-in implies, with <paramref name="role"/> as its primary role.</summary>
    Task<IamUser> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, string role, CancellationToken cancellationToken);

    /// <summary>Grants a canonical role. Idempotent — the grant table's PK settles a repeat.</summary>
    Task GrantRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string role, CancellationToken cancellationToken);

    /// <summary>Every role held. Effective permissions are their union (AL-06).</summary>
    Task<IReadOnlyList<string>> RolesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The account's fleet membership, or <see langword="null"/>. Becomes the
    /// <c>fleet_role</c>/<c>fleet_id</c> claim pair (AL-03).
    /// </summary>
    Task<FleetScope?> FleetScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>The roles and fleet scope one access token needs, in one round trip.</summary>
    Task<SessionPrincipal> PrincipalAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IUserRepository"/>
public sealed class UserRepository : IUserRepository
{
    private const string Columns =
        "id, phone, email, role, first_name, photo_url, language, operating_city_code, " +
        "default_payment_method, is_blocked, created_at";

    public Task<IamUser?> FindByPhoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<IamUser>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.users WHERE phone = @Phone;",
            new { Phone = phone },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<IamUser?> FindByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<IamUser>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.users WHERE id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<IamUser?> FindByEmailAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // lower() on both sides rather than citext: `iam.users.email` is TEXT with a UNIQUE
        // index (C003), so a citext comparison would not use it and every portal sign-in would
        // be a sequential scan of the user table.
        return connection.QuerySingleOrDefaultAsync<IamUser>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.users WHERE lower(email) = lower(@Email);",
            new { Email = email.Trim() },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<IamUser> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ON CONFLICT rather than a bare INSERT: two verifies racing on the same new number would
        // otherwise both pass the "does this phone exist" read and one would die on the unique
        // index. DO UPDATE (not DO NOTHING) so the RETURNING clause always yields the row.
        return connection.QuerySingleAsync<IamUser>(new CommandDefinition(
            $"""
             INSERT INTO iam.users (phone, role)
             VALUES (@Phone, @Role)
             ON CONFLICT (phone) DO UPDATE SET phone = EXCLUDED.phone
             RETURNING {Columns};
             """,
            new { Phone = phone, Role = role },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task GrantRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.user_roles (user_id, role)
            VALUES (@UserId, @Role)
            ON CONFLICT (user_id, role) DO NOTHING;
            """,
            new { UserId = userId, Role = role },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> RolesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The union of the grant table and the primary role: iam.users.role is authoritative for
        // the account's own role even where no grant row was written (C003's DDL note).
        var roles = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT role FROM iam.user_roles WHERE user_id = @UserId
            UNION
            SELECT role FROM iam.users WHERE id = @UserId
            ORDER BY 1;
            """,
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. roles];
    }

    public Task<FleetScope?> FleetScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // A person can be a member of more than one fleet; the token carries one pair, so the
        // most privileged membership wins and the rest are reachable per-fleet through fleet-svc
        // (C058). owner > manager > viewer, matching FleetRoles.Rank.
        return connection.QuerySingleOrDefaultAsync<FleetScope>(new CommandDefinition(
            """
            SELECT fleet_id, fleet_role
              FROM iam.fleet_members
             WHERE user_id = @UserId
             ORDER BY CASE fleet_role WHEN 'owner' THEN 0 WHEN 'manager' THEN 1 ELSE 2 END, fleet_id
             LIMIT 1;
            """,
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<SessionPrincipal> PrincipalAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        var roles = await RolesAsync(connection, transaction, userId, cancellationToken);
        var fleet = await FleetScopeAsync(connection, transaction, userId, cancellationToken);

        return new SessionPrincipal(userId, roles, fleet);
    }
}
