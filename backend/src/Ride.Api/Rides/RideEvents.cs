using System.Text.Json;
using MageRide.Ride.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Ride.Rides;

/// <summary>A coordinate as D6' §2.2 renders one.</summary>
public sealed record EventGeoPoint(double Lat, double Lng);

/// <summary>The <c>payload</c> member of a <c>ride.events</c> envelope (D6' §2.2).</summary>
/// <remarks>
/// Carries more than the spec's illustrative example: <c>vehicleType</c>, <c>paymentMethod</c> and
/// <c>fareEstimateMinor</c> are here because dispatch-svc has to build a candidate set for the
/// right tier and fill in the D6' <c>offer.created</c> push, and <c>ride.requested</c> is the only
/// message it gets. Consumers ignore what they do not read.
/// </remarks>
public sealed record RideEventPayload(
    Guid PassengerId,
    Guid BookerId,
    Guid? RiderId,
    Guid? DriverId,
    Guid? VehicleId,
    string Kind,
    bool IsProxy,
    string State,
    string VehicleType,
    string PaymentMethod,
    long? FareEstimateMinor,
    string Currency,
    EventGeoPoint Pickup,
    EventGeoPoint Dropoff,
    Guid? OfferId,
    DateTimeOffset? OfferExpiresAt,

    /// <summary>
    /// The server-owned §11.12 reason (<c>RIDER_CANCELLED_AFTER_ACCEPT</c>, …). Present on the
    /// terminal events; absent on the lifecycle ones, which have no reason beyond the move itself.
    /// </summary>
    string? ReasonCode = null,

    /// <summary>
    /// What the client said when it asked (<c>RIDER_CHANGED_MIND</c> | <c>DRIVER_TOO_FAR</c> |
    /// <c>EMERGENCY</c> | <c>OTHER</c>). Recorded and published because reputation-svc and support
    /// both care about it, and decided nothing — the matrix did.
    /// </summary>
    string? CancellationReason = null);

/// <summary>The full <c>ride.events</c> envelope (D6' §2.2).</summary>
/// <param name="EventId">Consumers deduplicate on this; delivery is at least once (D6' §2.3).</param>
public sealed record RideEventEnvelope(
    Guid EventId,
    string EventType,
    Guid RideId,
    long Version,
    DateTimeOffset Ts,
    RideEventPayload Payload);

/// <summary>
/// The <c>cancellation.penalty.accrued</c> payload (§11.12, D5' §7.1, D-05).
/// </summary>
/// <param name="AmountMinor">
/// LKR minor units. For <see cref="RidePenaltyBasis.FullFare"/> this is the <em>quoted</em> fare —
/// the only number ride-svc holds. fare-svc replaces it with the metered amount when it settles;
/// <paramref name="Basis"/> is what tells it to.
/// </param>
/// <param name="AffectedDriverId">
/// Who the money is owed to. D5' §7.1 credits the driver whose accepted ride was cancelled, paid
/// through the passenger's next trip.
/// </param>
/// <param name="DriverCompensationBasis">
/// How the driver's side is computed when the matrix names one — §11.12's "driver compensation =
/// base fare/2" on a rider no-show. The base fare is per tier (D5' §1.1) and is fare-svc's, so the
/// rule travels rather than a number.
/// </param>
public sealed record RidePenaltyPayload(
    Guid PassengerId,
    Guid? AffectedDriverId,
    long AmountMinor,
    string Currency,
    string Basis,
    string ReasonCode,
    string FromState,
    string SettledOn,
    string? DriverCompensationBasis);

/// <summary>The <c>reputation.driver_cancelled</c> payload (§11.12).</summary>
/// <param name="SystemInitiated">
/// <see langword="true"/> when the last-will grace expired rather than the driver tapping Cancel.
/// §11.12 gives both rows the same effect ("same"), and reputation-svc still wants to be able to
/// tell a driver who quit from a driver whose phone died.
/// </param>
public sealed record RideReputationPayload(
    Guid DriverId,
    Guid? VehicleId,
    Guid PassengerId,
    string FromState,
    string ToState,
    string ReasonCode,
    bool SystemInitiated);

/// <summary>The <c>ride.settled</c> payload (R-05).</summary>
public sealed record RideSettlementPayload(
    Guid PassengerId,
    Guid? DriverId,
    Guid? VehicleId,
    Guid PaymentId,
    string PaymentState,
    string State,
    long? SettledMinor,
    string Currency,
    bool EarningPayable);

/// <summary>Builds the outbox row for a ride state change.</summary>
public static class RideEvents
{
    /// <summary>
    /// Wraps <paramref name="ride"/> as it stands <em>after</em> the change, so the event's
    /// <c>state</c> and <c>version</c> are the ones a consumer will find if it reads back.
    /// </summary>
    public static OutboxRecord Build(
        string eventType,
        RideRow ride,
        Guid eventId,
        DateTimeOffset ts,
        string? reasonCode = null,
        string? cancellationReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(ride);

        var envelope = new RideEventEnvelope(
            EventId: eventId,
            EventType: eventType,
            RideId: ride.Id,
            Version: ride.Version,
            Ts: ts,
            Payload: new RideEventPayload(
                PassengerId: ride.PassengerId,
                BookerId: ride.BookerId,
                RiderId: ride.RiderId,
                DriverId: ride.AcceptedDriverId ?? ride.OfferedDriverId,
                VehicleId: ride.AcceptedVehicleId ?? ride.OfferedVehicleId,
                Kind: ride.KindName,
                IsProxy: ride.IsProxy,
                State: ride.State,
                VehicleType: ride.VehicleType,
                PaymentMethod: ride.PaymentMethod,
                FareEstimateMinor: ride.FareEstimateMinor,
                Currency: ride.Currency,
                Pickup: new EventGeoPoint(ride.PickupGeo.Latitude, ride.PickupGeo.Longitude),
                Dropoff: new EventGeoPoint(ride.DropoffGeo.Latitude, ride.DropoffGeo.Longitude),
                OfferId: ride.CurrentOfferId,
                OfferExpiresAt: ride.OfferExpiresAt,
                ReasonCode: reasonCode,
                CancellationReason: cancellationReason));

        // MageRideJson.StorageOptions: camelCase, and nulls are omitted — which is what makes an
        // absent `driverId` on a ride.requested an absent member rather than a claim about one.
        return OutboxRecord.Create(
            ride.Id,
            eventType,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }

    /// <summary>The <c>cancellation.penalty.accrued</c> row that rides alongside a §11.12 terminal.</summary>
    public static OutboxRecord BuildPenalty(
        RideRow ride, RidePenaltyPayload payload, Guid eventId, DateTimeOffset ts) =>
        BuildSibling(ride, RideEventTypes.PenaltyAccrued, payload, eventId, ts);

    /// <summary>The <c>reputation.driver_cancelled</c> row reputation-svc (C033) counts.</summary>
    public static OutboxRecord BuildReputation(
        RideRow ride, RideReputationPayload payload, Guid eventId, DateTimeOffset ts) =>
        BuildSibling(ride, RideEventTypes.DriverCancelled, payload, eventId, ts);

    /// <summary>The <c>ride.settled</c> row that authorises the driver's earning (R-05).</summary>
    public static OutboxRecord BuildSettlement(
        RideRow ride, RideSettlementPayload payload, Guid eventId, DateTimeOffset ts) =>
        BuildSibling(ride, RideEventTypes.Settled, payload, eventId, ts);

    /// <summary>
    /// An event about the ride that is not a state snapshot of it — the penalty, the reputation hit
    /// and the settlement.
    /// </summary>
    /// <remarks>
    /// Same envelope shape, same <c>aggregate_id</c>, so every one of them is keyed by
    /// <c>rideId</c> on <c>ride.events</c> (D6' §2.1) and reaches a consumer in the order the
    /// transaction wrote it. A separate topic would let a penalty overtake the cancellation that
    /// caused it.
    /// </remarks>
    private static OutboxRecord BuildSibling<TPayload>(
        RideRow ride, string eventType, TPayload payload, Guid eventId, DateTimeOffset ts)
    {
        ArgumentNullException.ThrowIfNull(ride);
        ArgumentNullException.ThrowIfNull(payload);

        var envelope = new
        {
            eventId,
            eventType,
            rideId = ride.Id,
            version = ride.Version,
            ts,
            payload,
        };

        return OutboxRecord.Create(
            ride.Id, eventType, JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }
}
