using MageRide.Fanout.Realtime;
using MageRide.Fanout.Rides;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Realtime;

namespace MageRide.Fanout.Messaging;

/// <summary>
/// Turns a <c>ride.events</c> envelope into the three things fanout-svc owes it: the participant
/// projection, the US-7.16 engagement mark, and the directed sends.
/// </summary>
public interface IRideEventHandler
{
    Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideEventHandler"/>
/// <remarks>
/// <para>
/// <b>The durable fact is written before the signal is published.</b> A replica that joins after the
/// signal has gone must still see an engaged vehicle as engaged, and a passenger who reconnects
/// after a revocation must still be outside the group. Redis first, channel second, in every branch.
/// </para>
/// <para>
/// <b>Every branch is idempotent.</b> D6' §2.3 makes delivery at-least-once and this service keeps
/// no dedupe table: the projection write is a whole-hash overwrite, the engagement mark is a
/// <c>SET</c>/<c>DEL</c>, and a re-sent <c>RideStateChanged</c> carries the version that tells a
/// client it has already seen it.
/// </para>
/// </remarks>
public sealed class RideEventHandler(
    IRideProjection rides,
    IVisibilityIndex visibility,
    IFanoutControlPlane control,
    ILogger<RideEventHandler> logger) : IRideEventHandler
{
    public async Task HandleAsync(RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (RideEventTypes.IsLocationRequest(envelope.EventType))
        {
            await ResolveLocationRequestAsync(envelope, cancellationToken);
            return;
        }

        if (envelope.RideId is not { } rideId)
        {
            // Neither a ride nor a request. Nothing on a socket is about it.
            return;
        }

        if (RideEventTypes.IsPackage(envelope.EventType))
        {
            await PublishPackageAsync(rideId, envelope, cancellationToken);
            return;
        }

        await ProjectAsync(rideId, envelope, cancellationToken);
    }

    /// <summary>
    /// Records who is on the ride, moves the engagement mark, and tells the ride group.
    /// </summary>
    private async Task ProjectAsync(Guid rideId, RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var state = envelope.PayloadString("state");

        if (envelope.PayloadGuid("passengerId") is not { } passengerId || state is null)
        {
            // A sibling event — a penalty, a settlement — whose payload is not a ride snapshot.
            // Those carry their own shapes and none of them changes who may watch the ride.
            return;
        }

        var vehicleId = envelope.PayloadGuid("vehicleId");

        var participants = new RideParticipants(
            rideId,
            passengerId,
            envelope.PayloadGuid("bookerId") ?? passengerId,
            envelope.PayloadGuid("riderId"),
            envelope.PayloadGuid("driverId"),
            vehicleId,
            state);

        await rides.WriteAsync(participants, cancellationToken);

        if (vehicleId is { } vehicle)
        {
            await ApplyEngagementAsync(vehicle, rideId, state, cancellationToken);
        }

        await control.PublishAsync(
            new FanoutSignal(
                FanoutSignalKinds.RideStateChanged,
                RideId: rideId,
                RideState: new RideStateChangedEvent(
                    rideId,
                    state,
                    envelope.Version,
                    participants.DriverId is { } driverId
                        ? new RideDriverSummary(driverId, vehicleId, envelope.PayloadString("vehicleType"))
                        : null)),
            cancellationToken);
    }

    /// <summary>US-7.16: on hire, off the public map; terminal, back on it.</summary>
    private async Task ApplyEngagementAsync(
        Guid vehicleId, Guid rideId, string state, CancellationToken cancellationToken)
    {
        if (EngagedRideStates.Includes(state))
        {
            await visibility.EngageAsync(vehicleId, rideId, cancellationToken);

            logger.LogDebug(
                "Vehicle {VehicleId} is engaged on ride {RideId} ({State}); off the public map",
                vehicleId, rideId, state);

            return;
        }

        // Every other state releases it, including the ones that never engaged it. Releasing a
        // vehicle that was not engaged is a no-op; the alternative — a list of terminal states —
        // would have to be kept in step with ride-svc's eighteen and would strand a vehicle the day
        // one was added.
        //
        // The ride id is passed because the release is conditional on it: `ride.events` is
        // partitioned by ride, so an expired offer for one ride can arrive after another ride's
        // accept, and an unconditional delete there would put an occupied taxi back on the map.
        await visibility.ReleaseAsync(vehicleId, rideId, cancellationToken);
    }

    /// <summary>P-13's round-trip resolving, to the booker's own group.</summary>
    private async Task ResolveLocationRequestAsync(
        RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var requestId = envelope.RequestId ?? envelope.PayloadGuid("requestId");

        if (requestId is not { } id || envelope.PayloadGuid("bookerId") is not { } bookerId)
        {
            logger.LogWarning("A {EventType} carried no request or booker; nothing to fan out", envelope.EventType);
            return;
        }

        // The state is taken from the event type rather than from the payload's `state`, because the
        // three the socket contract names are exactly these three and ride-svc's column carries more
        // (`RiderNotRegistered`, `Pending`), which are not resolutions.
        var state = envelope.EventType switch
        {
            RideEventTypes.LocationRequestConfirmed => LocationRequestStates.Confirmed,
            RideEventTypes.LocationRequestDeclined => LocationRequestStates.Declined,
            _ => LocationRequestStates.Expired,
        };

        // P-02's fence, restated where the payload is built: only a confirmation carries a position,
        // and a decline has none to carry.
        var geo = state == LocationRequestStates.Confirmed && envelope.PayloadPoint("geo") is { } point
            ? new LiveGeoPoint(point.Lat, point.Lng)
            : null;

        await control.PublishAsync(
            new FanoutSignal(
                FanoutSignalKinds.LocationRequestResolved,
                BookerId: bookerId,
                LocationRequest: new LocationRequestResolvedEvent(id, state, geo)),
            cancellationToken);
    }

    /// <summary>US-20.7's handoff progress, to the ride group.</summary>
    private async Task PublishPackageAsync(
        Guid rideId, RideEventEnvelope envelope, CancellationToken cancellationToken)
    {
        // ride-svc renders the status from the ride's own state (`PickupPending`, `PickedUp`,
        // `InTransit`, `Delivered`), which is the set the contract names; falling back to the event
        // type covers a producer that omitted it.
        var status = envelope.PayloadString("packageStatus")
            ?? (envelope.EventType == RideEventTypes.PackagePickedUp ? "PickedUp" : "Delivered");

        await control.PublishAsync(
            new FanoutSignal(
                FanoutSignalKinds.PackageStatus,
                RideId: rideId,
                Package: new PackageStatusEvent(rideId, status)),
            cancellationToken);
    }
}
