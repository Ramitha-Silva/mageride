using Dapper;
using MageRide.Ride.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>A row of <c>rides.location_requests</c> (migration 0606).</summary>
/// <param name="Id">Surrogate key. Never leaves the service — <paramref name="RequestId"/> is the handle.</param>
/// <param name="RequestId">
/// The client-facing id: <c>POST /v1/location-requests</c>'s <c>requestId</c>, the path parameter on
/// confirm/decline, the WebSocket group suffix <c>booker:{bookerId}:loc-req:{requestId}</c> (P-13)
/// and the subject of the <c>safety.location_request_audit</c> row (P-12).
/// </param>
/// <param name="RiderPhoneHash">
/// The subject, keyed-hashed (P-03). The raw number is never stored — for a registered rider
/// <paramref name="RiderId"/> is the handle, and for an unregistered one AL-45's SMS is dispatched
/// from the outbox payload rather than from a column.
/// </param>
/// <param name="ResolvedGeo">
/// Present only in <see cref="LocationRequestStates.Confirmed"/>. A decline writes no coordinates
/// and there is no code path that could (P-02).
/// </param>
public sealed record LocationRequestRow(
    Guid Id,
    Guid? RideId,
    Guid RequestId,
    Guid BookerId,
    Guid? RiderId,
    byte[]? RiderPhoneHash,
    string State,
    DateTimeOffset IssuedAt,
    int TtlSeconds,
    DateTimeOffset? ResolvedAt,
    GeoPoint? ResolvedGeo,
    decimal? ResolvedAccuracyM)
{
    /// <summary>When the 300 s window closes (ADD §11.15). The durable deadline; see <c>RideTimerKinds</c>.</summary>
    public DateTimeOffset ExpiresAt => IssuedAt.AddSeconds(TtlSeconds);

    /// <summary>
    /// Whether the request can still be answered. <b>Two states, not one</b>:
    /// <c>RiderNotRegistered</c> is live because AL-45 gives that rider a <c>pickup_confirm</c> link
    /// by SMS and SCR-WT-003 feeds this same machine, so the request is open on another channel
    /// rather than over.
    /// </summary>
    public bool IsLive => LocationRequestStates.Live.Contains(State);
}

/// <summary>The fields <c>POST /v1/location-requests</c> writes.</summary>
public sealed record NewLocationRequest(
    Guid RequestId,
    Guid BookerId,
    Guid? RiderId,
    byte[] RiderPhoneHash,
    string State,
    int TtlSeconds,
    Guid? RideId);

/// <summary>
/// <c>rides.location_requests</c> — the P-02 booker→rider GPS round-trip.
/// </summary>
/// <remarks>
/// <para>
/// Every resolution is a single conditional <c>UPDATE</c> guarded on the request still being live
/// and on the row still being inside its own TTL, so the three ways a request can end — the rider answers,
/// the rider refuses, the window closes — race each other in the database and the first writer wins.
/// A second confirmation of a request already confirmed changes nothing and is answered
/// <c>410</c>, which is what "only the first confirmation is honoured" means when two taps and a
/// sweep are in flight at once (ADD §11.15).
/// </para>
/// <para>
/// The TTL predicate is <c>issued_at + ttl_seconds &gt; now()</c> evaluated by <b>Postgres</b>, for
/// the same reason the offer deadline is (§11.11): a rider whose phone clock is fast must not be
/// able to answer a request that has already expired for the booker, and a sweeping replica whose
/// clock ran ahead must not be able to expire one the rider is still inside the window to answer.
/// </para>
/// </remarks>
public interface ILocationRequestRepository
{
    Task<LocationRequestRow> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewLocationRequest request,
        CancellationToken cancellationToken);

    Task<LocationRequestRow?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>Pending | RiderNotRegistered → Confirmed</c>, stamping the position the rider shared.
    /// </summary>
    /// <param name="requiredRiderId">
    /// The authenticated rider, on the in-app path. <see langword="null"/> on AL-45's web path,
    /// where the request has no <c>rider_id</c> to match — the <c>pickup_confirm</c> token is the
    /// credential there and public-bff has already burned it.
    /// </param>
    Task<LocationRequestRow?> ConfirmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        Guid? requiredRiderId,
        GeoPoint geo,
        double? accuracyM,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>Pending | RiderNotRegistered → Declined</c>. Writes no coordinates and takes none (P-02).
    /// </summary>
    Task<LocationRequestRow?> DeclineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        Guid? requiredRiderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Expires every other request this booker still has open (ADD §11.15: "only the *first*
    /// confirmation is honoured per booker session; subsequent confirmations transition to
    /// <c>Expired</c>").
    /// </summary>
    Task<IReadOnlyList<LocationRequestRow>> ExpireOthersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        Guid exceptRequestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Expires the requests whose 300 s window has closed, claiming them for this replica.
    /// </summary>
    /// <remarks>
    /// The durable backstop for a rider who never answers (ADD §11.15's expiry path). Unlike the
    /// <c>rides.timers</c> kinds this is not a lease: the transition <b>is</b> the claim, because
    /// live → <c>Expired</c> is one-way and a row that has moved cannot be claimed twice.
    /// <c>FOR UPDATE SKIP LOCKED</c> keeps two replicas off the same row inside one pass.
    /// </remarks>
    Task<IReadOnlyList<LocationRequestRow>> ClaimExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// How many requests this booker has issued since <paramref name="since"/> — P-12's 5/h and
    /// 30/day, counted over <c>ix_location_requests_booker</c>.
    /// </summary>
    Task<int> CountIssuedSinceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        DateTimeOffset since,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILocationRequestRepository"/>
public sealed class LocationRequestRepository : ILocationRequestRepository
{
    private const string Columns =
        "id, ride_id, request_id, booker_id, rider_id, rider_phone_hash, state, issued_at, " +
        "ttl_seconds, resolved_at, resolved_geo, resolved_accuracy_m";

    /// <summary>
    /// The predicate that makes Postgres, and not a caller's clock, decide the 300 s window. Both
    /// live states are in it — see <see cref="LocationRequestRow.IsLive"/>.
    /// </summary>
    private const string StillLive =
        "state IN ('" + LocationRequestStates.Pending + "', '" + LocationRequestStates.RiderNotRegistered + "') " +
        "AND issued_at + make_interval(secs => ttl_seconds) > now()";

    /// <summary>The same two states, unqualified by the deadline.</summary>
    private const string LiveStates =
        "('" + LocationRequestStates.Pending + "', '" + LocationRequestStates.RiderNotRegistered + "')";

    public Task<LocationRequestRow> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewLocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        return connection.QuerySingleAsync<LocationRequestRow>(new CommandDefinition(
            $"""
             INSERT INTO rides.location_requests
               (request_id, booker_id, rider_id, rider_phone_hash, state, ttl_seconds, ride_id,
                resolved_at)
             VALUES
               (@RequestId, @BookerId, @RiderId, @RiderPhoneHash, @State, @TtlSeconds, @RideId,
                CASE WHEN @State IN {LiveStates} THEN NULL ELSE now() END)
             RETURNING {Columns};
             """,
            request,
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<LocationRequestRow?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<LocationRequestRow>(new CommandDefinition(
            $"SELECT {Columns} FROM rides.location_requests WHERE request_id = @RequestId;",
            new { RequestId = requestId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<LocationRequestRow?> ConfirmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        Guid? requiredRiderId,
        GeoPoint geo,
        double? accuracyM,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<LocationRequestRow>(new CommandDefinition(
            $"""
             UPDATE rides.location_requests
                SET state = '{LocationRequestStates.Confirmed}',
                    resolved_geo = @Geo,
                    resolved_accuracy_m = @AccuracyM,
                    resolved_at = now()
              WHERE request_id = @RequestId
                AND (@RequiredRiderId::uuid IS NULL OR rider_id = @RequiredRiderId)
                AND {StillLive}
             RETURNING {Columns};
             """,
            new
            {
                RequestId = requestId,
                RequiredRiderId = requiredRiderId,
                Geo = geo,
                AccuracyM = accuracyM is { } accuracy ? (decimal)accuracy : (decimal?)null,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<LocationRequestRow?> DeclineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        Guid? requiredRiderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `resolved_geo` is not in the SET list and there is no parameter that could put one there.
        // P-02's fence — "declining never leaks the rider's location" — is a property of this
        // statement rather than of the caller remembering.
        return connection.QuerySingleOrDefaultAsync<LocationRequestRow>(new CommandDefinition(
            $"""
             UPDATE rides.location_requests
                SET state = '{LocationRequestStates.Declined}', resolved_at = now()
              WHERE request_id = @RequestId
                AND (@RequiredRiderId::uuid IS NULL OR rider_id = @RequiredRiderId)
                AND {StillLive}
             RETURNING {Columns};
             """,
            new { RequestId = requestId, RequiredRiderId = requiredRiderId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<LocationRequestRow>> ExpireOthersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        Guid exceptRequestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<LocationRequestRow>(new CommandDefinition(
            $"""
             UPDATE rides.location_requests
                SET state = '{LocationRequestStates.Expired}', resolved_at = now()
              WHERE booker_id = @BookerId
                AND request_id <> @ExceptRequestId
                AND state IN {LiveStates}
             RETURNING {Columns};
             """,
            new { BookerId = bookerId, ExceptRequestId = exceptRequestId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<LocationRequestRow>> ClaimExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The subquery is what `ix_location_requests_due` (migration 0609) serves: a partial index
        // on the Pending rows ordered by issue time, so the scan is proportional to the backlog and
        // not to the booker history.
        var rows = await connection.QueryAsync<LocationRequestRow>(new CommandDefinition(
            $"""
             UPDATE rides.location_requests r
                SET state = '{LocationRequestStates.Expired}', resolved_at = now()
              WHERE r.id IN (
                    SELECT id FROM rides.location_requests
                     WHERE state IN {LiveStates}
                       AND issued_at + make_interval(secs => ttl_seconds) <= now()
                     ORDER BY issued_at
                     LIMIT @BatchSize
                       FOR UPDATE SKIP LOCKED)
             RETURNING r.id, r.ride_id, r.request_id, r.booker_id, r.rider_id, r.rider_phone_hash,
                       r.state, r.issued_at, r.ttl_seconds, r.resolved_at, r.resolved_geo,
                       r.resolved_accuracy_m;
             """,
            new { BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<int> CountIssuedSinceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid bookerId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Every request counts, whatever became of it. P-12 limits how often a booker may *ping* a
        // rider, so a request that was declined is exactly the one the limit is aimed at.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM rides.location_requests
             WHERE booker_id = @BookerId AND issued_at >= @Since;
            """,
            new { BookerId = bookerId, Since = since },
            transaction,
            cancellationToken: cancellationToken));
    }
}
