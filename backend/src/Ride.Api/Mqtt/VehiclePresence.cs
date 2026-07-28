using System.Text.Json;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Ride.Timers;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Ride.Mqtt;

/// <summary>
/// What a vehicle's presence means to the ride it is carrying (R-15, R-16).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="VehiclePresenceWorker"/>, which owns the broker connection and nothing
/// else. The two facts a last will produces — "start the clock" and "the clock is off" — are
/// database work with no MQTT in them, and keeping them here means the R-16 windows are exercised
/// by tests that need no broker, and that a future caller (dispatch-svc relaying presence over the
/// internal plane, say) reaches the same code rather than a second copy of the policy.
/// </para>
/// <para>
/// <b>A last will starts a clock; it does not cancel a ride.</b> R-16 gives four windows precisely
/// because a driver who goes into an underpass has not abandoned anybody. Only the timer expiring
/// reaches the §11.12 matrix.
/// </para>
/// </remarks>
public interface IVehiclePresence
{
    /// <summary>
    /// The broker published this vehicle's last will. Arms the grace for whatever ride it is on.
    /// </summary>
    /// <returns><see langword="true"/> if a grace was armed by this call.</returns>
    Task<bool> WentOfflineAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The vehicle is back. Retires an unfired grace.</summary>
    /// <returns><see langword="true"/> if a grace was retired by this call.</returns>
    Task<bool> CameOnlineAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehiclePresence"/>
public sealed class VehiclePresence(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRideTimerRepository timers,
    TimeProvider timeProvider,
    ILogger<VehiclePresence> logger) : IVehiclePresence
{
    public async Task<bool> WentOfflineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindBusyByVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken);

        if (ride is null || RideGracePolicy.For(ride.State) is not { } window)
        {
            // The overwhelmingly common case: a driver going off shift with no ride running.
            // Nothing to protect, so nothing to arm.
            await unitOfWork.RollbackAsync(cancellationToken);
            return false;
        }

        var offlineSince = timeProvider.GetUtcNow();

        // Arm-if-absent, not arm: EMQX redelivers a retained `offline` to every replica and again
        // on reconnect, and a second arm would push the deadline forward every time — a broker
        // retrying would forgive a driver who never came back. The first will is the one that
        // counts, the same reason C031 keeps the earliest `offline_since`.
        var armed = await timers.ArmIfAbsentAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            RideTimerKinds.OfflineGrace,
            offlineSince + window,
            JsonSerializer.Serialize(
                new OfflineGracePayload(vehicleId, ride.State, offlineSince), MageRideJson.StorageOptions),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        if (armed is not null)
        {
            logger.LogWarning(
                "Vehicle {VehicleId} went offline on ride {RideId} ({State}); it has {Window} to come back (R-16)",
                vehicleId, ride.Id, ride.State, window);
        }

        return armed is not null;
    }

    public async Task<bool> CameOnlineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindBusyByVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicleId, cancellationToken);

        if (ride is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return false;
        }

        var retired = await timers.RetireAsync(
            unitOfWork.Connection, unitOfWork.Transaction, ride.Id, [RideTimerKinds.OfflineGrace], cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        if (retired > 0)
        {
            logger.LogInformation(
                "Vehicle {VehicleId} is back before the grace expired; ride {RideId} keeps its driver",
                vehicleId, ride.Id);
        }

        return retired > 0;
    }
}
