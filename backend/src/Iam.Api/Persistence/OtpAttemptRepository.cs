using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// <c>iam.otp_attempts</c> — the durable record behind the Redis rate-limit bucket (D-32).
/// </summary>
/// <remarks>
/// The bucket decides whether a code may be *sent*; these rows decide whether a presented code is
/// the right one, still fresh and still within its entry budget. Redis holds no part of that: a
/// flush must not let a spent OTP become usable again.
/// </remarks>
public interface IOtpAttemptRepository
{
    Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, OtpAttempt attempt, CancellationToken cancellationToken);

    Task<OtpAttempt?> FindByAuthIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken);

    /// <summary>Replaces the code on an existing attempt — the resend path (D-32).</summary>
    Task<bool> ReplaceCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid authId,
        byte[] otpHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>Records a wrong entry and returns the new count.</summary>
    Task<short> RecordFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the attempt used. Conditional on it not already being used, so two verifies racing
    /// on one <c>authId</c> produce one session rather than two.
    /// </summary>
    Task<bool> MarkVerifiedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOtpAttemptRepository"/>
public sealed class OtpAttemptRepository : IOtpAttemptRepository
{
    private const string Columns =
        "id, phone, auth_id, otp_hash, attempts, expires_at, verified, device_id, app, created_at";

    public Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, OtpAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(attempt);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.otp_attempts
              (id, phone, auth_id, otp_hash, attempts, expires_at, verified, device_id, app, created_at)
            VALUES
              (@Id, @Phone, @AuthId, @OtpHash, @Attempts, @ExpiresAt, @Verified, @DeviceId, @App, @CreatedAt);
            """,
            new
            {
                attempt.Id,
                attempt.Phone,
                attempt.AuthId,
                attempt.OtpHash,
                attempt.Attempts,
                attempt.ExpiresAt,
                attempt.Verified,
                attempt.DeviceId,
                attempt.App,
                attempt.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<OtpAttempt?> FindByAuthIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<OtpAttempt>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.otp_attempts WHERE auth_id = @AuthId;",
            new { AuthId = authId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> ReplaceCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid authId,
        byte[] otpHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Entry attempts are deliberately NOT reset: resending is a send-budget action, and
        // zeroing the counter would turn the 423 lock-out into a formality.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.otp_attempts
               SET otp_hash = @OtpHash, expires_at = @ExpiresAt
             WHERE auth_id = @AuthId AND verified = false;
            """,
            new { AuthId = authId, OtpHash = otpHash, ExpiresAt = expiresAt },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public Task<short> RecordFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteScalarAsync<short>(new CommandDefinition(
            """
            UPDATE iam.otp_attempts
               SET attempts = attempts + 1
             WHERE auth_id = @AuthId
            RETURNING attempts;
            """,
            new { AuthId = authId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> MarkVerifiedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid authId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.otp_attempts
               SET verified = true
             WHERE auth_id = @AuthId AND verified = false;
            """,
            new { AuthId = authId },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }
}
