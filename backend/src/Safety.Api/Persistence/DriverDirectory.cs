using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Safety.Persistence;

/// <summary>The three things a shared link says about who is driving.</summary>
public sealed record DriverSummary(Guid DriverId, string? Name, string? RegistrationNumber, string? VehicleType);

/// <summary>
/// Who is driving, for the D-34 public view.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name and a plate, and nothing else.</b> The person holding a shared link is somebody the
/// passenger chose to tell; what they need is enough to recognise the car at the kerb and to say who
/// it was afterwards. No phone number — AL-48 leaves a counterparty number on the *ride detail* for
/// the parties to the ride, and a share link is not one of them.
/// </para>
/// <para>
/// The same read-only cross-context read ride-svc's <c>DriverSummaryRepository</c> makes of
/// <c>iam.users</c> and <c>registry.vehicles</c>.
/// </para>
/// </remarks>
public interface IDriverDirectory
{
    Task<DriverSummary?> FindAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The driver on a Mode C ride, for a report that names the ride (US-12.5).</summary>
    Task<Guid?> FindRideDriverAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>Whether a vehicle exists at all — <c>404 vehicle-not-found</c> otherwise.</summary>
    Task<bool> VehicleExistsAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverDirectory"/>
internal sealed class DriverDirectory(INpgsqlConnectionFactory connections) : IDriverDirectory
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<DriverSummary?> FindAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // The vehicle is the one the driver currently has live (registry's own selection), joined
        // loosely: a driver between vehicles still has a name, and a share view with a name and no
        // plate is better than one with neither.
        return await connection.QuerySingleOrDefaultAsync<DriverSummary>(
            new CommandDefinition(
                """
                SELECT u.id            AS driver_id,
                       u.first_name    AS name,
                       v.registration_number,
                       v.vehicle_type
                  FROM iam.users u
                  LEFT JOIN LATERAL (
                       SELECT r.id, r.registration_number, r.vehicle_type
                         FROM rides.rides r2
                         JOIN registry.vehicles r ON r.id = r2.accepted_vehicle_id
                        WHERE r2.accepted_driver_id = u.id
                        ORDER BY r2.created_at DESC
                        LIMIT 1) v ON true
                 WHERE u.id = @DriverId;
                """,
                new { DriverId = driverId },
                cancellationToken: cancellationToken));
    }

    public async Task<Guid?> FindRideDriverAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT accepted_driver_id FROM rides.rides WHERE id = @RideId;",
                new { RideId = rideId },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> VehicleExistsAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM registry.vehicles WHERE id = @VehicleId);",
                new { VehicleId = vehicleId },
                cancellationToken: cancellationToken));
    }
}
