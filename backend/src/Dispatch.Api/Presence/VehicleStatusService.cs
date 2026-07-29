using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Presence;

/// <summary>
/// What an EMQX last will means to dispatch-svc (R-15).
/// </summary>
/// <remarks>
/// <para>
/// ADD's R-15 row reads "<c>veh/{vehicleId}/status=offline</c> event → <c>dispatch-svc</c> releases
/// active offer / starts grace timer per ride state". Both halves are here, and the grace is what
/// makes them one behaviour rather than two: <b>an <c>offline</c> starts a clock; only the clock
/// expiring releases anything</b>. A driver who goes through an underpass mid-offer has not
/// declined it, and their <c>online</c> retires the timer before it fires.
/// </para>
/// <para>
/// <b>An ON_RIDE driver is not this service's business.</b> §11.12 gives ride-svc four last-will
/// graces on an accepted ride — 60 s after accept, 120 s after arrive, 5 min in progress, 10 min at
/// payment — each ending in a cancellation with a reputation hit. dispatch-svc releasing that
/// driver's presence would take them out of the pool for a ride nobody has cancelled yet, and would
/// race ride-svc's own <c>offline_grace</c> row. So the state is checked before anything is armed.
/// </para>
/// <para>
/// <b>Not a shared subscription, and idempotent by index.</b> Presence is rare and every replica
/// takes the whole topic; <c>ux_dispatch_timers_driver_live</c> (migration 0711) is what makes two
/// replicas arming the same grace a single row. Same shape and same reasoning as ride-svc's
/// <c>VehiclePresenceWorker</c>.
/// </para>
/// </remarks>
public interface IVehicleStatusService
{
    /// <summary>The broker published this vehicle's last will. Starts the R-15 grace.</summary>
    /// <returns><see langword="true"/> when a grace was armed.</returns>
    Task<bool> WentOfflineAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The device reconnected. Retires the grace before it can fire.</summary>
    Task<bool> CameOnlineAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleStatusService"/>
public sealed class VehicleStatusService(
    INpgsqlConnectionFactory connectionFactory,
    IPresenceRepository presence,
    IDispatchTimerRepository dispatchTimers,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<VehicleStatusService> logger) : IVehicleStatusService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<bool> WentOfflineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await presence.FindByVehicleAsync(connection, vehicleId, cancellationToken);

        if (row is null || row.State is PresenceStates.Offline or PresenceStates.OnRide)
        {
            // Nothing on standby under this vehicle, already off, or mid-ride — the last three
            // being ride-svc's §11.12 graces and not dispatch's. Most last wills on this topic are
            // Mode A/B vehicles that have no presence row at all.
            return false;
        }

        var fireAt = timeProvider.GetUtcNow().Add(_options.OfferReleaseGrace);

        await dispatchTimers.ArmDriverTimerAsync(
            connection, null, row.DriverId, DispatchTimerKinds.OfferReleaseGrace, fireAt,
            payload: null, cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId}'s session dropped while driver {DriverId} was {State}; their offer is released " +
            "at {FireAt:O} unless they come back (R-15)",
            vehicleId, row.DriverId, row.State, fireAt);

        return true;
    }

    public async Task<bool> CameOnlineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var row = await presence.FindByVehicleAsync(connection, vehicleId, cancellationToken);

        if (row is null)
        {
            return false;
        }

        // Retiring an armed grace is the whole of "came back". Presence itself is not restored
        // here: a broker session is not a declaration that the driver wants rides, and
        // `POST /v1/standby/online` is the one signal that means that.
        await dispatchTimers.RetireForDriverAsync(
            connection, null, row.DriverId, DispatchTimerKinds.OfferReleaseGrace, cancellationToken);

        return true;
    }
}
