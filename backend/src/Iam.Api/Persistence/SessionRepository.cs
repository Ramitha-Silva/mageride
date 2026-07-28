using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary><c>iam.sessions</c> — the refresh-token record and the AL-08 invariant (D-29).</summary>
public interface ISessionRepository
{
    Task<AuthSession?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid jti, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every unrevoked session for <paramref name="app"/> and returns their ids, so the
    /// caller can drop the matching Redis mirrors. Only that app's sessions — a driver signing in
    /// must not end the same person's passenger session (AL-08, US-1.12).
    /// </summary>
    Task<IReadOnlyList<Guid>> RevokeActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string app,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every unrevoked session in one rotation lineage — what "revoke the session family"
    /// means when a spent refresh token is replayed (D3' <c>/v1/auth/refresh</c>). Scoped to the
    /// family rather than to <c>(user, app)</c> so a token left over from an older sign-in cannot
    /// end the session a newer sign-in just opened (0106).
    /// </summary>
    Task<IReadOnlyList<Guid>> RevokeFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes one session by id. Returns <see langword="false"/> when it was already revoked,
    /// which is how two concurrent rotations of the same refresh token are settled — exactly one
    /// call gets <see langword="true"/>.
    /// </summary>
    Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid jti,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AuthSession session,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISessionRepository"/>
public sealed class SessionRepository : ISessionRepository
{
    private const string Columns = "jti, user_id, device_id, app, family_id, issued_at, last_used_at, revoked_at";

    public Task<AuthSession?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid jti, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<AuthSession>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.sessions WHERE jti = @Jti;",
            new { Jti = jti },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> RevokeActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string app,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var revoked = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE iam.sessions
               SET revoked_at = @RevokedAt
             WHERE user_id = @UserId AND app = @App AND revoked_at IS NULL
            RETURNING jti;
            """,
            new { UserId = userId, App = app, RevokedAt = revokedAt },
            transaction,
            cancellationToken: cancellationToken));

        return [.. revoked];
    }

    public async Task<IReadOnlyList<Guid>> RevokeFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var revoked = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE iam.sessions
               SET revoked_at = @RevokedAt, last_used_at = @RevokedAt
             WHERE family_id = @FamilyId AND revoked_at IS NULL
            RETURNING jti;
            """,
            new { FamilyId = familyId, RevokedAt = revokedAt },
            transaction,
            cancellationToken: cancellationToken));

        return [.. revoked];
    }

    public async Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid jti,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // last_used_at moves with the revoke: revocation is the last thing that happens to a
        // session, whether it was rotated out, replaced by a new sign-in or logged out.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.sessions
               SET revoked_at = @RevokedAt, last_used_at = @RevokedAt
             WHERE jti = @Jti AND revoked_at IS NULL;
            """,
            new { Jti = jti, RevokedAt = revokedAt },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);

        // ux_sessions_active_app makes "one unrevoked session per (user, app)" an index-enforced
        // invariant, so this INSERT is what would fail if a caller forgot to revoke first.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO iam.sessions (jti, user_id, device_id, app, family_id, issued_at, last_used_at)
            VALUES (@Jti, @UserId, @DeviceId, @App, @FamilyId, @IssuedAt, @LastUsedAt);
            """,
            new
            {
                session.Jti,
                session.UserId,
                session.DeviceId,
                session.App,
                session.FamilyId,
                session.IssuedAt,
                session.LastUsedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
