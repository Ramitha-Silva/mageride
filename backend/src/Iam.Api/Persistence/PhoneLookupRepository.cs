using Dapper;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.phone_lookups</c> — the P-03 registration-oracle audit (migration 0108).</summary>
public interface IPhoneLookupRepository
{
    /// <summary>
    /// Records that a lookup happened. <paramref name="phoneHash"/> is already an HMAC — this
    /// interface takes no phone number, so there is no call site from which the clear value could
    /// reach the table.
    /// </summary>
    Task RecordAsync(
        NpgsqlConnection connection,
        byte[] phoneHash,
        bool registered,
        Guid? userId,
        string? caller,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPhoneLookupRepository"/>
public sealed class PhoneLookupRepository : IPhoneLookupRepository
{
    public Task RecordAsync(
        NpgsqlConnection connection,
        byte[] phoneHash,
        bool registered,
        Guid? userId,
        string? caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(phoneHash);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.phone_lookups (phone_hash, registered, user_id, caller)
            VALUES (@PhoneHash, @Registered, @UserId, @Caller);
            """,
            new
            {
                PhoneHash = phoneHash,
                Registered = registered,
                // ck_phone_lookups_identity: an unregistered answer names nobody.
                UserId = registered ? userId : null,
                Caller = caller,
            },
            cancellationToken: cancellationToken));
    }
}
