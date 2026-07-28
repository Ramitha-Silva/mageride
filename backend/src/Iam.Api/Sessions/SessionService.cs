using System.Text.Json;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Caching;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Iam.Sessions;

/// <summary>An access + refresh pair and the session row behind them.</summary>
public sealed record IssuedSession(Guid SessionId, Guid UserId, string App, AccessToken Access, string RefreshToken);

/// <summary>
/// The session lifecycle D-29 and AL-08 describe: issue, rotate, revoke.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Opens a session, revoking whatever session this user had <em>for this app</em> first.
    /// A driver signing in does not end the same person's passenger session (AL-08, US-1.12).
    /// </summary>
    Task<IssuedSession> IssueAsync(
        Guid userId, Guid deviceRowId, string deviceKey, string app, IReadOnlyList<string> roles, CancellationToken cancellationToken);

    /// <summary>
    /// Spends a refresh token and issues its successor (D-29). Replaying an already-spent token
    /// revokes its whole rotation family — the contract's rule, and the only defence against a
    /// stolen refresh token being used alongside the real one.
    /// </summary>
    Task<IssuedSession> RotateAsync(string? refreshToken, CancellationToken cancellationToken);

    /// <summary>Ends a session. Already-revoked is success, not an error (US-1.7).</summary>
    Task LogoutAsync(Guid userId, string app, Guid? sessionId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISessionService"/>
public sealed class SessionService(
    IUnitOfWorkFactory unitOfWorkFactory,
    ISessionRepository sessions,
    IDeviceRepository devices,
    IUserRepository users,
    IAccessTokenIssuer accessTokens,
    RefreshTokenCodec refreshTokens,
    IConnectionMultiplexer redis,
    IOptions<TokenOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionService> logger) : ISessionService
{
    private readonly TokenOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IssuedSession> IssueAsync(
        Guid userId,
        Guid deviceRowId,
        string deviceKey,
        string app,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        ArgumentNullException.ThrowIfNull(roles);

        var now = timeProvider.GetUtcNow();
        var jti = Guid.NewGuid();

        // A sign-in starts its own rotation family, so a refresh token left over from a previous
        // sign-in cannot reach this session's lineage when it is replayed (0106).
        var session = new AuthSession(jti, userId, deviceRowId, app, FamilyId: jti, now, null, null);

        IReadOnlyList<Guid> superseded;
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            superseded = await sessions.RevokeActiveAsync(
                unitOfWork.Connection, unitOfWork.Transaction, userId, app, now, cancellationToken);

            await sessions.InsertAsync(unitOfWork.Connection, unitOfWork.Transaction, session, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
        }

        await ForgetAsync(superseded, cancellationToken);
        await RememberAsync(session, cancellationToken);

        return Build(session, deviceKey, roles);
    }

    public async Task<IssuedSession> RotateAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!refreshTokens.TryRead(refreshToken, out var presentedJti))
        {
            throw new MageRideException(MageRideErrors.Unauthorized, "The refresh token is not a valid MageRide token.");
        }

        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await sessions.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, presentedJti, cancellationToken);
        if (existing is null)
        {
            throw new MageRideException(MageRideErrors.Unauthorized, "The refresh token does not match a known session.");
        }

        if (existing.IsRevoked)
        {
            // Replay of a spent token. Either the client kept an old copy or somebody else has
            // one; we cannot tell, so the family goes (D3' /v1/auth/refresh). Family, not
            // (user, app): a token superseded by a later sign-in belongs to a dead lineage and
            // must not take the newer session with it.
            await unitOfWork.RollbackAsync(cancellationToken);
            await RevokeFamilyAsync(existing.FamilyId, now, cancellationToken);

            logger.LogWarning(
                "Refresh token for revoked session {SessionId} was replayed; revoked rotation family {FamilyId}",
                existing.Jti, existing.FamilyId);

            throw new MageRideException(MageRideErrors.Unauthorized, "The refresh token has already been used.");
        }

        if (now - existing.IssuedAt >= _options.RefreshTokenLifetime)
        {
            await sessions.RevokeAsync(unitOfWork.Connection, unitOfWork.Transaction, existing.Jti, now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            await ForgetAsync([existing.Jti], cancellationToken);

            throw new MageRideException(MageRideErrors.Unauthorized, "The refresh token has expired.");
        }

        if (!await sessions.RevokeAsync(unitOfWork.Connection, unitOfWork.Transaction, existing.Jti, now, cancellationToken))
        {
            // Another rotation of the same token won the row between the read and here.
            await unitOfWork.RollbackAsync(cancellationToken);
            await RevokeFamilyAsync(existing.FamilyId, now, cancellationToken);

            throw new MageRideException(MageRideErrors.Unauthorized, "The refresh token has already been used.");
        }

        var deviceKey = await devices.FindKeyAsync(
            unitOfWork.Connection, unitOfWork.Transaction, existing.DeviceId, cancellationToken);

        var roles = await users.RolesAsync(unitOfWork.Connection, unitOfWork.Transaction, existing.UserId, cancellationToken);
        if (roles.Count == 0)
        {
            // The account was deleted or stripped of every role while the session was alive.
            await unitOfWork.CommitAsync(cancellationToken);
            await ForgetAsync([existing.Jti], cancellationToken);
            throw new MageRideException(MageRideErrors.Unauthorized, "The account no longer holds any role.");
        }

        var rotated = existing with { Jti = Guid.NewGuid(), IssuedAt = now, LastUsedAt = null, RevokedAt = null };
        await sessions.InsertAsync(unitOfWork.Connection, unitOfWork.Transaction, rotated, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        await ForgetAsync([existing.Jti], cancellationToken);
        await RememberAsync(rotated, cancellationToken);

        return Build(rotated, deviceKey ?? string.Empty, roles);
    }

    public async Task LogoutAsync(Guid userId, string app, Guid? sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        var now = timeProvider.GetUtcNow();
        IReadOnlyList<Guid> revoked;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            if (sessionId is { } jti)
            {
                var wasActive = await sessions.RevokeAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, jti, now, cancellationToken);
                revoked = wasActive ? [jti] : [];
            }
            else
            {
                // No jti on the token (an older build): fall back to this app's active session,
                // which AL-08 guarantees is at most one.
                revoked = await sessions.RevokeActiveAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, userId, app, now, cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await ForgetAsync(revoked, cancellationToken);
    }

    private IssuedSession Build(AuthSession session, string deviceKey, IReadOnlyList<string> roles)
    {
        var access = accessTokens.Issue(new AccessTokenRequest(
            session.UserId, roles, deviceKey, session.App, session.Jti));

        return new IssuedSession(session.Jti, session.UserId, session.App, access, refreshTokens.Issue(session.Jti));
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var revoked = await sessions.RevokeFamilyAsync(
            unitOfWork.Connection, unitOfWork.Transaction, familyId, now, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
        await ForgetAsync(revoked, cancellationToken);
    }

    /// <summary>
    /// Mirrors the session into Redis <c>refresh:{jti}</c> for O(1) revocation lookups
    /// (ADD §12.1). Best effort: <c>iam.sessions</c> is the record, so a Redis outage costs a
    /// cache, not a session (D6' §8.3).
    /// </summary>
    private async Task RememberAsync(AuthSession session, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new { userId = session.UserId, app = session.App, issuedAt = session.IssuedAt },
            MageRideJson.StorageOptions);

        try
        {
            await redis.GetDatabase()
                .StringSetAsync(RedisKeys.RefreshToken(session.Jti.ToString()), payload, _options.RefreshTokenLifetime)
                .WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not mirror session {SessionId} into Redis; Postgres remains authoritative", session.Jti);
        }
    }

    private async Task ForgetAsync(IReadOnlyList<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        try
        {
            var database = redis.GetDatabase();
            var keys = sessionIds.Select(id => (RedisKey)RedisKeys.RefreshToken(id.ToString())).ToArray();
            await database.KeyDeleteAsync(keys).WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not drop {Count} revoked session mirrors from Redis", sessionIds.Count);
        }
    }
}
