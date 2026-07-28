using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>
/// <c>registry.driver_eligible_vehicles</c> — the go-live projection (migration 0310, US-9.6,
/// US-13.9).
/// </summary>
/// <remarks>
/// The one place "which vehicles may this driver operate" is answered. registry-svc owns it;
/// dispatch-svc reads the same view for its standby gate and trip-state-svc will for session
/// start, so the three cannot derive the rule differently.
/// </remarks>
public interface IEligibilityRepository
{
    /// <summary>Everything the driver may operate, owned first, then the assigned group (US-13.9).</summary>
    Task<IReadOnlyList<EligibleVehicle>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// One entitlement, or <see langword="null"/> when the driver neither owns the vehicle nor
    /// holds a live assignment to it.
    /// </summary>
    Task<EligibleVehicle?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEligibilityRepository"/>
public sealed class EligibilityRepository : IEligibilityRepository
{
    private const string Columns =
        "driver_id, vehicle_id, source, fleet_id, owner_id, registration_number, vehicle_type, " +
        "mode, status, dispatch_state, onboarding_status, driver_name, driver_photo_url, " +
        "created_at, is_go_live_eligible";

    public async Task<IReadOnlyList<EligibleVehicle>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Owned before assigned so the caller can slice the list into US-13.9's two groups without
        // sorting; `created_at, vehicle_id` inside each group matches ListByOwnerAsync's order, so
        // My Vehicles does not reshuffle between the two reads.
        var rows = await connection.QueryAsync<EligibleVehicle>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM registry.driver_eligible_vehicles
              WHERE driver_id = @DriverId
              ORDER BY CASE source WHEN '{EligibilitySources.Owned}' THEN 0 ELSE 1 END,
                       created_at,
                       vehicle_id;
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<EligibleVehicle?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The view is DISTINCT ON (driver_id, vehicle_id), so this is single-row by construction
        // even for a driver who both owns a vehicle and is assigned to it.
        return connection.QuerySingleOrDefaultAsync<EligibleVehicle>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM registry.driver_eligible_vehicles
              WHERE driver_id = @DriverId AND vehicle_id = @VehicleId;
             """,
            new { DriverId = driverId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
