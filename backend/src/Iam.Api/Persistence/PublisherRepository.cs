using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// The two facts <c>POST /v1/auth/mqtt-token</c> needs and iam does not own: who owns a vehicle,
/// and whether its driver is on a ride (E-02).
/// </summary>
/// <remarks>
/// <para>
/// Both are reads across a bounded-context line — <c>registry.vehicles</c> belongs to
/// registry-svc and <c>rides.rides</c> to ride-svc — and both are deliberate. The alternative is
/// two synchronous HTTP calls on the path that mints a device's publishing credential, which
/// would mean a driver cannot start publishing while registry-svc is redeploying. Read-only, one
/// statement each, no writes, and the same shape dispatch-svc's <c>PresenceRepository</c> already
/// uses to read <c>registry.vehicles</c>.
/// </para>
/// <para>
/// The universal rule this does not break is the one about *state changes*: those go through the
/// outbox. Nothing here changes anything.
/// </para>
/// </remarks>
public interface IPublisherRepository
{
    /// <summary>The vehicle a token is being asked for, or <see langword="null"/> if there is none.</summary>
    Task<VehiclePublisher?> FindVehicleAsync(
        NpgsqlConnection connection, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// The driver's live ride, if any — <c>Accepted</c>, <c>DriverArrived</c>, <c>InProgress</c>
    /// or <c>PaymentPending</c>, which is exactly the set <c>ux_rides_driver_busy</c> allows one
    /// of (O2, R-10). At most one row by that index.
    /// </summary>
    Task<ActiveRide?> FindActiveRideForDriverAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublisherRepository"/>
public sealed class PublisherRepository : IPublisherRepository
{
    /// <summary>
    /// The four states in which a driver holds a ride. Kept as SQL rather than a parameter list
    /// so it reads the same as the index that enforces it.
    /// </summary>
    private const string DriverBusyStates =
        "('Accepted','DriverArrived','InProgress','PaymentPending')";

    public Task<VehiclePublisher?> FindVehicleAsync(
        NpgsqlConnection connection, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<VehiclePublisher>(new CommandDefinition(
            """
            SELECT id AS vehicle_id, owner_id, status
              FROM registry.vehicles
             WHERE id = @VehicleId;
            """,
            new { VehicleId = vehicleId },
            cancellationToken: cancellationToken));
    }

    public Task<ActiveRide?> FindActiveRideForDriverAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<ActiveRide>(new CommandDefinition(
            $"""
             SELECT id AS ride_id,
                    accepted_driver_id AS driver_id,
                    accepted_vehicle_id AS vehicle_id,
                    state,
                    created_at AS started_at
               FROM rides.rides
              WHERE accepted_driver_id = @DriverId
                AND state IN {DriverBusyStates};
             """,
            new { DriverId = driverId },
            cancellationToken: cancellationToken));
    }
}
