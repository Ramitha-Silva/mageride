using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.driver_presence</c> (migration 0701) — the durable half of the R-08 availability
/// pair. Redis holds the hot candidate index; this table survives a flush and is what the exact
/// <c>ST_DWithin</c> post-filter reads (ADD §6, D-06).
/// </summary>
public interface IPresenceRepository
{
    /// <summary>
    /// The vehicle a driver may go online on: their own, Mode C, APPROVED. Returns
    /// <see langword="null"/> when no such row exists, which the caller turns into
    /// <c>404 vehicle-not-found</c> or <c>403 vehicle-not-approved</c> after a second look.
    /// </summary>
    Task<OnlineVehicle?> FindVehicleAsync(
        NpgsqlConnection connection, Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    Task<PresenceRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// The presence row a vehicle is on standby under. The EMQX plane knows only vehicles — the
    /// topic is <c>veh/{vehicleId}/status</c> and the ACL binds it to the device credential — so
    /// this is how a last will reaches a driver (R-15).
    /// </summary>
    Task<PresenceRow?> FindByVehicleAsync(
        NpgsqlConnection connection, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Upserts presence to AVAILABLE — the driver is in the pool as of now.</summary>
    Task<PresenceRow> GoOnlineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        string vehicleType,
        GeoPoint position,
        GeoPoint? driverHome,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves presence to <paramref name="toState"/> only from <paramref name="fromStates"/>. One
    /// conditional UPDATE, so two workers reacting to the same event cannot both "win" a move.
    /// </summary>
    Task<PresenceRow?> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        IReadOnlyCollection<string> fromStates,
        string toState,
        CancellationToken cancellationToken);

    /// <summary>Unconditional move to OFFLINE — <c>POST /v1/standby/offline</c> always succeeds.</summary>
    Task<PresenceRow?> GoOfflineAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// R-08: a live GPS sample refreshes the driver's presence. Returns <see langword="null"/> when
    /// the vehicle is not on standby, or when the sample is older than what the row already holds.
    /// </summary>
    Task<PositionUpdate?> RecordPositionAsync(
        NpgsqlConnection connection,
        Guid vehicleId,
        GeoPoint position,
        DateTimeOffset sampledAt,
        int moveThresholdM,
        CancellationToken cancellationToken);
}

/// <summary>What one position sample did to a presence row.</summary>
/// <param name="Moved">
/// The driver travelled more than <c>Dispatch:PositionMoveThresholdM</c> since the row was last
/// written, so <c>geo</c> changed and the Redis GEO index needs the new coordinate. False means only
/// <c>last_seen_at</c> advanced, which is the common case for a driver waiting at a rank.
/// </param>
public sealed record PositionUpdate(
    Guid DriverId, Guid VehicleId, string VehicleType, string State, GeoPoint? Geo, bool Moved);

/// <summary>What <c>POST /v1/standby/online</c> found when it looked the vehicle up.</summary>
public sealed record OnlineVehicle(Guid Id, Guid OwnerId, string VehicleType, string Mode, string Status);

/// <inheritdoc cref="IPresenceRepository"/>
public sealed class PresenceRepository : IPresenceRepository
{
    private const string Columns =
        "driver_id, vehicle_id, vehicle_type, state, geo, driver_home, last_seen_at, updated_at";

    public Task<OnlineVehicle?> FindVehicleAsync(
        NpgsqlConnection connection, Guid driverId, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // registry.driver_eligible_vehicles, not registry.vehicles (C028, migration 0310).
        // Scoping by owner_id — which is what this read used to do — cannot see a vehicle a fleet
        // has *assigned* to the driver, and US-13.9 says an assigned driver may go online with
        // one. The projection is registry-svc's answer to "which vehicles may this driver
        // operate", and it is scoped by driver_id, so "somebody else's vehicle" and "no such
        // vehicle" stay the same answer to a caller.
        //
        // The raw columns are read rather than the view's `is_go_live_eligible`, deliberately:
        // PresenceService maps an unapproved vehicle to `vehicle-not-approved` and a Mode A/B one
        // to `mode-not-allowed`, and a pre-filtered read would collapse both into
        // `vehicle-not-found`.
        return connection.QuerySingleOrDefaultAsync<OnlineVehicle>(new CommandDefinition(
            """
            SELECT vehicle_id AS id, owner_id, vehicle_type, mode, status
              FROM registry.driver_eligible_vehicles
             WHERE vehicle_id = @VehicleId AND driver_id = @DriverId;
            """,
            new { VehicleId = vehicleId, DriverId = driverId },
            cancellationToken: cancellationToken));
    }

    public Task<PresenceRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PresenceRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PresenceRow?> FindByVehicleAsync(
        NpgsqlConnection connection, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // At most one row: `driver_id` is the primary key and a vehicle is on standby under one
        // driver at a time (D-03's one-live-vehicle-per-driver rule, seen from the other side).
        // ORDER BY updated_at is the tie-break for a row left behind by a handover.
        return connection.QuerySingleOrDefaultAsync<PresenceRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.driver_presence
              WHERE vehicle_id = @VehicleId
              ORDER BY updated_at DESC
              LIMIT 1;
             """,
            new { VehicleId = vehicleId },
            cancellationToken: cancellationToken));
    }

    public async Task<PresenceRow> GoOnlineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        string vehicleType,
        GeoPoint position,
        GeoPoint? driverHome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // One row per driver is the table's primary key, which is the presence-plane echo of O2
        // and of D-03's one-live-session rule: going online on a second vehicle overwrites the
        // first rather than creating a second presence.
        //
        // driver_home is COALESCEd rather than overwritten, so a heartbeat that omits it does not
        // silently erase the D-06 Job Board anchor the driver set earlier.
        return await connection.QuerySingleAsync<PresenceRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.driver_presence
               (driver_id, vehicle_id, vehicle_type, state, geo, driver_home, last_seen_at)
             VALUES
               (@DriverId, @VehicleId, @VehicleType, '{PresenceStates.Available}', @Geo, @DriverHome, now())
             ON CONFLICT (driver_id) DO UPDATE
                SET vehicle_id = EXCLUDED.vehicle_id,
                    vehicle_type = EXCLUDED.vehicle_type,
                    state = '{PresenceStates.Available}',
                    geo = EXCLUDED.geo,
                    driver_home = COALESCE(EXCLUDED.driver_home, dispatch.driver_presence.driver_home),
                    last_seen_at = now()
             RETURNING {Columns};
             """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                VehicleType = vehicleType,
                Geo = position,
                DriverHome = driverHome,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PresenceRow?> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        IReadOnlyCollection<string> fromStates,
        string toState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fromStates);

        return connection.QuerySingleOrDefaultAsync<PresenceRow>(new CommandDefinition(
            $"""
             UPDATE dispatch.driver_presence
                SET state = @ToState
              WHERE driver_id = @DriverId
                AND state = ANY(@FromStates)
             RETURNING {Columns};
             """,
            new { DriverId = driverId, FromStates = fromStates.ToArray(), ToState = toState },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PresenceRow?> GoOfflineAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // geo is cleared with the state. A stale position left behind would keep answering the
        // ST_DWithin post-filter for a driver who has gone home, and the only thing stopping an
        // offer would then be the state column — one predicate instead of two.
        return connection.QuerySingleOrDefaultAsync<PresenceRow>(new CommandDefinition(
            $"""
             UPDATE dispatch.driver_presence
                SET state = '{PresenceStates.Offline}', geo = NULL
              WHERE driver_id = @DriverId
             RETURNING {Columns};
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PositionUpdate?> RecordPositionAsync(
        NpgsqlConnection connection,
        Guid vehicleId,
        GeoPoint position,
        DateTimeOffset sampledAt,
        int moveThresholdM,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `moved` has to be computed from the row as it stands BEFORE the update, and an
        // `UPDATE … RETURNING` returns the new values — hence the CTE. It is also what decides
        // whether the caller issues a GEOADD, so getting it from the same statement that did the
        // write is what keeps Postgres and Redis describing one position rather than two.
        //
        // `last_seen_at` advances on every accepted sample while `geo` only advances on a real
        // move: D5' §3.2's freshness gate is about liveness, and a driver sitting at a rank for ten
        // minutes is the candidate this service most wants to keep, not the first one to drop.
        //
        // The `sampledAt >= last_seen_at` guard is the reorder defence. `telemetry.normalized` is
        // keyed by vehicleId so one consumer owns a vehicle and ordering holds — except for the
        // seconds around a group rebalance, which is exactly when a stale sample could otherwise
        // teleport a driver backwards.
        return connection.QuerySingleOrDefaultAsync<PositionUpdate>(new CommandDefinition(
            $"""
             WITH before AS (
               SELECT driver_id,
                      (geo IS NULL OR NOT ST_DWithin(geo, @Geo, @ThresholdM)) AS moved
                 FROM dispatch.driver_presence
                WHERE vehicle_id = @VehicleId)
             UPDATE dispatch.driver_presence p
                SET last_seen_at = @SampledAt,
                    geo = CASE WHEN b.moved THEN @Geo ELSE p.geo END
               FROM before b
              WHERE p.driver_id = b.driver_id
                AND p.state <> '{PresenceStates.Offline}'
                AND (p.last_seen_at IS NULL OR @SampledAt >= p.last_seen_at)
             RETURNING p.driver_id AS DriverId,
                       p.vehicle_id AS VehicleId,
                       p.vehicle_type AS VehicleType,
                       p.state AS State,
                       p.geo AS Geo,
                       b.moved AS Moved;
             """,
            new
            {
                VehicleId = vehicleId,
                Geo = position,
                SampledAt = sampledAt,
                ThresholdM = (double)moveThresholdM,
            },
            cancellationToken: cancellationToken));
    }
}
