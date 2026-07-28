using Dapper;
using MageRide.Iam.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.saved_addresses</c> — Home, Work and labelled places (AL-14, AL-26, US-22.1/22.2).</summary>
public interface ISavedAddressRepository
{
    /// <summary>The caller's addresses, Home then Work then the rest by age.</summary>
    Task<IReadOnlyList<SavedAddress>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    Task<SavedAddress?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken);

    Task<SavedAddress> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SavedAddress address, CancellationToken cancellationToken);

    Task<SavedAddress?> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SavedAddress address, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears the Home or Work flag from whichever of the caller's other addresses holds it.
    /// </summary>
    /// <remarks>
    /// Run inside the same transaction as the insert or update that is claiming the flag. The
    /// two partial unique indexes (<c>uq_saved_home</c>, <c>uq_saved_work</c>, C003) are what
    /// actually enforce "at most one"; this is what makes moving the flag an edit rather than a
    /// <c>409</c> the user has to resolve by hand — the contract's wording for
    /// <c>PUT /v1/me/saved-addresses/{addressId}</c>.
    /// </remarks>
    Task ClearFlagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid? exceptAddressId,
        bool clearHome,
        bool clearWork,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISavedAddressRepository"/>
public sealed class SavedAddressRepository : ISavedAddressRepository
{
    private const string Columns =
        "id, user_id, label, line1, line2, line3, geo, is_home, is_work, created_at, updated_at";

    /// <summary>
    /// Home first, then Work, then newest-saved first — the order the booking screen's shortcut
    /// row and the Saved Addresses list both render in (D2 SCR-PA-026), so no client has to sort.
    /// </summary>
    private const string ListOrder = "ORDER BY is_home DESC, is_work DESC, created_at DESC, id";

    public async Task<IReadOnlyList<SavedAddress>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<SavedAddress>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.saved_addresses WHERE user_id = @UserId {ListOrder};",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<SavedAddress?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // user_id in the predicate, not checked afterwards: a caller must not be able to learn
        // that somebody else's address id exists by getting a different error for it.
        return connection.QuerySingleOrDefaultAsync<SavedAddress>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.saved_addresses WHERE id = @AddressId AND user_id = @UserId;",
            new { AddressId = addressId, UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SavedAddress> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SavedAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(address);

        return connection.QuerySingleAsync<SavedAddress>(new CommandDefinition(
            $"""
             INSERT INTO iam.saved_addresses (user_id, label, line1, line2, line3, geo, is_home, is_work)
             VALUES (@UserId, @Label, @Line1, @Line2, @Line3, @Geo, @IsHome, @IsWork)
             RETURNING {Columns};
             """,
            new
            {
                address.UserId,
                address.Label,
                address.Line1,
                address.Line2,
                address.Line3,
                address.Geo,
                address.IsHome,
                address.IsWork,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SavedAddress?> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SavedAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(address);

        return connection.QuerySingleOrDefaultAsync<SavedAddress>(new CommandDefinition(
            $"""
             UPDATE iam.saved_addresses
                SET label = @Label, line1 = @Line1, line2 = @Line2, line3 = @Line3,
                    geo = @Geo, is_home = @IsHome, is_work = @IsWork
              WHERE id = @Id AND user_id = @UserId
             RETURNING {Columns};
             """,
            new
            {
                address.Id,
                address.UserId,
                address.Label,
                address.Line1,
                address.Line2,
                address.Line3,
                address.Geo,
                address.IsHome,
                address.IsWork,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM iam.saved_addresses WHERE id = @AddressId AND user_id = @UserId;",
            new { AddressId = addressId, UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return deleted > 0;
    }

    public Task ClearFlagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        Guid? exceptAddressId,
        bool clearHome,
        bool clearWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!clearHome && !clearWork)
        {
            return Task.CompletedTask;
        }

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.saved_addresses
               SET is_home = is_home AND NOT @ClearHome,
                   is_work = is_work AND NOT @ClearWork
             WHERE user_id = @UserId
               AND (@ExceptId::uuid IS NULL OR id <> @ExceptId)
               AND ((@ClearHome AND is_home) OR (@ClearWork AND is_work));
            """,
            new
            {
                UserId = userId,
                ExceptId = exceptAddressId,
                ClearHome = clearHome,
                ClearWork = clearWork,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}

/// <summary>Helpers shared by the saved-address service and its tests.</summary>
public static class SavedAddressLabels
{
    public const string Home = "home";
    public const string Work = "work";

    /// <summary>
    /// Whether a label is one of the two reserved ones, in any casing.
    /// </summary>
    /// <remarks>
    /// D2 SCR-PA-026 gives Home and Work their own map pins and the rest a free-text
    /// "Save Address As". The reserved labels and the <c>is_home</c>/<c>is_work</c> booleans are
    /// two spellings of one fact (see <see cref="SavedAddress"/>), so the service keeps them in
    /// step rather than letting a client save a second address labelled "Home".
    /// </remarks>
    public static bool IsReserved(string? label) =>
        string.Equals(label, Home, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(label, Work, StringComparison.OrdinalIgnoreCase);
}
