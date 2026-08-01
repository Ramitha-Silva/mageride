using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.AdminBff.Persistence;

/// <summary>
/// The tables a suspension touches: <c>registry.vehicles</c>, <c>iam.users</c>,
/// <c>trips.sessions</c>, <c>dispatch.driver_presence</c> and <c>iam.sessions</c> (US-14.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Five tables in one transaction, and every one of them is somebody else's.</b> That is the
/// exception this component makes and it is bounded: a suspension has to be atomic — a vehicle
/// marked un-dispatchable while its tracking session is still live is still on the passenger's map —
/// and there is no service that owns all five. The platform's usual answer, "forward to the owner",
/// has nothing to forward to: registry-svc's <c>/v1/internal/vehicles/**</c> plane carries the
/// OnePay merchant bind and the onboarding recompute, and no spec gives it a suspend route.
/// Recorded as a boundary decision in the C062 handoff rather than left to be discovered.
/// </para>
/// <para>
/// <b>Suspension is <c>dispatch_state</c>, not <c>status</c>.</b> Migration 0303 already carries
/// exactly this distinction: <c>DISPATCH_SUSPENDED</c> is E-03's "documents lapsed, do not offer
/// rides to it" and is what dispatch-svc's candidate query excludes (C035), while
/// <c>status = 'DEACTIVATED'</c> is the end of a registration — which is what US-12.6's third
/// confirmed report reaches, and safety-svc reaches it. An admin suspending a vehicle for a week is
/// making the first statement, not the second: retiring the registration would burn the plate under
/// D-37's live-set uniqueness and make reinstatement a re-registration.
/// </para>
/// </remarks>
public interface IModerationRepository
{
    Task<AdminVehicle?> LockVehicleAsync(IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken);

    Task SetDispatchStateAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, string dispatchState, CancellationToken cancellationToken);

    /// <summary>The driver's row, or null when the id is not an account at all.</summary>
    Task<(Guid Id, string? FirstName, bool IsBlocked)?> LockDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, CancellationToken cancellationToken);

    Task SetBlockedAsync(IUnitOfWork unitOfWork, Guid driverId, bool blocked, CancellationToken cancellationToken);

    /// <summary>Ends live Mode A/B tracking sessions. Returns how many were ended.</summary>
    Task<int> EndSessionsAsync(
        IUnitOfWork unitOfWork, Guid? vehicleId, Guid? driverId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Takes the driver out of the dispatcher's candidate set immediately (R-08).</summary>
    Task<int> GoOfflineAsync(
        IUnitOfWork unitOfWork, Guid? vehicleId, Guid? driverId, CancellationToken cancellationToken);

    /// <summary>Revokes the driver's live app session, so the handset is signed out (AL-08).</summary>
    Task<int> RevokeDriverSessionsAsync(
        IUnitOfWork unitOfWork, Guid driverId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IModerationRepository"/>
internal sealed class ModerationRepository : IModerationRepository
{
    private const string VehicleColumns =
        """
        id                  AS Id,
        owner_id            AS OwnerId,
        registration_number AS RegistrationNumber,
        vehicle_type        AS VehicleType,
        mode                AS Mode,
        status              AS Status,
        dispatch_state      AS DispatchState,
        driver_name         AS DriverName
        """;

    public async Task<AdminVehicle?> LockVehicleAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<AdminVehicle>(new CommandDefinition(
            $"SELECT {VehicleColumns} FROM registry.vehicles WHERE id = @Id FOR UPDATE;",
            new { Id = vehicleId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task SetDispatchStateAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, string dispatchState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE registry.vehicles SET dispatch_state = @State WHERE id = @Id;",
            new { Id = vehicleId, State = dispatchState },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<(Guid Id, string? FirstName, bool IsBlocked)?> LockDriverAsync(
        IUnitOfWork unitOfWork, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<(Guid Id, string? FirstName, bool IsBlocked)>(
            new CommandDefinition(
                "SELECT id, first_name, is_blocked FROM iam.users WHERE id = @Id FOR UPDATE;",
                new { Id = driverId },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));

        return row.Id == Guid.Empty ? null : row;
    }

    public Task SetBlockedAsync(
        IUnitOfWork unitOfWork, Guid driverId, bool blocked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE iam.users SET is_blocked = @Blocked WHERE id = @Id;",
            new { Id = driverId, Blocked = blocked },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>end_reason = 'admin'</c> is one of the four values migration 0501's CHECK admits, and it
    /// is there for this: an operator can tell an admin-ended session from an idle timeout without
    /// joining the audit log.
    /// </remarks>
    public Task<int> EndSessionsAsync(
        IUnitOfWork unitOfWork,
        Guid? vehicleId,
        Guid? driverId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE trips.sessions
               SET state = 'COMPLETED', ended_at = @Now, end_reason = 'admin'
             WHERE state = 'ACTIVE'
               AND (@VehicleId::uuid IS NULL OR vehicle_id = @VehicleId)
               AND (@DriverId::uuid  IS NULL OR driver_id  = @DriverId);
            """,
            new { VehicleId = vehicleId, DriverId = driverId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// The durable half of presence only (R-08's Redis hash expires on its own 60 s TTL). Writing
    /// <c>OFFLINE</c> here is what removes the driver from the candidate query's partial index in
    /// the same transaction as the suspension — the cache going stale for under a minute is a
    /// smaller window than waiting for a heartbeat that will not come.
    /// </remarks>
    public Task<int> GoOfflineAsync(
        IUnitOfWork unitOfWork, Guid? vehicleId, Guid? driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dispatch.driver_presence
               SET state = 'OFFLINE'
             WHERE state <> 'OFFLINE'
               AND (@VehicleId::uuid IS NULL OR vehicle_id = @VehicleId)
               AND (@DriverId::uuid  IS NULL OR driver_id  = @DriverId);
            """,
            new { VehicleId = vehicleId, DriverId = driverId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// The <c>driver</c> app only. A suspended driver who also rides as a passenger keeps that
    /// session: US-14.3 suspends an operator, not a person's ability to book a taxi, and AL-08's
    /// per-app session model is what makes the distinction expressible.
    /// </remarks>
    public Task<int> RevokeDriverSessionsAsync(
        IUnitOfWork unitOfWork, Guid driverId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE iam.sessions
               SET revoked_at = @Now
             WHERE user_id = @DriverId AND app = 'driver' AND revoked_at IS NULL;
            """,
            new { DriverId = driverId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}
