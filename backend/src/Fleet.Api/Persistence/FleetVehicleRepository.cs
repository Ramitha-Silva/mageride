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

    /// <summary>The org's whole roster, newest first (SCR-FP-004's status table).</summary>
    Task<IReadOnlyList<FleetVehicle>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds a Mode A or Mode B vehicle to the org (US-13.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two inserts, one statement, one transaction: <c>registry.vehicles</c> and the
    /// <c>registry.fleet_vehicles</c> row that puts it on this org's roster. A vehicle without its
    /// roster row belongs to nobody and is invisible to every fleet-scoped read, including the one
    /// that would let somebody notice.
    /// </para>
    /// <para>
    /// <paramref name="ownerId"/> is the organisation's owner, not the manager who typed the form:
    /// <c>registry.vehicles.owner_id</c> is what subscription-svc reads as "the vehicle's owner"
    /// for the Mode B money (C050 <c>is_vehicle_owner</c>), and a manager leaving the organisation
    /// must not take a bus's ownership with them.
    /// </para>
    /// </remarks>
    Task<FleetVehicle> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid ownerId,
        string registrationNumber,
        string vehicleType,
        string mode,
        string driverName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes a vehicle off the org's roster and out of service (US-13.7).
    /// </summary>
    /// <remarks>
    /// <b>Deactivates rather than deletes.</b> The vehicle's journeys, telemetry and — for a Mode B
    /// vehicle — its subscribers' payment history all reference the row; deleting it would take an
    /// operator's own analytics with it. DEACTIVATED is outside <c>ux_vehicles_regno_active</c>'s
    /// predicate, so this also frees the plate (D-37), and the <c>registry.fleet_vehicles</c> row
    /// goes with it so every fleet-scoped view stops returning the vehicle at once.
    /// </remarks>
    Task<bool> RemoveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one of the org's vehicles to APPROVED or REJECTED — the Verification Officer's
    /// decision, arriving through the internal plane (AL-50, US-13.6).
    /// </summary>
    /// <remarks>
    /// Guarded on fleet membership in the same statement, for
    /// <see cref="SetClassificationAsync"/>'s reason, and on the vehicle not being DEACTIVATED: an
    /// officer approving a vehicle the operator removed while the queue item sat there would put it
    /// back on the road.
    /// </remarks>
    Task<FleetVehicle?> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string status,
        string? rejectionReason,
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

    public async Task<IReadOnlyList<FleetVehicle>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Newest first, because SCR-FP-004's status table is what an operator opens straight after
        // adding a vehicle. `created_at` is the view's, projected from registry.vehicles.
        var rows = await connection.QueryAsync<FleetVehicle>(new CommandDefinition(
            $"""
             SELECT {ViewColumns} FROM registry.fleet_vehicles_fleet
              WHERE fleet_id = @FleetId
              ORDER BY created_at DESC, vehicle_id DESC
              LIMIT @Limit;
             """,
            new { FleetId = fleetId, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FleetVehicle> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid ownerId,
        string registrationNumber,
        string vehicleType,
        string mode,
        string driverName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `mode_b_billing` is left NULL rather than defaulted to 'free'. AL-24 makes NULL "nobody
        // has classified this yet", which is what subscription-svc reads it as (C048: "a NULL is a
        // vehicle onboarded before the setting existed — reading it as Paid would start charging
        // subscribers of a vehicle whose owner never named a price"). The caller sets it through
        // the classification path afterwards, which is where BR-31.1's gate lives.
        //
        // `onboarding_status` is left at its 'incomplete' default and never moved: AL-30 derives it
        // from the four-step Mode C wizard, which a fleet vehicle does not go through. The gate
        // that matters here is AL-50's document slots, and it is evaluated from the documents.
        return await connection.QuerySingleAsync<FleetVehicle>(new CommandDefinition(
            """
            WITH vehicle AS (
              INSERT INTO registry.vehicles
                (owner_id, registration_number, vehicle_type, mode, driver_name)
              VALUES (@OwnerId, @RegistrationNumber, @VehicleType, @Mode, @DriverName)
              RETURNING id, registration_number, vehicle_type, status, dispatch_state,
                        mode_b_billing, default_monthly_fare_minor, driver_name),
                 roster AS (
              INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
              SELECT @FleetId, vehicle.id, @Mode FROM vehicle
              RETURNING fleet_id, vehicle_id, mode)
            SELECT roster.fleet_id, roster.vehicle_id, roster.mode, vehicle.registration_number,
                   vehicle.vehicle_type, vehicle.status, vehicle.dispatch_state,
                   vehicle.mode_b_billing, vehicle.default_monthly_fare_minor, vehicle.driver_name
              FROM roster JOIN vehicle ON vehicle.id = roster.vehicle_id;
            """,
            new
            {
                FleetId = fleetId,
                OwnerId = ownerId,
                RegistrationNumber = registrationNumber,
                VehicleType = vehicleType,
                Mode = mode,
                DriverName = driverName,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> RemoveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The roster row is deleted and the vehicle is deactivated in one statement, guarded on the
        // vehicle actually being this org's. Revoking the open assignments is the caller's — it has
        // to happen in the same transaction, and it is a fact about drivers rather than about the
        // vehicle, so it reads better beside the rest of the cascade.
        var removed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
             WITH removed AS (
               DELETE FROM registry.fleet_vehicles
                WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId
               RETURNING vehicle_id),
                  deactivated AS (
               UPDATE registry.vehicles
                  SET status = '{FleetVehicleStatuses.Deactivated}'
                WHERE id IN (SELECT vehicle_id FROM removed)
               RETURNING id)
             SELECT count(*)::int FROM deactivated;
             """,
            new { FleetId = fleetId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));

        return removed == 1;
    }

    public async Task<FleetVehicle?> SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        string status,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The reason is cleared on an approve rather than left behind: a vehicle that was rejected,
        // had its permit re-uploaded and was then approved must not still carry "route permit
        // expired" on the screen the operator reads (US-2.15's column, US-13.6's screen).
        return await connection.QuerySingleOrDefaultAsync<FleetVehicle>(new CommandDefinition(
            $"""
             UPDATE registry.vehicles v
                SET status = @Status,
                    rejection_reason = @RejectionReason
               FROM registry.fleet_vehicles fv
              WHERE v.id = @VehicleId
                AND fv.vehicle_id = v.id
                AND fv.fleet_id = @FleetId
                AND v.status <> '{FleetVehicleStatuses.Deactivated}'
             RETURNING fv.fleet_id, fv.vehicle_id, fv.mode, v.registration_number, v.vehicle_type,
                       v.status, v.dispatch_state, v.mode_b_billing, v.default_monthly_fare_minor,
                       v.driver_name;
             """,
            new
            {
                FleetId = fleetId,
                VehicleId = vehicleId,
                Status = status,
                RejectionReason = rejectionReason,
            },
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
