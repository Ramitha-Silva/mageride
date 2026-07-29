using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Levels;
using MageRide.Dispatch.Penalties;
using Microsoft.Extensions.Logging;

namespace MageRide.Dispatch.Messaging;

/// <summary>
/// Turns a <c>ride.events</c> envelope into the dispatch action it implies.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the Kafka loop so the whole reaction table is testable without a broker, and so
/// C024's SignalR fan-out and C034's fuller matrix have one place to extend.
/// </para>
/// <para>
/// <b>No dedupe table.</b> D6' §2.3 makes delivery at-least-once and says consumers key on
/// <c>eventId</c>; this consumer instead makes every action idempotent by construction — the
/// commands it issues to ride-svc carry deterministic <c>Idempotency-Key</c>s, and every write it
/// makes is a conditional <c>UPDATE</c> guarded on the status it expects. A redelivered
/// <c>ride.accepted</c> settles nothing twice; a redelivered <c>ride.requested</c> is answered 409
/// by ride-svc and dispatches against a ride that is already Offered, which the offer route
/// refuses. Recorded in <c>Dispatch.Api/CLAUDE.md</c> — C034 should revisit if it adds an action
/// that is not naturally idempotent.
/// </para>
/// </remarks>
public interface IRideEventHandler
{
    Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideEventHandler"/>
public sealed class RideEventHandler(
    IDispatchService dispatch,
    IDriverLevelService levels,
    IPenaltyService penalties,
    ILogger<RideEventHandler> logger) : IRideEventHandler
{
    public async Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.EventType)
        {
            case RideEventTypes.Requested:
                await BeginAsync(envelope, cancellationToken);
                break;

            // Both are "the driver is free again and the ride wants another candidate" (§11.12 and
            // ADD §11.11's R-04 paragraph). Neither envelope names the driver or the offer —
            // ride-svc clears both columns before building the payload — so the release is keyed
            // by ride and resolved against dispatch.offers.
            case RideEventTypes.OfferDeclined:
                await ReleaseAndRetryAsync(envelope, OfferStatuses.Declined, cancellationToken);
                break;

            case RideEventTypes.OfferExpired:
                await ReleaseAndRetryAsync(envelope, OfferStatuses.Expired, cancellationToken);
                break;

            case RideEventTypes.Accepted when envelope.Payload?.DriverId is { } acceptedBy:
                await dispatch.MarkAcceptedAsync(envelope.RideId, acceptedBy, cancellationToken);
                break;

            // ADD §9.4: on a terminal event the driver's availability is restored — decrement
            // currentRideId, re-add to the GEO index, release the lock. Skipping any of these
            // leaves a "ghost-busy" driver, which is what the R-20 stuck-state alert catches.
            //
            // The US-6A.11 deadline is retired with it, and unconditionally: a terminal ride with a
            // live 120-second timer behind it would have the sweep try to system-cancel a ride that
            // is already over, once per poll, until the lease expired.
            case RideEventTypes.Completed or RideEventTypes.Cancelled:
                await dispatch.RetireRideAsync(envelope.RideId, cancellationToken);

                if (envelope.Payload?.DriverId is { } driverId)
                {
                    await dispatch.ReturnToPoolAsync(driverId, cancellationToken);
                }

                break;

            case RideEventTypes.ExpiredNoDriver:
                await dispatch.RetireRideAsync(envelope.RideId, cancellationToken);
                break;

            // D5' §7.1's accrual. Idempotent by ux_penalty_accrual(original_ride_id, basis)
            // (migration 0713), which is what absorbs the redelivery D6' §2.3 guarantees.
            case RideEventTypes.PenaltyAccrued:
                await AccrueAsync(envelope, cancellationToken);
                break;

            // US-6A.7. The same path POST /v1/internal/drivers/{id}/no-show takes, and idempotent
            // on (driver, ride) — so a redelivery, or the internal route being called for the same
            // ride as well, takes one level and not two.
            //
            // `reputation.driver_cancelled` is deliberately NOT consumed alongside it. §11.12 gives
            // a driver cancellation a reputation hit and a brief delist, both of which are
            // reputation-svc's and both of which it already applies; no spec gives it a level or a
            // point cost. `level_config.cancellation_penalty_points` exists because the contract's
            // LevelConfig names it, and nothing here reads it — see Dispatch.Api/CLAUDE.md.
            case RideEventTypes.NoShowDriver when envelope.Payload?.DriverId is { } absentDriver:
                await levels.RecordNoShowAsync(absentDriver, envelope.RideId, cancellationToken);
                break;

            default:
                logger.LogDebug("Ignoring {EventType} on ride {RideId}", envelope.EventType, envelope.RideId);
                break;
        }
    }

    private async Task BeginAsync(RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.ToDispatchRequest() is not { } request)
        {
            // A ride.requested with no pickup or no tier cannot be dispatched by anybody. Logged
            // and dropped rather than retried: replaying it would produce the same nothing forever
            // and block the partition, which is the failure D6' §2.3's DLQ exists for (C034).
            logger.LogError(
                "ride.requested for ride {RideId} carries no usable pickup or vehicleType; dropping it",
                envelope.RideId);
            return;
        }

        var outcome = await dispatch.BeginAsync(request, cancellationToken);

        logger.LogInformation("ride.requested for {RideId} → {Result}", envelope.RideId, outcome.Result);
    }

    private async Task AccrueAsync(RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        // A penalty with nobody to pay it to is not this table's row: D5' §7.1 credits the driver
        // whose accepted ride was cancelled, and `affected_driver_id` is NOT NULL for that reason.
        // ride-svc omits it on the rows of §11.12 that have no driver, which are also the rows with
        // no penalty — so this is a defensive read, not a case that fires.
        if (envelope.Payload is not { AffectedDriverId: { } driverId, PassengerId: { } passengerId } payload ||
            payload.AmountMinor is not { } amountMinor ||
            payload.Basis is not { Length: > 0 } basis)
        {
            logger.LogWarning(
                "cancellation.penalty.accrued for ride {RideId} is missing the passenger, the driver, the amount " +
                "or the basis; not recording it",
                envelope.RideId);

            return;
        }

        await penalties.AccrueAsync(
            new PenaltyAccrual(passengerId, envelope.RideId, driverId, amountMinor, basis), cancellationToken);
    }

    private async Task ReleaseAndRetryAsync(
        RideEventEnvelope envelope, string toStatus, CancellationToken cancellationToken)
    {
        await dispatch.ReleaseLiveOfferAsync(envelope.RideId, toStatus, cancellationToken);

        if (envelope.ToDispatchRequest() is not { } request)
        {
            logger.LogWarning(
                "{EventType} for ride {RideId} carries no pickup; the ride stays in Matching with no offer",
                envelope.EventType, envelope.RideId);
            return;
        }

        var outcome = await dispatch.DispatchAsync(request, cancellationToken);

        logger.LogInformation(
            "{EventType} for {RideId} → next round {Result}", envelope.EventType, envelope.RideId, outcome.Result);
    }
}
