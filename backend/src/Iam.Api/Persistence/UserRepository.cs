using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.users</c> and <c>iam.user_roles</c> (ADD §9.1, AL-06).</summary>
public interface IUserRepository
{
    Task<IamUser?> FindByPhoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, CancellationToken cancellationToken);

    Task<IamUser?> FindByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>Creates the account a first sign-in implies, with <paramref name="role"/> as its primary role.</summary>
    Task<IamUser> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phone, string role, CancellationToken cancellationToken);

    /// <summary>Grants a canonical role. Idempotent — the grant table's PK settles a repeat.</summary>
    Task GrantRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, string role, CancellationToken cancellationToken);

    /// <summary>Every role held. Effective permissions are their union (AL-06).</summary>
    Task<IReadOnlyList<string>> RolesAsync(
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
}
