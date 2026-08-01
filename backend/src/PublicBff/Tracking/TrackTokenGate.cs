using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Domain;
using MageRide.PublicBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Tracking;

/// <summary>
/// The one door onto every <c>/public/track/**</c> route: both rate limits, the metering and the
/// uniform 404/410.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every route goes through it, and that is what makes the error family uniform.</b> D3' Δ
/// 2026-07-05 says the six operations answer <c>404 token-unknown</c>, <c>410
/// token-expired-or-revoked</c> and <c>429 rate-limited</c> identically; six handlers each deciding
/// that for themselves is six chances for one of them to leak the difference between "no such
/// token" and "a token for somebody else's ride".
/// </para>
/// <para>
/// <b>Both limits are applied before the token is looked up.</b> A token nobody ever issued costs a
/// Redis round trip and no database work, which is what makes enumerating a 256-bit key space
/// uninteresting. The per-IP bucket exists because a per-token limit is no limit at all against
/// somebody who has harvested a hundred links.
/// </para>
/// <para>
/// <b>The token is metered before the gate, not after.</b> The forensic value of
/// <c>access_count</c> is precisely in the hits on a token that has already been revoked — somebody
/// still holding a dead link is the pattern AL-44's metering exists to surface.
/// </para>
/// <para>
/// <b>The 410 is produced before any ride row is read.</b> There is no code path on which a dead
/// token could carry a position, a plate or a name, because nothing about the trip has been fetched
/// by the time the refusal is thrown.
/// </para>
/// </remarks>
public interface ITrackTokenGate
{
    /// <summary>
    /// Applies both limits, meters the token and returns it if it is live and belongs to this
    /// surface. Throws the family's 404/410/429 otherwise.
    /// </summary>
    Task<ShareToken> RedeemAsync(string? token, string? clientIp, CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads a token without metering it or spending a bucket token.
    /// </summary>
    /// <remarks>
    /// The live stream's re-check. A connection held for minutes has to notice a revocation, and
    /// counting each of those re-reads as an access would make <c>access_count</c> a measure of how
    /// long a tab was left open rather than of how often a link was opened.
    /// </remarks>
    Task<ShareToken?> PeekAsync(string token, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackTokenGate"/>
internal sealed class TrackTokenGate(
    IShareTokenRepository tokens,
    ITokenBucketRateLimiter rateLimiter,
    IOptions<PublicBffOptions> options,
    TimeProvider clock,
    ILogger<TrackTokenGate> logger) : ITrackTokenGate
{
    /// <summary>Capacity equals the rate, so an idle link carries no burst credit.</summary>
    internal static TokenBucketPolicy PerToken(int perMinute) =>
        new("public-track-token", capacity: perMinute, refillTokens: perMinute, refillPeriod: TimeSpan.FromMinutes(1));

    /// <inheritdoc cref="PerToken"/>
    internal static TokenBucketPolicy PerIp(int perMinute) =>
        new("public-track-ip", capacity: perMinute, refillTokens: perMinute, refillPeriod: TimeSpan.FromMinutes(1));

    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ShareToken> RedeemAsync(string? token, string? clientIp, CancellationToken cancellationToken)
    {
        // A token shorter than anything ever minted is refused with the same 404 an unknown one
        // gets, and without a Redis or a Postgres round trip. `public-bff.yaml` bounds the path
        // parameter at 16..128 characters; notification-svc and safety-svc both mint 32 bytes of
        // base64url, which is 43.
        if (token is not { Length: >= 16 and <= 128 })
        {
            throw new MageRideException(MageRideErrors.TokenUnknown, "No such tracking link.");
        }

        await RequireWithinLimitsAsync(token, clientIp, cancellationToken);

        var share = await tokens.FindAsync(token, cancellationToken)
                    ?? throw new MageRideException(MageRideErrors.TokenUnknown, "No such tracking link.");

        var now = clock.GetUtcNow();

        await tokens.MeterAsync(token, now, cancellationToken);

        if (!share.IsLiveAt(now))
        {
            logger.LogInformation(
                "A dead {Scope} token was presented ({AccessCount} accesses, expired {ExpiresAt:O}).",
                share.Scope, share.AccessCount + 1, share.ExpiresAt);

            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This link has expired or was closed.");
        }

        if (!ShareTokenScopes.IsPublicSurface(share.Scope))
        {
            // D-34's `trip_view` is safety-svc's `GET /v1/trip-share/public/{token}`, whose response
            // is a different shape with a different redaction. Answering it here would serve a
            // passenger's own share link through a contract written for a package recipient.
            // The message is the unknown-token one on purpose: telling a caller that a token exists
            // but belongs elsewhere is an oracle over which links are live.
            throw new MageRideException(MageRideErrors.TokenUnknown, "No such tracking link.");
        }

        return share;
    }

    public Task<ShareToken?> PeekAsync(string token, CancellationToken cancellationToken) =>
        tokens.FindAsync(token, cancellationToken);

    private async Task RequireWithinLimitsAsync(string token, string? clientIp, CancellationToken cancellationToken)
    {
        var perToken = await rateLimiter.TryAcquireAsync(
            PerToken(_options.PerTokenPerMinute), token, cancellationToken: cancellationToken);

        if (!perToken.Allowed)
        {
            throw Limited(
                $"A tracking link may be read {_options.PerTokenPerMinute} times a minute.", perToken.RetryAfter);
        }

        if (string.IsNullOrWhiteSpace(clientIp))
        {
            return;
        }

        var perIp = await rateLimiter.TryAcquireAsync(
            PerIp(_options.PerIpPerMinute), clientIp, cancellationToken: cancellationToken);

        if (!perIp.Allowed)
        {
            throw Limited("Too many tracking reads from this address.", perIp.RetryAfter);
        }
    }

    private static MageRideException Limited(string detail, TimeSpan retryAfter) =>
        new MageRideException(MageRideErrors.RateLimited, detail)
            .WithExtension("retryAfterSeconds", (int)Math.Ceiling(retryAfter.TotalSeconds));
}
