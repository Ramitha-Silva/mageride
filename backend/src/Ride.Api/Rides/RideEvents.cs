using System.Text.Json;
using MageRide.Ride.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Ride.Rides;

/// <summary>
/// The <c>ride.events</c> types this slice emits (D6' §2.2 event registry).
/// </summary>
/// <remarks>
/// D6' §2.2 lists <c>offer.created</c> on <c>ride.events</c> as well as on <c>dispatch.events</c>,
/// and ADD §11.11 writes it to <c>rides.outbox</c> — so the ride-side row is the one that commits
/// with the state change, which is the whole point of R-13. <c>offer.declined</c> is named by
/// §11.12's matrix without a topic; it rides here for the same reason, because ride-svc is what
/// performed the <c>Offered → Matching</c> move dispatch-svc needs to hear about.
/// <para>
/// Not emitted: anything for <c>Requested → Matching</c>. dispatch-svc drives that move itself, so
/// an event would only tell it what it just did, and the registry has no name for one.
/// </para>
/// </remarks>
public static class RideEventTypes
{
    public const string Requested = "ride.requested";
    public const string OfferCreated = "offer.created";
    public const string OfferDeclined = "offer.declined";

    /// <summary>
    /// The 15 s window closed with no answer (D5' §6's <c>Offered | Offer expires 15 s | →Matching
    /// | … | offer.expired</c> row, ADD §11.11's R-04 backstop). Emitted here for the same reason as
    /// <see cref="OfferDeclined"/>: ride-svc is what performed the <c>Offered → Matching</c> move.
    /// </summary>
    public const string OfferExpired = "offer.expired";
    public const string Accepted = "ride.accepted";
    public const string DriverArrived = "ride.driver_arrived";
    public const string Started = "ride.started";
    public const string Completed = "ride.completed";
}

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
    DateTimeOffset? OfferExpiresAt);

/// <summary>The full <c>ride.events</c> envelope (D6' §2.2).</summary>
/// <param name="EventId">Consumers deduplicate on this; delivery is at least once (D6' §2.3).</param>
public sealed record RideEventEnvelope(
    Guid EventId,
    string EventType,
    Guid RideId,
    long Version,
    DateTimeOffset Ts,
    RideEventPayload Payload);

/// <summary>Builds the outbox row for a ride state change.</summary>
public static class RideEvents
{
    /// <summary>
    /// Wraps <paramref name="ride"/> as it stands <em>after</em> the change, so the event's
    /// <c>state</c> and <c>version</c> are the ones a consumer will find if it reads back.
    /// </summary>
    public static OutboxRecord Build(string eventType, RideRow ride, Guid eventId, DateTimeOffset ts)
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
                OfferExpiresAt: ride.OfferExpiresAt));

        // MageRideJson.StorageOptions: camelCase, and nulls are omitted — which is what makes an
        // absent `driverId` on a ride.requested an absent member rather than a claim about one.
        return OutboxRecord.Create(
            ride.Id,
            eventType,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }
}
