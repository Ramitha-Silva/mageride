using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Subscriptions.Persistence;

/// <summary>What the fee rule needs to know about a vehicle. Read-only — registry-svc owns the row.</summary>
public sealed record VehicleFacts(
    Guid VehicleId, Guid OwnerId, string VehicleType, string Mode, string Status, string RegistrationNumber);

/// <summary>
/// The <c>registry.*</c> reads the fee rule makes. <b>This service writes nothing here.</b>
/// </summary>
/// <remarks>
/// Two facts, both of which live in registry-svc's bounded context (C028/C029) and neither of which
/// has an internal route to fetch it: the vehicle's type (which rate applies) and the driver's
/// currently selected vehicle (US-9.6/9.7's "the single active vehicle"). The alternative is a
/// synchronous call to registry-svc inside D-08's budget on every second trip — the same trade
/// wallet-svc's <c>outstandingDebtMinor</c> and iam-svc's bootstrap already make, and named here for
/// the same reason.
/// </remarks>
internal interface IVehicleRepository
{
    Task<VehicleFacts?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// The vehicle the driver has selected to go live on (US-9.6), or <see langword="null"/> when they
    /// have selected none.
    /// </summary>
    Task<VehicleFacts?> ActiveVehicleAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleRepository"/>
internal sealed class VehicleRepository(INpgsqlConnectionFactory connections) : IVehicleRepository
{
    private const string SelectColumns =
        """
        v.id AS vehicle_id, v.owner_id, v.vehicle_type, v.mode, v.status,
        v.registration_number
        """;

    public async Task<VehicleFacts?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VehicleFacts>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM registry.vehicles v WHERE v.id = @VehicleId;",
                new { VehicleId = vehicleId },
                cancellationToken: cancellationToken));
    }

    public async Task<VehicleFacts?> ActiveVehicleAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // registry.driver_profiles.active_vehicle_id (migration 0308) is the answer to "which vehicle?"
        // — the column US-9.6's "only one vehicle can go live at a time" is enforced by, because the
        // profile row is 1:1 with the driver.
        return await connection.QuerySingleOrDefaultAsync<VehicleFacts>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM registry.driver_profiles p
                  JOIN registry.vehicles v ON v.id = p.active_vehicle_id
                 WHERE p.driver_id = @DriverId;
                """,
                new { DriverId = driverId },
                cancellationToken: cancellationToken));
    }
}
