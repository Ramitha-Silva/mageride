using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.emergency_contacts</c> — the driver SOS fan-out list (AL-13, US-12.1/12.9).</summary>
public interface IEmergencyContactRepository
{
    /// <summary>
    /// The caller's contacts, oldest first.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing, not cosmetic: the first row is the primary, and the primary is what
    /// is copied onto <c>iam.users.emergency_contact_name</c>/<c>.emergency_contact_phone</c> for
    /// the D-33 SOS fast path. "Oldest" makes promotion after a delete deterministic without a
    /// column the schema does not have.
    /// </remarks>
    Task<IReadOnlyList<EmergencyContact>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    Task<EmergencyContact> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string name,
        string phone,
        CancellationToken cancellationToken);

    Task<EmergencyContact?> UpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid contactId,
        string name,
        string phone,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid contactId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites the denormalised primary contact on <c>iam.users</c>. Both arguments
    /// <see langword="null"/> clears it, which is what deleting the last contact does — and puts
    /// <c>POST /v1/sos</c> back to <c>400 no-emergency-contact</c>.
    /// </summary>
    Task SetPrimaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string? name,
        string? phone,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEmergencyContactRepository"/>
public sealed class EmergencyContactRepository : IEmergencyContactRepository
{
    private const string Columns = "id, user_id, name, phone, created_at, updated_at";

    public async Task<IReadOnlyList<EmergencyContact>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // is_primary is computed, not stored: the row ordering decides it, so there is no second
        // place for the answer to be wrong. `id` breaks a tie between two contacts saved inside
        // the same clock tick.
        var rows = await connection.QueryAsync<ContactRow>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.emergency_contacts WHERE user_id = @UserId ORDER BY created_at, id;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows.Select((row, index) => row.ToContact(isPrimary: index == 0))];
    }

    public async Task<EmergencyContact> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string name,
        string phone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleAsync<ContactRow>(new CommandDefinition(
            $"""
             INSERT INTO iam.emergency_contacts (user_id, name, phone)
             VALUES (@UserId, @Name, @Phone)
             RETURNING {Columns};
             """,
            new { UserId = userId, Name = name, Phone = phone },
            transaction,
            cancellationToken: cancellationToken));

        // The caller re-lists to learn whether this became the primary; a fresh insert is only
        // primary when it is also the only one.
        return row.ToContact(isPrimary: false);
    }

    public async Task<EmergencyContact?> UpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid contactId,
        string name,
        string phone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<ContactRow>(new CommandDefinition(
            $"""
             UPDATE iam.emergency_contacts
                SET name = @Name, phone = @Phone
              WHERE id = @ContactId AND user_id = @UserId
             RETURNING {Columns};
             """,
            new { ContactId = contactId, UserId = userId, Name = name, Phone = phone },
            transaction,
            cancellationToken: cancellationToken));

        return row?.ToContact(isPrimary: false);
    }

    public async Task<bool> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid contactId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM iam.emergency_contacts WHERE id = @ContactId AND user_id = @UserId;",
            new { ContactId = contactId, UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return deleted > 0;
    }

    public Task SetPrimaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string? name,
        string? phone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.users
               SET emergency_contact_name = @Name, emergency_contact_phone = @Phone
             WHERE id = @UserId;
            """,
            new { UserId = userId, Name = name, Phone = phone },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record ContactRow(
        Guid Id, Guid UserId, string Name, string Phone, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public EmergencyContact ToContact(bool isPrimary) =>
            new(Id, UserId, Name, Phone, isPrimary, CreatedAt, UpdatedAt);
    }
}
