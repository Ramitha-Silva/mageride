using System.Text.Json;
using MageRide.Ride.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Ride.Rides;

/// <summary>A coordinate as D6' §2.2 renders one.</summary>
public sealed record EventGeoPoint(double Lat, double Lng);

/// <summary>The <c>payload</c> member of a <c>ride.events</c> envelope (D6' §2.2).</summary>
/// <remarks>
/// <para>
/// Carries more than the spec's illustrative example: <c>vehicleType</c>, <c>paymentMethod</c> and
/// <c>fareEstimateMinor</c> are here because dispatch-svc has to build a candidate set for the
/// right tier and fill in the D6' <c>offer.created</c> push, and <c>ride.requested</c> is the only
/// message it gets. Consumers ignore what they do not read.
/// </para>
/// <para>
/// <b><c>bookerId</c> and <c>riderId</c> are the notification fan-out (P-05, Δ C037).</b> On a proxy
/// booking they are two different people and <b>both</b> are told about every state change — the
/// booker because they arranged the ride and are paying for it, the rider because they are in the
/// car. They are the same person on a passenger booking, which is what makes one payload serve
/// both. What must never be inverted is who the <em>driver</em> reaches: <c>counterpartyPhone</c> on
/// <c>RideDetail</c> gives the driver the rider's number and never the booker's, and no event
/// carries the booker's.
/// </para>
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
    string? CancellationReason = null,

    /// <summary>
    /// <c>S</c> | <c>M</c> | <c>L</c> on a package booking (P-06). Δ C037, and the field
    /// dispatch-svc's P-11 gate has been waiting for: <c>dispatch.candidate_scores</c> already has
    /// <c>package_size_compatible</c> and C034's read model already parses this member, which until
    /// now no producer filled (the C034 handoff's open gap).
    /// </summary>
    string? PackageSize = null,

    /// <summary>
    /// What the sender said is in the parcel. On the offer it is what lets a driver exercise the
    /// autonomy P-11 keeps for them — the compatibility table narrows the round, the description is
    /// how a driver decides they still do not want it.
    /// </summary>
    string? PackageDescription = null,

    /// <summary>
    /// The third party a proxy booking is for (P-01). Present so a notification reads "your ride"
    /// to the rider and "the ride you booked for X" to the booker without a second lookup.
    /// </summary>
    string? RiderName = null);

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

/// <summary>
/// The payload of every <c>location.request.*</c> event (ADD §11.15, P-02/P-13).
/// </summary>
/// <param name="RequestId">
/// The public handle. fanout-svc's group is <c>booker:{bookerId}:loc-req:{requestId}</c>, so the
/// pair below is the whole address of the socket this event has to reach (P-13).
/// </param>
/// <param name="RiderPhone">
/// <b>Present on <c>location.request.issued</c> only</b>, and the one place an unhashed number
/// appears in this service's events. AL-45 makes notification-svc mint a <c>pickup_confirm</c> token
/// and <em>SMS</em> it to an unregistered rider, and an SMS cannot be addressed to a digest — the
/// number travels in the message and is stored nowhere but the outbox row that carries it (P-03
/// hashes the number <em>at rest</em>, which is <c>rides.location_requests.rider_phone_hash</c>).
/// </param>
/// <param name="Geo">
/// <b><c>location.request.confirmed</c> only.</b> A decline and an expiry carry <see langword="null"/>
/// and there is no code path that could fill it — P-02's fence, in the type.
/// </param>
/// <param name="ExpiresAt">When the 300 s window closes, so the booker's UI can count down.</param>
public sealed record LocationRequestPayload(
    Guid RequestId,
    Guid BookerId,
    Guid? RiderId,
    string? RiderPhone,
    string State,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    EventGeoPoint? Geo = null,
    double? AccuracyM = null);

/// <summary>
/// The payload of <c>package.picked_up</c>, <c>package.delivered</c> and
/// <c>package.otp_locked</c> (ADD §11.16, AL-21, AL-33).
/// </summary>
/// <param name="DeliveryOtp">
/// <b><c>package.picked_up</c> only</b>, and the second and last time the delivery code leaves the
/// server: ADD §11.16 hands it to the recipient at pickup, by FCM to a registered one and by SMS to
/// an unregistered one. Nothing else may carry it — least of all <c>package.delivered</c>, by which
/// point it has been spent.
/// </param>
/// <param name="RecipientPhone">
/// AL-21's branch input: notification-svc decides between the FCM deep link and the
/// <c>safety.trip_share_tokens</c> SMS from whether this number belongs to an account.
/// </param>
/// <param name="ProofArtifactId">
/// <c>package.delivered</c> by photo (P-10). Its presence is what makes the AL-44 receipt say
/// <c>photo_proof</c> instead of <c>otp_verified</c>, which D4' notes is derived and not stored.
/// </param>
/// <param name="Gate">
/// <c>package.otp_locked</c> only: which of the two budgets was exhausted (<c>pickup</c> |
/// <c>delivery</c>). The admin queue needs to know which end of the delivery is stuck.
/// </param>
public sealed record PackageEventPayload(
    Guid PassengerId,
    Guid? DriverId,
    Guid? VehicleId,
    string State,
    string PackageStatus,
    string? PackageSize,
    string? PackageDescription,
    string? RecipientName,
    string? RecipientPhone,
    string PaymentMethod,
    string? DeliveryOtp = null,
    Guid? ProofArtifactId = null,
    string? Gate = null,
    int? Attempts = null);

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
                CancellationReason: cancellationReason,
                PackageSize: ride.PackageSize,
                PackageDescription: ride.PackageDescription,
                RiderName: ride.RiderName));

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
    /// A <c>package.*</c> row (Δ C037), riding alongside the <c>ride.*</c> state snapshot of the
    /// same transaction.
    /// </summary>
    /// <remarks>
    /// Two events, not one, on the pickup and the delivery. The <c>ride.started</c> /
    /// <c>ride.completed</c> pair is the aggregate's own — dispatch-svc releases the driver on
    /// <c>ride.completed</c> and would leave them ghost-busy if a package's completion were spelled
    /// differently — while <c>package.picked_up</c> / <c>package.delivered</c> are the domain events
    /// ADD §11.16 names and the ones AL-21's recipient notification hangs off. Same
    /// <c>aggregate_id</c>, so they reach a consumer in the order the transaction wrote them.
    /// </remarks>
    public static OutboxRecord BuildPackage(
        RideRow ride, string eventType, PackageEventPayload payload, Guid eventId, DateTimeOffset ts) =>
        BuildSibling(ride, eventType, payload, eventId, ts);

    /// <summary>
    /// A <c>location.request.*</c> row (Δ C037), keyed by the <b>request</b> rather than by a ride.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The round-trip happens <em>before</em> a ride exists — that is the point of it, and why
    /// <c>rides.location_requests.ride_id</c> is nullable (migration 0606). So the aggregate these
    /// events are about is the request, and <c>requestId</c> is the Kafka partition key: what has to
    /// stay ordered is one request's issue → confirm/decline/expire, and a booker with several in
    /// flight has no ordering between them to preserve.
    /// </para>
    /// <para>
    /// They ride <c>ride.events</c> because ride-svc has one outbox and D6' §2.1 gives it one topic.
    /// A consumer keyed on rides ignores them by <c>eventType</c>, which dispatch-svc's handler
    /// already does for everything it does not recognise.
    /// </para>
    /// </remarks>
    public static OutboxRecord BuildLocationRequest(
        string eventType, LocationRequestPayload payload, Guid eventId, DateTimeOffset ts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        var envelope = new
        {
            eventId,
            eventType,
            requestId = payload.RequestId,
            ts,
            payload,
        };

        return OutboxRecord.Create(
            payload.RequestId, eventType, JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }

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
