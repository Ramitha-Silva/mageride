using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// The org's vehicle roster — read through <c>registry.fleet_vehicles_fleet</c> (migration 1806),
/// written through <c>registry.vehicles</c> for the one column AL-24 item 16b gives this service.
/// </summary>
/// <remarks>
/// <para>
/// <b>The read never touches <c>registry.vehicles</c> directly.</b> That table holds every vehicle
/// on the platform and the fleet read role is granted no privilege on it at all; the view is the
/// only reach, and it carries the org predicate itself. A roster query that forgot its
/// <c>WHERE</c> therefore returns the caller's own vehicles, not everybody's.
/// </para>
/// <para>
/// <b>The write is registry-svc's table and fleet-svc's decision.</b> D3' Δ 2026-06-21 item 16b
/// puts <c>PUT /fleets/{fleetId}/vehicles/{vehicleId}/classification</c> on this service, and the
/// value it sets is <c>registry.vehicles.mode_b_billing</c> — there is no other column and no
/// internal route on registry-svc for it. The two writers do not overlap: registry-svc owns
/// registration, status and the document lifecycle, fleet-svc owns the Service-payment pair for a
/// vehicle that is in one of its fleets. Named in the C058 handoff.
/// </para>
/// </remarks>
public interface IFleetVehicleRepository
{
    /// <summary>One of the org's vehicles, or <see langword="null"/> when it is not one of them.</summary>
    Task<FleetVehicle?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the Service-payment classification (AL-24 item 16b, AL-51's label).
    /// </summary>
    /// <remarks>
    /// Guarded on the vehicle being in this fleet <em>in the same statement</em>. Checking
    /// membership first and updating afterwards would leave a window in which a vehicle removed
    /// from the fleet still had its fare set by its former operator.
    /// </remarks>
    Task<FleetVehicle?> SetClassificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string modeBBilling,
        int? defaultMonthlyFareMinor,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetVehicleRepository"/>
internal sealed class FleetVehicleRepository : IFleetVehicleRepository
{
    private const string ViewColumns = """
        fleet_id, vehicle_id, mode, registration_number, vehicle_type, status,
        dispatch_state, mode_b_billing, default_monthly_fare_minor, driver_name
        """;

    public async Task<FleetVehicle?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<FleetVehicle>(new CommandDefinition(
            $"""
             SELECT {ViewColumns} FROM registry.fleet_vehicles_fleet
              WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId;
             """,
            new { FleetId = fleetId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<FleetVehicle?> SetClassificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string modeBBilling,
        int? defaultMonthlyFareMinor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The fare is nulled when the vehicle goes Free rather than left behind. A stale default
        // on a Free vehicle is a number subscription-svc could pick up if the vehicle were ever
        // switched back, and "Free, Rs 2,500" is not a state SCR-FP-004 can render.
        //
        // The RETURNING list is the view's shape so the response is identical whichever route
        // produced it; `mode` is re-read from registry.fleet_vehicles because the UPDATE targets
        // registry.vehicles, which has its own `mode` and could disagree if a vehicle were ever
        // re-moded outside the fleet (registry-svc's write, not this one's).
        return await connection.QuerySingleOrDefaultAsync<FleetVehicle>(new CommandDefinition(
            """
            UPDATE registry.vehicles v
               SET mode_b_billing = @ModeBBilling,
                   default_monthly_fare_minor =
                     CASE WHEN @ModeBBilling = 'paid' THEN @DefaultMonthlyFareMinor ELSE NULL END
              FROM registry.fleet_vehicles fv
             WHERE v.id = @VehicleId AND fv.vehicle_id = v.id AND fv.fleet_id = @FleetId
            RETURNING fv.fleet_id, fv.vehicle_id, fv.mode, v.registration_number, v.vehicle_type,
                      v.status, v.dispatch_state, v.mode_b_billing, v.default_monthly_fare_minor,
                      v.driver_name;
            """,
            new
            {
                FleetId = fleetId,
                VehicleId = vehicleId,
                ModeBBilling = modeBBilling,
                DefaultMonthlyFareMinor = defaultMonthlyFareMinor,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
