using Dapper;
using MageRide.Provisioning.Domain;
using Npgsql;

namespace MageRide.Provisioning.Persistence;

/// <summary>
/// The read-only window onto registry-svc's schema a bind needs.
/// </summary>
/// <remarks>
/// <para>
/// provisioning-svc has to answer "does this vehicle exist, who owns it and which fleet's roster
/// carries it" before it mints anything, and the answer lives in <c>registry.vehicles</c> and
/// <c>registry.fleet_vehicles</c>. <b>Reads only, and never a write.</b> Vehicle lifecycle is
/// registry-svc's (C028/C029); a service that wrote here would be a second author of a state
/// machine it does not own.
/// </para>
/// <para>
/// A synchronous read of another context's tables rather than a cached projection because a bind
/// is a rare, human-driven operation and staleness here would mint a credential for a vehicle
/// that was deactivated a moment ago. The hot path — <c>validate</c>, once per device connect —
/// touches none of this.
/// </para>
/// </remarks>
public interface IVehicleLookupRepository
{
    Task<VehicleReference?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a registration number within one fleet's roster — how a bulk CSV row names its
    /// vehicle (D3': rows are <c>imei,registrationNumber</c>).
    /// </summary>
    Task<VehicleReference?> FindInFleetByRegistrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        string registrationNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the caller may onboard trackers for this fleet: its owner account, or a member
    /// holding <c>owner</c> or <c>manager</c> (AL-03).
    /// </summary>
    /// <remarks>
    /// <c>viewer</c> is deliberately excluded. AL-03 ranks the sub-roles and a viewer's is
    /// read-only; bulk-binding 5,000 trackers is the largest write the Fleet Portal can make.
    /// </remarks>
    Task<bool> IsFleetPrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid userId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleLookupRepository"/>
public sealed class VehicleLookupRepository : IVehicleLookupRepository
{
    private const string Columns =
        """
        v.id AS "Id", v.owner_id AS "OwnerId", v.registration_number AS "RegistrationNumber",
        v.status AS "Status", fv.fleet_id AS "FleetId"
        """;

    public Task<VehicleReference?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QueryFirstOrDefaultAsync<VehicleReference>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM registry.vehicles v
               LEFT JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
              WHERE v.id = @VehicleId
              LIMIT 1;
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<VehicleReference?> FindInFleetByRegistrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        string registrationNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Scoped to the fleet's roster, not to the whole platform: a CSV that names a plate the
        // fleet does not operate must fail that row, and a global lookup would happily bind a
        // tracker to a stranger's vehicle because the operator mistyped a letter.
        return connection.QueryFirstOrDefaultAsync<VehicleReference>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM registry.vehicles v
               JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
              WHERE fv.fleet_id = @FleetId AND v.registration_number = @RegistrationNumber
              LIMIT 1;
             """,
            new { FleetId = fleetId, RegistrationNumber = registrationNumber },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> IsFleetPrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM registry.fleets WHERE id = @FleetId AND owner_id = @UserId)
                OR EXISTS (SELECT 1 FROM iam.fleet_members
                            WHERE fleet_id = @FleetId AND user_id = @UserId
                              AND fleet_role IN ('owner', 'manager'));
            """,
            new { FleetId = fleetId, UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
