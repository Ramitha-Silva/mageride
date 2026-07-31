using System.Security.Cryptography;
using MageRide.Safety.Configuration;
using MageRide.Safety.Domain;
using MageRide.Safety.Live;
using MageRide.Safety.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Sharing;

/// <summary>An issued D-34 link.</summary>
public sealed record IssuedShare(string Token, string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// The live snapshot a shared link shows — and every field it is allowed to carry.
/// </summary>
/// <remarks>
/// <b>There is no field for a track.</b> D-34 forbids historical replay, and the type is the fence:
/// nothing here can hold a sequence of positions, so no later change adds a trail without changing
/// the contract first.
/// </remarks>
public sealed record SharedTripView(
    string State,
    LivePosition? Position,
    string? VehicleRegistration,
    string? VehicleType,
    string? DriverName,
    DateTimeOffset AsOf,
    DateTimeOffset ExpiresAt);

/// <summary>D-34's share link: issue, revoke, read.</summary>
public interface ITripShareService
{
    Task<IssuedShare> IssueAsync(Guid callerId, Guid tripId, CancellationToken cancellationToken);

    Task RevokeAsync(Guid callerId, Guid tripId, CancellationToken cancellationToken);

    /// <summary>
    /// The public read. Meters the token, applies both rate limits, and answers <c>410</c> for a
    /// dead one <b>before any ride row is read</b>.
    /// </summary>
    Task<SharedTripView> ReadAsync(string token, string? clientIp, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every trip-scoped token when a trip reaches a terminal state (D-34's "trip + 1 h").
    /// </summary>
    /// <returns>How many tokens were live.</returns>
    Task<int> CloseTripAsync(Guid tripId, CancellationToken cancellationToken);

    /// <summary>
    /// A live link for an SOS SMS, best-effort.
    /// </summary>
    /// <remarks>
    /// An SOS on a ride shares that ride; an SOS with no ride has nothing to share and the message
    /// goes without a link. Never allowed to fail the alert — a missing link costs the recipient a
    /// map, and a refused SOS costs them everything.
    /// </remarks>
    Task<string?> TryMintSosLinkAsync(SosEvent sos, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITripShareService"/>
internal sealed class TripShareService(
    IShareTokenRepository tokens,
    ITripReadRepository trips,
    ILivePositionReader positions,
    IDriverDirectory drivers,
    ITokenBucketRateLimiter rateLimiter,
    IOptions<SafetyOptions> options,
    TimeProvider clock,
    ILogger<TripShareService> logger) : ITripShareService
{
    /// <summary>D-34: 60 requests a minute per token. Capacity equals the rate, so there is no burst credit.</summary>
    internal static TokenBucketPolicy PerToken(int perMinute) =>
        new("trip-share-token", capacity: perMinute, refillTokens: perMinute, refillPeriod: TimeSpan.FromMinutes(1));

    /// <summary>The per-IP companion D3' asks for and gives no number.</summary>
    internal static TokenBucketPolicy PerIp(int perMinute) =>
        new("trip-share-ip", capacity: perMinute, refillTokens: perMinute, refillPeriod: TimeSpan.FromMinutes(1));

    private readonly SafetyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IssuedShare> IssueAsync(Guid callerId, Guid tripId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ShareBaseUrl))
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "Safety:ShareBaseUrl is not configured, so a share link cannot be built.");
        }

        var trip = await trips.FindAsync(tripId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No trip {tripId}.");

        if (!await trips.IsParticipantAsync(tripId, callerId, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "Only a party to the trip may share it.");
        }

        if (trip.Terminal)
        {
            // A terminal trip has nothing live to show, and D-34's public view is live-only — a link
            // to it would open on a page that can never update.
            throw new MageRideException(
                MageRideErrors.RideTerminal, "This trip has ended; there is nothing live left to share.");
        }

        var now = clock.GetUtcNow();

        // Re-issuing replays. Two live links for one trip would mean the passenger revoking "the"
        // link and leaving another one open — which is the failure D-34's revocability exists to
        // prevent.
        if (await tokens.FindLiveForTripAsync(tripId, ShareTokenScopes.TripView, now, cancellationToken) is { } live)
        {
            return new IssuedShare(live.Token, _options.ShareBaseUrl + Uri.EscapeDataString(live.Token), live.ExpiresAt);
        }

        // The trip has not ended, so "trip end + 1 h" has no end yet. The ceiling is what stops a
        // ride that never reached a terminal state leaving a link open for ever; CloseTripAsync is
        // what normally ends it.
        var expiresAt = now + _options.ShareMaxLifetime;

        var issued = await tokens.IssueAsync(
            NewToken(_options.ShareTokenBytes), tripId, ShareTokenScopes.TripView, expiresAt, cancellationToken);

        logger.LogInformation("Trip {TripId} shared by {CallerId} until {ExpiresAt:O}.", tripId, callerId, expiresAt);

        return new IssuedShare(
            issued.Token, _options.ShareBaseUrl + Uri.EscapeDataString(issued.Token), issued.ExpiresAt);
    }

    public async Task RevokeAsync(Guid callerId, Guid tripId, CancellationToken cancellationToken)
    {
        if (await trips.FindAsync(tripId, cancellationToken) is null)
        {
            throw new MageRideException(MageRideErrors.NotFound, $"No trip {tripId}.");
        }

        if (!await trips.IsParticipantAsync(tripId, callerId, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "Only a party to the trip may revoke its link.");
        }

        // Only the scope this service issues. The AL-44 scopes are notification-svc's and are the
        // recipient's only way in — revoking a package recipient's link because the *sender* tapped
        // "stop sharing my trip" would strand somebody waiting for a parcel.
        var revoked = await tokens.RevokeForTripAsync(
            tripId, [ShareTokenScopes.TripView], clock.GetUtcNow(), cancellationToken);

        logger.LogInformation("Trip {TripId}: {Count} share link(s) revoked by {CallerId}.", tripId, revoked, callerId);
    }

    public async Task<SharedTripView> ReadAsync(
        string token, string? clientIp, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Both limits before the lookup. A token nobody has ever issued costs a Redis round trip and
        // no database work, which is what makes enumeration of the key space uninteresting.
        await RequireWithinLimitsAsync(token, clientIp, cancellationToken);

        var share = await tokens.FindAsync(token, cancellationToken)
                    ?? throw new MageRideException(MageRideErrors.TokenUnknown, "No such share link.");

        var now = clock.GetUtcNow();

        // **Metered before the gate, not after.** The forensic value of `access_count` is in the
        // hits on a token that has been revoked — somebody still holding a dead link is exactly the
        // pattern AL-44's metering exists to surface.
        await tokens.MeterAsync(token, now, cancellationToken);

        if (share.RevokedAt is not null || share.ExpiresAt <= now)
        {
            // 410 before anything about the trip is read. There is no code path on which a dead
            // token could carry a position: the ride row is not fetched at all.
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This link has expired or was revoked.");
        }

        if (!string.Equals(share.Scope, ShareTokenScopes.TripView, StringComparison.Ordinal))
        {
            // The richer package/proxy/pickup-confirm scopes are public-bff's `/public/track/{token}`
            // family (AL-44); serving them here would answer with the wrong shape and skip the
            // scope-specific redaction that family does.
            throw new MageRideException(
                MageRideErrors.TokenUnknown, "This link belongs to another surface.");
        }

        var trip = share.TripId is { } tripId
            ? await trips.FindAsync(tripId, cancellationToken)
            : null;

        if (trip is null)
        {
            throw new MageRideException(MageRideErrors.TokenExpiredOrRevoked, "The shared trip is no longer available.");
        }

        var position = trip.VehicleId is { } vehicleId
            ? await positions.ReadAsync(vehicleId, cancellationToken)
            : null;

        // Stale is not shown. The person watching is not in the vehicle and has no other way to tell
        // that the marker stopped moving twenty minutes ago.
        if (position is not null && now - position.SampledAt > _options.PositionMaxAge)
        {
            position = null;
        }

        var driver = trip.DriverId is { } driverId
            ? await drivers.FindAsync(driverId, cancellationToken)
            : null;

        return new SharedTripView(
            trip.State,
            position,
            driver?.RegistrationNumber,
            driver?.VehicleType,
            driver?.Name,
            now,
            share.ExpiresAt);
    }

    public Task<int> CloseTripAsync(Guid tripId, CancellationToken cancellationToken) =>
        // Every trip-scoped token, not just this service's: D-34's window ends at trip end + 1 h and
        // so does AL-44's package link. `pickup_confirm` is deliberately absent — it names a
        // location request rather than a trip, and its own 300 s TTL is what closes it.
        tokens.RevokeForTripAsync(
            tripId, ShareTokenScopes.TripScoped, clock.GetUtcNow() + _options.ShareGrace, cancellationToken);

    public async Task<string?> TryMintSosLinkAsync(SosEvent sos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sos);

        if (sos.RideId is not { } rideId || string.IsNullOrWhiteSpace(_options.ShareBaseUrl))
        {
            return null;
        }

        try
        {
            var now = clock.GetUtcNow();

            var live = await tokens.FindLiveForTripAsync(rideId, ShareTokenScopes.TripView, now, cancellationToken)
                       ?? await tokens.IssueAsync(
                           NewToken(_options.ShareTokenBytes),
                           rideId,
                           ShareTokenScopes.TripView,
                           now + _options.ShareMaxLifetime,
                           cancellationToken);

            return _options.ShareBaseUrl + Uri.EscapeDataString(live.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never allowed to fail the alert.
            logger.LogError(exception, "Could not mint a tracking link for SOS {SosId}; the SMS goes without one.", sos.Id);
            return null;
        }
    }

    private async Task RequireWithinLimitsAsync(string token, string? clientIp, CancellationToken cancellationToken)
    {
        var perToken = await rateLimiter.TryAcquireAsync(
            PerToken(_options.PublicViewPerMinute), token, cancellationToken: cancellationToken);

        if (!perToken.Allowed)
        {
            throw new MageRideException(
                MageRideErrors.RateLimited, $"A share link may be read {_options.PublicViewPerMinute} times a minute (D-34).")
                .WithExtension("retryAfterSeconds", (int)Math.Ceiling(perToken.RetryAfter.TotalSeconds));
        }

        if (string.IsNullOrWhiteSpace(clientIp))
        {
            return;
        }

        var perIp = await rateLimiter.TryAcquireAsync(
            PerIp(_options.PublicViewPerMinutePerIp), clientIp, cancellationToken: cancellationToken);

        if (!perIp.Allowed)
        {
            throw new MageRideException(
                MageRideErrors.RateLimited, "Too many share-link reads from this address.")
                .WithExtension("retryAfterSeconds", (int)Math.Ceiling(perIp.RetryAfter.TotalSeconds));
        }
    }

    /// <summary>
    /// Base64url over cryptographic bytes, unpadded so it is URL-safe without escaping.
    /// </summary>
    internal static string NewToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
