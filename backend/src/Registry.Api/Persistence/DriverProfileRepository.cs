using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>
/// <c>registry.driver_profiles</c> (server_db_schema.md §2, D4' §2; migrations 0304 and 0308).
/// </summary>
public interface IDriverProfileRepository
{
    Task<DriverProfile?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the profile row if the driver has none, and returns it either way. An existing
    /// display name is never overwritten — <c>PUT /v1/drivers/profile</c> (C029) owns changing it,
    /// and a vehicle registration is not a rename.
    /// </summary>
    Task<DriverProfile> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string displayName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Points the driver at <paramref name="vehicleId"/> as their single live publisher (US-9.6).
    /// Returns <see langword="false"/> when the driver has no profile row.
    /// </summary>
    /// <remarks>
    /// <b>Entitlement is the caller's to check.</b> 0308 made ownership a composite foreign key;
    /// 0311 relaxed it to a plain one when US-13.9 gave an assigned non-owner the right to select
    /// a fleet vehicle. What the database still guarantees is that the selection names a real
    /// vehicle and is cleared if that vehicle is deleted. Who may select it is
    /// <c>registry.driver_eligible_vehicles</c>, read by <c>VehicleService</c>.
    /// </remarks>
    Task<bool> SelectActiveVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears the selection when it names <paramref name="vehicleId"/>, and says whether it did.
    /// </summary>
    /// <remarks>
    /// Deactivating the selected vehicle (US-2.16) has to unpick the selection, because the
    /// foreign key fires on DELETE and a status change is not one. A vehicle that stayed selected
    /// while DEACTIVATED would fail the eligibility gate on every go-online with nothing on the
    /// screen to explain why.
    /// </remarks>
    Task<bool> ClearActiveVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverProfileRepository"/>
public sealed class DriverProfileRepository : IDriverProfileRepository
{
    private const string Columns =
        "driver_id, display_name, photo_url, active_vehicle_id, active_vehicle_selected_at";

    public Task<DriverProfile?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<DriverProfile>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.driver_profiles WHERE driver_id = @DriverId;",
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<DriverProfile> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // DO UPDATE with a no-op assignment rather than DO NOTHING, so RETURNING always yields
        // the row — DO NOTHING returns nothing at all when the conflict fires.
        return connection.QuerySingleAsync<DriverProfile>(new CommandDefinition(
            $"""
             INSERT INTO registry.driver_profiles (driver_id, display_name)
             VALUES (@DriverId, @DisplayName)
             ON CONFLICT (driver_id) DO UPDATE SET display_name = registry.driver_profiles.display_name
             RETURNING {Columns};
             """,
            new { DriverId = driverId, DisplayName = displayName },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> SelectActiveVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // One row per driver, so this replaces any previous selection: "only one vehicle can go
        // live at a time" (US-9.6) is the primary key, not a DELETE-then-INSERT that could leave
        // two selections behind if it were interrupted.
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = @VehicleId,
                   active_vehicle_selected_at = now()
             WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return updated == 1;
    }

    public async Task<bool> ClearActiveVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Conditional on the vehicle, so deactivating a vehicle the driver had not selected does
        // not silently take away the selection of one they had. Both columns move together —
        // ck_driver_profiles_active_vehicle_pair rejects a half-cleared row.
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = NULL,
                   active_vehicle_selected_at = NULL
             WHERE driver_id = @DriverId AND active_vehicle_id = @VehicleId;
            """,
            new { DriverId = driverId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return updated == 1;
    }
}
