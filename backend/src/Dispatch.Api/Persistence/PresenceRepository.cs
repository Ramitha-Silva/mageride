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
}

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

        // Scoped by owner in the statement rather than checked afterwards: "somebody else's
        // vehicle" and "no such vehicle" are the same answer to a caller, and a WHERE clause is
        // the only version of that rule a later refactor cannot drop.
        return connection.QuerySingleOrDefaultAsync<OnlineVehicle>(new CommandDefinition(
            """
            SELECT id, owner_id, vehicle_type, mode, status
              FROM registry.vehicles
             WHERE id = @VehicleId AND owner_id = @DriverId;
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
}
