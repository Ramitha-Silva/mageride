using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>registry.fleet_assignments</c> — who may drive which of the org's vehicles, and until when
/// (US-13.2, US-13.8, US-13.9, AL-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads go through <c>registry.fleet_assignments_fleet</c> (migration 1807), writes go to the
/// base table.</b> The view carries the driver's name and the plate, which live in <c>iam.users</c>
/// and <c>registry.vehicles</c> — two tables the fleet reader holds no privilege on at all. Writes
/// need neither and run as the service's own login role.
/// </para>
/// <para>
/// <b>What this repository does not do is as important as what it does.</b> Nothing here sweeps
/// expired assignments, closes them, or marks them anything. US-13.9's "auto-expires" is
/// <c>registry.driver_eligible_vehicles</c> (migration 0314) evaluating a window at read time: the
/// row is untouched and simply stops being returned, which is what makes "without manual action"
/// true rather than merely fast. A sweep would be a second mechanism that could lag, fail or be
/// switched off, and the driver would keep the bus for as long as it did.
/// </para>
/// </remarks>
public interface IFleetAssignmentRepository
{
    /// <summary>The org's assignments, newest first; one vehicle's when <paramref name="vehicleId"/> is given.</summary>
    Task<IReadOnlyList<FleetAssignment>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid? vehicleId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>One assignment of the org's, or <see langword="null"/>.</summary>
    Task<FleetAssignment?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid assignmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a time-bounded assignment.
    /// </summary>
    /// <remarks>
    /// The overlap rule is <c>ex_fleet_assign_overlap</c>'s and is deliberately not re-checked
    /// here: a <c>SELECT</c> then an <c>INSERT</c> loses the race between two managers assigning at
    /// once, and the exclusion constraint does not. The caller turns <c>23P01</c> into the 409.
    /// </remarks>
    Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        Guid driverId,
        DateTimeOffset validFrom,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends an assignment now (US-13.8). Returns <see langword="false"/> when it was already ended.
    /// </summary>
    /// <remarks>
    /// <c>revoked_at = now()</c> rather than a delete: SCR-FP-005 shows assignment history, and the
    /// question "who was driving on the 14th" is answered from the rows that ended. Not idempotent
    /// on purpose — a second revoke returns false and the endpoint answers 404, because re-revoking
    /// an assignment that ended a week ago is a client working from a stale list.
    /// </remarks>
    Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid assignmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends every open assignment on a vehicle, for the US-13.7 removal cascade.
    /// </summary>
    /// <remarks>
    /// In the same transaction as the removal, or a driver keeps the right to start a session on a
    /// vehicle that has left the fleet — the exact leak US-13.7's "immediately removes it from the
    /// fleet and passenger maps" is about.
    /// </remarks>
    Task<int> RevokeAllForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The drivers whose assignment covers <paramref name="at"/> on this vehicle (US-13.11b).
    /// </summary>
    /// <remarks>
    /// Usually one. Two when a shift changes over the departure instant, and both are returned
    /// rather than one chosen: the alarm is "this bus has not left", and the operator's answer is
    /// whichever driver is standing next to it.
    /// </remarks>
    Task<IReadOnlyList<Guid>> DriversCoveringAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetAssignmentRepository"/>
internal sealed class FleetAssignmentRepository : IFleetAssignmentRepository
{
    private const string ViewColumns = """
        id, fleet_id, vehicle_id, driver_id, assigned_at, valid_from, expires_at, revoked_at,
        driver_name, driver_phone, registration_number, is_active
        """;

    public async Task<IReadOnlyList<FleetAssignment>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid? vehicleId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Active first, then by when the assignment was written. SCR-FP-005 is a working screen —
        // the rows an operator acts on are the live ones — and history sits underneath it.
        var rows = await connection.QueryAsync<FleetAssignment>(new CommandDefinition(
            $"""
             SELECT {ViewColumns} FROM registry.fleet_assignments_fleet
              WHERE fleet_id = @FleetId
                AND (@VehicleId::uuid IS NULL OR vehicle_id = @VehicleId)
              ORDER BY is_active DESC, assigned_at DESC, id DESC
              LIMIT @Limit;
             """,
            new { FleetId = fleetId, VehicleId = vehicleId, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FleetAssignment?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<FleetAssignment>(new CommandDefinition(
            $"""
             SELECT {ViewColumns} FROM registry.fleet_assignments_fleet
              WHERE fleet_id = @FleetId AND id = @AssignmentId;
             """,
            new { FleetId = fleetId, AssignmentId = assignmentId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        Guid driverId,
        DateTimeOffset validFrom,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The vehicle is confirmed to be this org's in the INSERT itself, not before it: checking
        // membership first and inserting afterwards would leave a window in which a vehicle removed
        // from the fleet still gained a driver. `SELECT ... WHERE EXISTS` inserts nothing when the
        // roster row is absent, and the caller reads the empty result as vehicle-not-found.
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO registry.fleet_assignments (fleet_id, vehicle_id, driver_id, valid_from, expires_at)
            SELECT @FleetId, @VehicleId, @DriverId, @ValidFrom, @ExpiresAt
             WHERE EXISTS (SELECT 1 FROM registry.fleet_vehicles
                            WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId)
            RETURNING id;
            """,
            new
            {
                FleetId = fleetId,
                VehicleId = vehicleId,
                DriverId = driverId,
                ValidFrom = validFrom,
                ExpiresAt = expiresAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var revoked = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.fleet_assignments
               SET revoked_at = now()
             WHERE id = @AssignmentId AND fleet_id = @FleetId AND revoked_at IS NULL;
            """,
            new { FleetId = fleetId, AssignmentId = assignmentId },
            transaction,
            cancellationToken: cancellationToken));

        return revoked == 1;
    }

    public Task<int> RevokeAllForVehicleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.fleet_assignments
               SET revoked_at = now()
             WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId AND revoked_at IS NULL;
            """,
            new { FleetId = fleetId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> DriversCoveringAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var drivers = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT driver_id
              FROM registry.fleet_assignments
             WHERE vehicle_id = @VehicleId
               AND revoked_at IS NULL
               AND valid_from <= @At
               AND (expires_at IS NULL OR expires_at > @At);
            """,
            new { VehicleId = vehicleId, At = at },
            transaction,
            cancellationToken: cancellationToken));

        return [.. drivers];
    }
}
