using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
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
public sealed class RideEventHandler(IDispatchService dispatch, ILogger<RideEventHandler> logger) : IRideEventHandler
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
