using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Persistence;

/// <summary>
/// Trains — Mode A <c>registry.vehicles</c> rows nobody but an admin may create (US-2.17/2.18).
/// </summary>
/// <remarks>
/// <para>
/// <b>Admin-only is enforced by there being nowhere else to do it.</b> AL-09 puts <c>train</c> in
/// the canonical vehicle-type enum but the Driver App's and Fleet Portal's own enums exclude it
/// (registry-svc C029, fleet-svc C058 both refuse <c>train</c>), so the only path that can insert
/// one is this file. That is the whole of D3's "train admin-only" — a rule about which surfaces
/// exist rather than a check somebody could forget.
/// </para>
/// <para>
/// <b>A train has an owner, because <c>registry.vehicles.owner_id</c> is <c>NOT NULL</c> and
/// references a real account.</b> The admin who registers it is recorded as the owner: it is the
/// only account that exists at that moment, it is the one the audit row already names, and a
/// synthetic "platform" user would be an account with credentials nobody holds and a row every
/// directory in C064 would have to special-case. The operator relationship a rail company actually
/// has is a fleet organisation (AL-03), which is a later, separate act.
/// </para>
/// <para>
/// <b>Retirement is <c>status = 'DEACTIVATED'</c> and never a delete.</b> Historical trips reference
/// the vehicle (<c>trips.sessions.vehicle_id</c>) and D-37's uniqueness index deliberately covers
/// only <c>PENDING</c>/<c>APPROVED</c>, so a retired train releases its number for a successor
/// while every past journey still resolves.
/// </para>
/// </remarks>
public interface ITrainRepository
{
    Task<Train?> ReadAsync(IUnitOfWork unitOfWork, Guid trainId, CancellationToken cancellationToken);

    /// <summary>Registers a train. Null when the number is already live (D-37).</summary>
    Task<Train?> InsertAsync(
        IUnitOfWork unitOfWork,
        Guid trainId,
        Guid ownerId,
        string name,
        string trainNumber,
        Guid? routeId,
        bool active,
        CancellationToken cancellationToken);

    Task<Train> UpdateAsync(
        IUnitOfWork unitOfWork,
        Guid trainId,
        string name,
        string trainNumber,
        Guid? routeId,
        bool active,
        CancellationToken cancellationToken);

    Task RetireAsync(IUnitOfWork unitOfWork, Guid trainId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrainRepository"/>
internal sealed class TrainRepository : ITrainRepository
{
    /// <summary>AL-09's type, and R-01's mode: a train is public transport or it is not a train.</summary>
    private const string TrainType = "train";
    private const string PublicMode = "A";

    /// <summary>
    /// <c>default_route_id</c> is the line the train is <em>registered for</em> (migration 1409),
    /// which is not the same question as the line a journey ran.
    /// </summary>
    /// <remarks>
    /// D4' §4 puts <c>route_id</c> on <c>trips.sessions</c> because a bus is reassigned between
    /// routes and a column on the vehicle would be wrong for every past journey. A train is the
    /// case that argument does not cover — US-2.17 registers it against a line before it has ever
    /// run — so 1409 adds the second column and nothing derives one from the other.
    /// </remarks>
    private const string TrainColumns =
        """
        v.id                    AS TrainId,
        v.driver_name           AS Name,
        v.registration_number   AS TrainNumber,
        v.default_route_id      AS RouteId,
        (v.status = 'APPROVED') AS Active
        """;

    public async Task<Train?> ReadAsync(
        IUnitOfWork unitOfWork, Guid trainId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<Train>(new CommandDefinition(
            $"""
             SELECT {TrainColumns}
               FROM registry.vehicles v
              WHERE v.id = @Id AND v.vehicle_type = '{TrainType}';
             """,
            new { Id = trainId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<Train?> InsertAsync(
        IUnitOfWork unitOfWork,
        Guid trainId,
        Guid ownerId,
        string name,
        string trainNumber,
        Guid? routeId,
        bool active,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var rows = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             INSERT INTO registry.vehicles
               (id, owner_id, registration_number, vehicle_type, mode, status, driver_name,
                onboarding_status, default_route_id)
             VALUES
               (@Id, @OwnerId, @TrainNumber, '{TrainType}', '{PublicMode}', @Status, @Name,
                'approved', @RouteId)
             ON CONFLICT DO NOTHING;
             """,
            new
            {
                Id = trainId,
                OwnerId = ownerId,
                TrainNumber = trainNumber,
                Name = name,
                RouteId = routeId,
                Status = active ? "APPROVED" : "DEACTIVATED",
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        // `ON CONFLICT DO NOTHING` covers ux_vehicles_regno_active (D-37) as well as the primary
        // key: zero rows means the number is already carried by a live registration, which is a 409
        // rather than a 500 with a constraint name in it.
        if (rows == 0)
        {
            return null;
        }

        return await ReadAsync(unitOfWork, trainId, cancellationToken);
    }

    public async Task<Train> UpdateAsync(
        IUnitOfWork unitOfWork,
        Guid trainId,
        string name,
        string trainNumber,
        Guid? routeId,
        bool active,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE registry.vehicles
                SET registration_number = @TrainNumber,
                    driver_name         = @Name,
                    default_route_id    = @RouteId,
                    status              = @Status
              WHERE id = @Id AND vehicle_type = '{TrainType}';
             """,
            new
            {
                Id = trainId,
                TrainNumber = trainNumber,
                Name = name,
                RouteId = routeId,
                Status = active ? "APPROVED" : "DEACTIVATED",
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return (await ReadAsync(unitOfWork, trainId, cancellationToken))!;
    }

    public Task RetireAsync(IUnitOfWork unitOfWork, Guid trainId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE registry.vehicles
                SET status = 'DEACTIVATED', dispatch_state = 'DISPATCH_SUSPENDED'
              WHERE id = @Id AND vehicle_type = '{TrainType}';
             """,
            new { Id = trainId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}
