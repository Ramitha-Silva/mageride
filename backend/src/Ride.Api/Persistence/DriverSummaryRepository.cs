using Dapper;
using MageRide.Ride.Domain;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>
/// The read-only projection behind <c>RideDetail.driver</c>.
/// </summary>
/// <remarks>
/// <para>
/// This reads <c>registry.vehicles</c>, which belongs to registry-svc (C021). It is a **read and
/// only ever a read** — ride-svc writes nothing outside the <c>rides</c> schema. It is here
/// because the contract puts <c>driver.name</c>, <c>photoUrl</c> and <c>registrationNumber</c> on
/// the ride detail from <c>Accepted</c> onward (US-2.12: the passenger is shown who is coming),
/// registry-svc owns those three facts, and query-svc — which will own this read model — is C048.
/// </para>
/// <para>
/// <b>C048 replaces this.</b> When query-svc's read model lands, ride-svc should stop crossing the
/// schema boundary and the join goes with it.
/// </para>
/// </remarks>
public interface IDriverSummaryRepository
{
    /// <summary>
    /// The driver behind an accepted ride's vehicle, or <see langword="null"/> when the ride has
    /// no vehicle yet or the vehicle has since been removed.
    /// </summary>
    Task<RideDriverSummary?> FindByVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverSummaryRepository"/>
public sealed class DriverSummaryRepository : IDriverSummaryRepository
{
    public Task<RideDriverSummary?> FindByVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // owner_id is matched as well as id: the vehicle recorded on the ride must still belong to
        // the driver recorded on the ride, or the name shown to the passenger is somebody else's.
        return connection.QuerySingleOrDefaultAsync<RideDriverSummary>(new CommandDefinition(
            """
            SELECT owner_id AS driver_id, driver_name AS name, driver_photo_url AS photo_url,
                   vehicle_type, registration_number
              FROM registry.vehicles
             WHERE id = @VehicleId AND owner_id = @DriverId;
            """,
            new { VehicleId = vehicleId, DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
