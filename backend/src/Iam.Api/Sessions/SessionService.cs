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
    /// Opens a session, revoking whatever session this user had <em>for this surface</em> first.
    /// A driver signing in does not end the same person's passenger session, and neither ends
    /// their Fleet Portal session (AL-08, US-1.12, migration 0107).
    /// </summary>
    Task<IssuedSession> IssueAsync(
        SessionPrincipal principal, Guid deviceRowId, string deviceKey, string app, CancellationToken cancellationToken);

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

    /// <summary>
    /// How far past an access token's own expiry a revocation tombstone is kept (Δ MCS-30).
    /// </summary>
    /// <remarks>
    /// The JWT validator allows a little clock skew, so a token can still be accepted slightly
    /// after its stated expiry. A tombstone that expired exactly on time would leave that sliver
    /// unguarded, which is the one window this whole mechanism exists to close.
    /// </remarks>
    private static readonly TimeSpan TokenSkew = TimeSpan.FromMinutes(5);

    public async Task<IssuedSession> IssueAsync(
        SessionPrincipal principal,
        Guid deviceRowId,
        string deviceKey,
        string app,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        var now = timeProvider.GetUtcNow();
        var jti = Guid.NewGuid();

        // A sign-in starts its own rotation family, so a refresh token left over from a previous
        // sign-in cannot reach this session's lineage when it is replayed (0106).
        var session = new AuthSession(jti, principal.UserId, deviceRowId, app, FamilyId: jti, now, null, null);

        IReadOnlyList<Guid> superseded;
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            superseded = await sessions.RevokeActiveAsync(
                unitOfWork.Connection, unitOfWork.Transaction, principal.UserId, app, now, cancellationToken);

            await sessions.InsertAsync(unitOfWork.Connection, unitOfWork.Transaction, session, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
        }

        await ForgetAsync(superseded, cancellationToken);
        await DisplaceAsync(superseded, cancellationToken);
        await RememberAsync(session, cancellationToken);

        return Build(session, deviceKey, principal);
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

        // Re-read rather than carry forward: a role granted (C029's driver grant) or revoked
        // since the last rotation has to reach the token within one refresh, not at next sign-in.
        var principal = await users.PrincipalAsync(
            unitOfWork.Connection, unitOfWork.Transaction, existing.UserId, cancellationToken);
        if (principal.Roles.Count == 0)
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

        return Build(rotated, deviceKey ?? string.Empty, principal);
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

    private IssuedSession Build(AuthSession session, string deviceKey, SessionPrincipal principal)
    {
        var access = accessTokens.Issue(new AccessTokenRequest(
            session.UserId,
            principal.Roles,
            deviceKey,
            session.App,
            session.Jti,
            principal.Fleet?.FleetRole,
            principal.Fleet?.FleetId));

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

    /// <summary>
    /// Marks sessions as DISPLACED — ended because somebody signed in elsewhere (AL-08, Δ MCS-30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only displacement, and keeping it out of <see cref="ForgetAsync"/> is the point.</b>
    /// Every revocation drops the refresh mirror; only this one means "another device took your
    /// session". Tombstoning every revocation made a second logout answer <c>403 device-revoked</c>
    /// where the contract promises an idempotent <c>204</c>, and would have told somebody who had
    /// just tapped Log out that they were signed in on another device — then wiped their local
    /// database with that as the reason.
    /// </para>
    /// <para>
    /// Dropping the mirror only stops a device REFRESHING. Its access token stays valid until it
    /// expires, because nothing outside this service reads session state — so without this a phone
    /// the account had just been signed out of could go on accepting rides for up to
    /// <c>AccessTokenLifetime</c>. That window is worth closing for a device that was replaced and
    /// not for one whose owner chose to leave.
    /// </para>
    /// <para>
    /// The TTL is that lifetime plus the clock skew the validator already allows: a tombstone must
    /// outlive every token it exists to kill, and once the last one has expired on its own there is
    /// nothing left for it to do.
    /// </para>
    /// </remarks>
    private async Task DisplaceAsync(IReadOnlyList<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        try
        {
            var database = redis.GetDatabase();
            var lifetime = _options.AccessTokenLifetime + TokenSkew;

            await Task.WhenAll(sessionIds.Select(id =>
                database.StringSetAsync(RedisKeys.RevokedSession(id.ToString()), "1", lifetime)))
                .WaitAsync(cancellationToken);
        }
        catch (RedisException ex)
        {
            // Best effort, and the consequence is worth naming: without the tombstone the displaced
            // device keeps its access token until it expires, which is exactly the behaviour this
            // replaced. Refusing the sign-in instead would let a Redis outage stop drivers signing
            // in at all, which is a great deal worse than a bounded overlap.
            logger.LogWarning(
                ex, "Could not tombstone {Count} displaced sessions; they expire with their access tokens",
                sessionIds.Count);
        }
    }
}
