using Dapper;
using MageRide.Safety.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Safety.Persistence;

/// <summary>One row of <c>safety.trip_share_tokens</c> (migration 0901).</summary>
public sealed record ShareToken(
    string Token,
    Guid? TripId,
    string Scope,
    Guid? LocationRequestId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastAccessAt,
    int AccessCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>safety.trip_share_tokens</c> — D-34's link, and AL-44's three other scopes.
/// </summary>
/// <remarks>
/// <b>This service issues <c>trip_view</c> and revokes every scope.</b> The other three are minted
/// by notification-svc and SMSed (AL-44/AL-45: never returned to a client), and they die when the
/// trip does — which is a fact about the trip rather than about who minted the token, so revocation
/// belongs to whoever is told the trip ended.
/// </remarks>
public interface IShareTokenRepository
{
    /// <summary>
    /// The live token for this trip in this scope, if there is one.
    /// </summary>
    /// <remarks>
    /// Re-issuing replays rather than minting a second: two live links for one trip would mean the
    /// passenger revoking "the" link and leaving another one open. <c>ix_trip_share_tokens_trip_scope</c>
    /// (0905) is this query.
    /// </remarks>
    Task<ShareToken?> FindLiveForTripAsync(
        Guid tripId, string scope, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ShareToken> IssueAsync(
        string token, Guid tripId, string scope, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>The token itself is the primary key — every lookup is by the value in the URL.</summary>
    Task<ShareToken?> FindAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Records one redemption (AL-44's metering).
    /// </summary>
    /// <remarks>
    /// A single <c>UPDATE … SET access_count = access_count + 1</c> rather than read-modify-write:
    /// a shared link is unauthenticated, so the count is the only forensic trail there is, and two
    /// concurrent readers must not lose one between them.
    /// </remarks>
    Task MeterAsync(string token, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Revokes one trip's tokens in the given scopes. Returns how many were live.</summary>
    Task<int> RevokeForTripAsync(
        Guid tripId, IReadOnlyCollection<string> scopes, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IShareTokenRepository"/>
internal sealed class ShareTokenRepository(INpgsqlConnectionFactory connections) : IShareTokenRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        "token, trip_id, scope, location_request_id, expires_at, revoked_at, last_access_at, access_count, created_at";

    public async Task<ShareToken?> FindLiveForTripAsync(
        Guid tripId, string scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ShareToken>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM safety.trip_share_tokens
                  WHERE trip_id = @TripId
                    AND scope = @Scope
                    AND revoked_at IS NULL
                    AND expires_at > @Now
                  ORDER BY created_at DESC
                  LIMIT 1;
                 """,
                new { TripId = tripId, Scope = scope, Now = now },
                cancellationToken: cancellationToken));
    }

    public async Task<ShareToken> IssueAsync(
        string token, Guid tripId, string scope, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<ShareToken>(
            new CommandDefinition(
                $"""
                 INSERT INTO safety.trip_share_tokens (token, trip_id, scope, expires_at)
                 VALUES (@Token, @TripId, @Scope, @ExpiresAt)
                 RETURNING {Columns};
                 """,
                new { Token = token, TripId = tripId, Scope = scope, ExpiresAt = expiresAt },
                cancellationToken: cancellationToken));
    }

    public async Task<ShareToken?> FindAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ShareToken>(
            new CommandDefinition(
                $"SELECT {Columns} FROM safety.trip_share_tokens WHERE token = @Token;",
                new { Token = token },
                cancellationToken: cancellationToken));
    }

    public async Task MeterAsync(string token, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE safety.trip_share_tokens
                   SET access_count = access_count + 1, last_access_at = @At
                 WHERE token = @Token;
                """,
                new { Token = token, At = at },
                cancellationToken: cancellationToken));
    }

    public async Task<int> RevokeForTripAsync(
        Guid tripId, IReadOnlyCollection<string> scopes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Guarded on `revoked_at IS NULL`, so a second revocation is a no-op rather than a rewrite
        // of when the first one happened — the timestamp is evidence.
        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE safety.trip_share_tokens
                   SET revoked_at = @At
                 WHERE trip_id = @TripId
                   AND scope = ANY(@Scopes)
                   AND revoked_at IS NULL;
                """,
                new { TripId = tripId, Scopes = scopes.ToArray(), At = at },
                cancellationToken: cancellationToken));
    }
}

/// <summary>What a shared trip looks like from outside — the D-34 public view's source rows.</summary>
/// <param name="Terminal">
/// The trip has reached a terminal state. What sets the share window's end: D-34 is "trip + 1 h",
/// and a token issued while the trip was running is closed at the terminal by the trip-end hook.
/// </param>
public sealed record SharedTrip(
    Guid TripId,
    string State,
    bool Terminal,
    Guid? VehicleId,
    Guid? DriverId,
    DateTimeOffset? TerminalAt);

/// <summary>
/// Reads the trip a share token points at, across both planes.
/// </summary>
/// <remarks>
/// <b>Polymorphic on purpose.</b> <c>safety.trip_share_tokens.trip_id</c> is deliberately
/// unconstrained (0901: "the referent is polymorphic, exactly as both DDL sources print it") because
/// a Mode C journey is a <c>rides.rides</c> row and a Mode A/B one is a <c>trips.sessions</c> row.
/// The read tries the ride first — Mode C is the only plane that shares a link today — and falls
/// through, so a Mode B share needs no second endpoint.
/// <para>
/// Read-only, and the same cross-context read query-svc and fare-svc make of the same tables.
/// </para>
/// </remarks>
public interface ITripReadRepository
{
    Task<SharedTrip?> FindAsync(Guid tripId, CancellationToken cancellationToken);

    /// <summary>Whether this user may share this trip — the passenger, the booker or the rider.</summary>
    Task<bool> IsParticipantAsync(Guid tripId, Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITripReadRepository"/>
internal sealed class TripReadRepository(INpgsqlConnectionFactory connections) : ITripReadRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>
    /// The eight terminal states of D5' §6 / ADD Appendix B.2.
    /// </summary>
    /// <remarks>
    /// <c>Completed</c> is deliberately absent — C004's note (b) and ride-svc's own rule: the ride
    /// moves through it to <c>PaymentPending</c> in one transaction and never rests there. Treating
    /// it as terminal would close a share link while the passenger is still in the car.
    /// </remarks>
    private static readonly string[] TerminalStates =
    [
        "Paid", "CashSettled", "CashOnDeliveryCollected", "Disputed",
        "CancelledByRiderBeforeAccept", "CancelledByRiderAfterAccept", "CancelledByDriver",
        "ExpiredNoDriver", "NoShowRider", "NoShowDriver",
    ];

    public async Task<SharedTrip?> FindAsync(Guid tripId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var ride = await connection.QuerySingleOrDefaultAsync<SharedTrip>(
            new CommandDefinition(
                """
                SELECT id AS trip_id,
                       state,
                       (state = ANY(@Terminal)) AS terminal,
                       accepted_vehicle_id AS vehicle_id,
                       accepted_driver_id AS driver_id,
                       terminal_at
                  FROM rides.rides
                 WHERE id = @TripId;
                """,
                new { TripId = tripId, Terminal = TerminalStates },
                cancellationToken: cancellationToken));

        if (ride is not null)
        {
            return ride;
        }

        return await connection.QuerySingleOrDefaultAsync<SharedTrip>(
            new CommandDefinition(
                """
                SELECT id AS trip_id,
                       state,
                       (state = 'COMPLETED') AS terminal,
                       vehicle_id,
                       driver_id,
                       ended_at AS terminal_at
                  FROM trips.sessions
                 WHERE id = @TripId;
                """,
                new { TripId = tripId },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> IsParticipantAsync(Guid tripId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Mode C: the booker arranged it, the rider is in it, the passenger account pays for it —
        // all three may share the link (P-01/P-05). The *driver* may not: a share link is the
        // passenger's to give away, and D-34 frames it as "share my trip".
        //
        // Mode A/B: the driver is the only party a session names, so it is theirs to share.
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                  SELECT 1 FROM rides.rides
                   WHERE id = @TripId
                     AND (passenger_id = @UserId OR booker_id = @UserId OR rider_id = @UserId))
                    OR EXISTS (
                  SELECT 1 FROM trips.sessions
                   WHERE id = @TripId AND driver_id = @UserId);
                """,
                new { TripId = tripId, UserId = userId },
                cancellationToken: cancellationToken));
    }
}
