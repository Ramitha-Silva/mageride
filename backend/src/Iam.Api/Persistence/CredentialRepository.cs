using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// <c>iam.user_credentials</c> and <c>iam.federated_identities</c> — the portal half of AL-07 and
/// the AL-37 lock-out (0107).
/// </summary>
public interface ICredentialRepository
{
    Task<UserCredential?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>Sets or replaces a password verifier, clearing any lock-out with it.</summary>
    Task UpsertPasswordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string passwordHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a wrong password and locks the account once
    /// <paramref name="maxFailedAttempts"/> consecutive failures have been reached (AL-37).
    /// </summary>
    /// <returns>The lock-out instant if this failure caused one, otherwise <see langword="null"/>.</returns>
    Task<DateTimeOffset?> RecordFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Clears the failure counter and stamps the sign-in.</summary>
    Task RecordSuccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<FederatedIdentity?> FindFederatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string provider,
        string subject,
        CancellationToken cancellationToken);

    /// <summary>
    /// Binds a provider identity to an account, or refreshes the binding that already exists.
    /// </summary>
    Task LinkFederatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string provider,
        string subject,
        string? email,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICredentialRepository"/>
public sealed class CredentialRepository : ICredentialRepository
{
    private const string CredentialColumns =
        "user_id, password_hash, password_updated_at, failed_attempts, locked_until, last_login_at";

    private const string FederatedColumns =
        "id, user_id, provider, subject, email, linked_at, last_login_at";

    public Task<UserCredential?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<UserCredential>(new CommandDefinition(
            $"SELECT {CredentialColumns} FROM iam.user_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task UpsertPasswordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string passwordHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // A password change clears the lock-out: whoever set it proved control of the account by
        // a stronger means than the counter was defending.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.user_credentials (user_id, password_hash, password_updated_at)
            VALUES (@UserId, @PasswordHash, @Now)
            ON CONFLICT (user_id) DO UPDATE
               SET password_hash = EXCLUDED.password_hash,
                   password_updated_at = EXCLUDED.password_updated_at,
                   failed_attempts = 0,
                   locked_until = NULL;
            """,
            new { UserId = userId, PasswordHash = passwordHash, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<DateTimeOffset?> RecordFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Counter and lock move in one statement, so two simultaneous guesses cannot both read
        // "four failures" and each decide the account is still open.
        return await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            """
            UPDATE iam.user_credentials
               SET failed_attempts = failed_attempts + 1,
                   locked_until = CASE
                     WHEN failed_attempts + 1 >= @MaxFailedAttempts THEN @LockedUntil
                     ELSE locked_until
                   END
             WHERE user_id = @UserId
            RETURNING locked_until;
            """,
            new
            {
                UserId = userId,
                MaxFailedAttempts = maxFailedAttempts,
                LockedUntil = now + lockoutDuration,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task RecordSuccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.user_credentials
               SET failed_attempts = 0, locked_until = NULL, last_login_at = @Now
             WHERE user_id = @UserId;
            """,
            new { UserId = userId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<FederatedIdentity?> FindFederatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<FederatedIdentity>(new CommandDefinition(
            $"""
             SELECT {FederatedColumns}
               FROM iam.federated_identities
              WHERE provider = @Provider AND subject = @Subject;
             """,
            new { Provider = provider, Subject = subject },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task LinkFederatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string provider,
        string subject,
        string? email,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ux_federated_user_provider is the conflict target on the re-link path: one account has
        // at most one identity per provider, so signing in again with a *different* Google
        // account under the same MageRide user moves the binding rather than adding a second.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.federated_identities (user_id, provider, subject, email, last_login_at)
            VALUES (@UserId, @Provider, @Subject, @Email, @Now)
            ON CONFLICT (user_id, provider) DO UPDATE
               SET subject = EXCLUDED.subject,
                   email = EXCLUDED.email,
                   last_login_at = EXCLUDED.last_login_at;
            """,
            new { UserId = userId, Provider = provider, Subject = subject, Email = email, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }
}
