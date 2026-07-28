using Dapper;
using MageRide.Shared.Primitives;
using MageRide.TripState.Domain;
using Npgsql;

namespace MageRide.TripState.Persistence;

/// <summary>
/// <c>trips.sessions</c> — the Mode A/B tracking session (D-03, migrations 0501 and 0504).
/// </summary>
/// <remarks>
/// <b>The active-session mutex is <c>ux_sessions_active_driver</c> and nothing else.</b> Redis
/// carries the same fact for the planes that need it quickly, but the invariant "a driver holds
/// one live session" is a partial unique index, so ten concurrent starts settle without anybody's
/// cooperation and a Redis outage cannot let a second one through.
/// </remarks>
public interface ISessionRepository
{
    /// <summary>The driver's live session, or <see langword="null"/>. Single-row by the index.</summary>
    Task<Session?> FindActiveByDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>The vehicle's live session — what <c>GET /v1/sessions/{vehicleId}/active</c> reads.</summary>
    Task<Session?> FindActiveByVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    Task<Session?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a session.
    /// </summary>
    /// <remarks>
    /// May throw a unique violation on <c>ux_sessions_active_driver</c>; the caller turns that into
    /// <c>409 driver-already-live</c> rather than checking first, because a check-then-insert is
    /// exactly the race the index exists to settle.
    /// </remarks>
    Task<Session> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid vehicleId,
        Guid driverId,
        string mode,
        Guid? routeId,
        bool autoEndAtDestination,
        GeoPoint? destination,
        string startedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes a session, but only from ACTIVE.
    /// </summary>
    /// <returns>The closed row, or <see langword="null"/> when it was no longer live — which is how
    /// a dashboard End and a fired timer settle on one winner without a lock.</returns>
    Task<Session?> EndAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string endReason,
        string endedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Reopens an auto-ended session in place (US-5.10), keeping its id and started_at.</summary>
    /// <remarks>
    /// In place rather than as a new row: US-5.10 calls it a restart, the passengers watching it
    /// have the session id, and a new row would break the D-03 index's meaning of "the driver's
    /// current session" for anything that cached the old one.
    /// </remarks>
    Task<Session?> RestartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        DateTimeOffset restartableFrom,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Advances the idle clock and records where the vehicle is (US-5.3, US-5.4).</summary>
    Task<bool> RecordMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid sessionId,
        GeoPoint point,
        DateTimeOffset sampleTs,
        bool moved,
        CancellationToken cancellationToken);

    /// <summary>
    /// ACTIVE sessions that have not moved since <paramref name="idleSince"/> (US-5.3).
    /// </summary>
    /// <remarks><c>FOR UPDATE SKIP LOCKED</c>, so two replicas sweeping at once end disjoint sets
    /// rather than racing to close the same session.</remarks>
    Task<IReadOnlyList<Session>> ClaimIdleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset idleSince,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// ACTIVE sessions whose vehicle is inside its armed destination fence (US-5.4).
    /// </summary>
    /// <remarks>
    /// <c>ST_DWithin</c> on <c>geography</c> is metres on the spheroid, so the radius needs no
    /// projection — the same reason dispatch-svc's candidate query uses it directly.
    /// </remarks>
    Task<IReadOnlyList<Session>> ClaimArrivedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        double radiusM,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The most recent fix recorded on a session, for the movement comparison (US-5.3).</summary>
    Task<GeoPoint?> FindLastPositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Where the vehicle's previous journey finished — the US-5.4 fence centre.</summary>
    Task<GeoPoint?> FindLastEndPointAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISessionRepository"/>
public sealed class SessionRepository : ISessionRepository
{
    private const string Columns =
        "id, vehicle_id, driver_id, mode, state, route_id, auto_end_at_destination, end_reason, " +
        "started_by, ended_by, started_at, ended_at, last_movement_at";

    public Task<Session?> FindActiveByDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<Session>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM trips.sessions
              WHERE driver_id = @DriverId AND state = '{SessionStates.Active}';
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Session?> FindActiveByVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Not single-row by construction: the D-03 index is per driver, so two drivers could in
        // principle hold sessions on one vehicle. Newest wins rather than throwing — a read for a
        // client resume must not fail because of a state only a write path can prevent.
        return connection.QueryFirstOrDefaultAsync<Session>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM trips.sessions
              WHERE vehicle_id = @VehicleId AND state = '{SessionStates.Active}'
              ORDER BY started_at DESC
              LIMIT 1;
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Session?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<Session>(new CommandDefinition(
            $"SELECT {Columns} FROM trips.sessions WHERE id = @SessionId;",
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Session> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid vehicleId,
        Guid driverId,
        string mode,
        Guid? routeId,
        bool autoEndAtDestination,
        GeoPoint? destination,
        string startedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // last_movement_at is seeded to the start: a session whose vehicle never reports a single
        // fix has to age out of the idle sweep like any other, and a NULL would make it immortal.
        return connection.QuerySingleAsync<Session>(new CommandDefinition(
            $"""
             INSERT INTO trips.sessions
                 (vehicle_id, driver_id, mode, state, route_id, auto_end_at_destination,
                  destination_geo, started_by, started_at, last_movement_at)
             VALUES (@VehicleId, @DriverId, @Mode, '{SessionStates.Active}', @RouteId, @AutoEnd,
                     @Destination, @StartedBy, @Now, @Now)
             RETURNING {Columns};
             """,
            new
            {
                VehicleId = vehicleId,
                DriverId = driverId,
                Mode = mode,
                RouteId = routeId,
                AutoEnd = autoEndAtDestination,
                Destination = destination,
                StartedBy = startedBy,
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Session?> EndAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string endReason,
        string endedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `state = ACTIVE` is a predicate in the UPDATE, not a check before it. A dashboard End
        // and a fired idle timer arrive at the same instant often enough to matter; only the one
        // whose UPDATE matched gets a row back, and only it emits the event.
        //
        // end_geo remembers where this journey finished, which is what arms the *next* session's
        // destination fence (US-5.4). It comes from the last position seen on the session, so a
        // session that never reported one simply leaves it null and the next journey has no fence.
        return connection.QuerySingleOrDefaultAsync<Session>(new CommandDefinition(
            $"""
             UPDATE trips.sessions
                SET state = '{SessionStates.Completed}',
                    end_reason = @EndReason,
                    ended_by = @EndedBy,
                    ended_at = @Now,
                    end_geo = last_position_geo
              WHERE id = @SessionId AND state = '{SessionStates.Active}'
             RETURNING {Columns};
             """,
            new { SessionId = sessionId, EndReason = endReason, EndedBy = endedBy, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<Session?> RestartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        DateTimeOffset restartableFrom,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Every condition US-5.10 imposes is in the WHERE clause: the session is closed, it closed
        // automatically, and it closed inside the grace window. Checking them in C# and updating
        // afterwards would let a second request through the gap. The unique index still decides
        // whether the driver may hold it — a restart takes the mutex like a start does.
        return connection.QuerySingleOrDefaultAsync<Session>(new CommandDefinition(
            $"""
             UPDATE trips.sessions
                SET state = '{SessionStates.Active}',
                    end_reason = NULL,
                    ended_by = NULL,
                    ended_at = NULL,
                    end_geo = NULL,
                    last_movement_at = @Now
              WHERE id = @SessionId
                AND state = '{SessionStates.Completed}'
                AND end_reason IS NOT NULL
                AND end_reason <> '{EndReasons.DriverEnded}'
                AND ended_at >= @RestartableFrom
             RETURNING {Columns};
             """,
            new { SessionId = sessionId, RestartableFrom = restartableFrom, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> RecordMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid sessionId,
        GeoPoint point,
        DateTimeOffset sampleTs,
        bool moved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The position is recorded on every fix; the idle clock advances only when the vehicle
        // actually moved. That split is US-5.3's whole point — a bus parked at a terminus keeps
        // reporting, and treating those fixes as activity would make the timer unreachable.
        //
        // `GREATEST` guards the out-of-order case: telemetry.normalized is partitioned by vehicle
        // so fixes arrive in order per vehicle, but a replayed backlog (R-17) can interleave, and
        // an older fix must never wind the clock back.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE trips.sessions
                SET last_position_geo = @Point,
                    last_position_at = GREATEST(COALESCE(last_position_at, @SampleTs), @SampleTs),
                    last_movement_at = CASE WHEN @Moved
                                            THEN GREATEST(COALESCE(last_movement_at, @SampleTs), @SampleTs)
                                            ELSE last_movement_at END
              WHERE id = @SessionId AND state = '{SessionStates.Active}';
             """,
            new { SessionId = sessionId, Point = point, SampleTs = sampleTs, Moved = moved },
            transaction,
            cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<IReadOnlyList<Session>> ClaimIdleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset idleSince,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Session>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM trips.sessions
              WHERE state = '{SessionStates.Active}'
                AND COALESCE(last_movement_at, started_at) <= @IdleSince
              ORDER BY COALESCE(last_movement_at, started_at)
              LIMIT @Limit
                FOR UPDATE SKIP LOCKED;
             """,
            new { IdleSince = idleSince, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<Session>> ClaimArrivedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        double radiusM,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The fence is only armed when the driver asked for it *and* the previous journey left a
        // destination behind; both are `destination_geo IS NOT NULL`.
        var rows = await connection.QueryAsync<Session>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM trips.sessions
              WHERE state = '{SessionStates.Active}'
                AND auto_end_at_destination
                AND destination_geo IS NOT NULL
                AND last_position_geo IS NOT NULL
                AND ST_DWithin(destination_geo, last_position_geo, @RadiusM)
              ORDER BY started_at
              LIMIT @Limit
                FOR UPDATE SKIP LOCKED;
             """,
            new { RadiusM = radiusM, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<GeoPoint?> FindLastPositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QueryFirstOrDefaultAsync<GeoPoint?>(new CommandDefinition(
            "SELECT last_position_geo FROM trips.sessions WHERE id = @SessionId;",
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<GeoPoint?> FindLastEndPointAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QueryFirstOrDefaultAsync<GeoPoint?>(new CommandDefinition(
            """
            SELECT end_geo FROM trips.sessions
             WHERE vehicle_id = @VehicleId AND end_geo IS NOT NULL
             ORDER BY ended_at DESC
             LIMIT 1;
            """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
