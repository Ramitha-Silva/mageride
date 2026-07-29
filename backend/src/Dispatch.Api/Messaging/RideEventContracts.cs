using System.Text.Json;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Messaging;

/// <summary>The <c>ride.events</c> types dispatch-svc reacts to (D6' §2.1/§2.2).</summary>
public static class RideEventTypes
{
    public const string Requested = "ride.requested";
    public const string OfferDeclined = "offer.declined";
    public const string OfferExpired = "offer.expired";
    public const string Accepted = "ride.accepted";
    public const string Completed = "ride.completed";
    public const string Cancelled = "ride.cancelled";

    /// <summary>
    /// US-6A.11's terminal. Emitted by ride-svc when the <c>system-cancel</c> this service sent
    /// lands — or when another replica's did. Consumed so a redelivery retires the deadline that
    /// caused it rather than leaving a live timer against a terminal ride.
    /// </summary>
    public const string ExpiredNoDriver = "ride.expired_no_driver";

    /// <summary>
    /// §11.12's debt statement, riding alongside a cancellation terminal. Consumed into
    /// <c>dispatch.cancellation_penalties</c>, which is where D5' §7.1 has the Rs 50 wait for the
    /// passenger's next completed trip (C035).
    /// </summary>
    public const string PenaltyAccrued = "cancellation.penalty.accrued";

    /// <summary>
    /// A driver did not reach the pickup and the rider gave up (§11.12). US-6A.7's level decrement;
    /// the same idempotent path <c>POST /v1/internal/drivers/{id}/no-show</c> uses (C035).
    /// </summary>
    public const string NoShowDriver = "ride.no_show_driver";
}

/// <summary>The <c>payload</c> member of a <c>ride.events</c> envelope.</summary>
/// <remarks>
/// A read model of ride-svc's <c>RideEventPayload</c>, not a shared type: the two services own
/// their own halves of the wire format, and D6' §2.3's at-least-once contract means this side has
/// to tolerate a producer that has grown a field. Every member is nullable for the same reason —
/// the envelope omits nulls (<c>MageRideJson.StorageOptions</c>), so an absent <c>driverId</c> on
/// a <c>ride.requested</c> is an absent member and not a JSON null.
/// </remarks>
public sealed record RideEventPayload(
    Guid? PassengerId,
    Guid? DriverId,
    Guid? VehicleId,
    string? Kind,
    string? State,
    string? VehicleType,
    string? PaymentMethod,
    long? FareEstimateMinor,
    string? Currency,
    RideEventPoint? Pickup,
    RideEventPoint? Dropoff,
    Guid? OfferId,
    DateTimeOffset? OfferExpiresAt,

    /// <summary>
    /// <c>S</c> | <c>M</c> | <c>L</c> for a <c>kind=package</c> ride — the P-11 compatibility
    /// gate's only input.
    /// </summary>
    /// <remarks>
    /// <b>ride-svc does not produce it yet.</b> <c>rides.rides.package_size</c> exists (migration
    /// 0601) and its CHECK is <c>S|M|L</c>, but C022's <c>RideEventPayload</c> carries <c>kind</c>
    /// and not the size, and only <c>kind: passenger</c> is bookable until C037 lands package
    /// delivery. The member is here rather than waiting for it because the gate it feeds is this
    /// component's deliverable and because the read model has to tolerate a producer that has grown
    /// a field either way (D6' §2.3). Until C037 adds it the gate simply has nothing to reject —
    /// which is a missing input, not a gate that always passes:
    /// <c>candidate_scores.package_size_compatible</c> stays NULL and says so. Raised as a
    /// micro-change-set in the C034 handoff.
    /// </remarks>
    string? PackageSize = null,

    /// <summary>
    /// <c>cancellation.penalty.accrued</c> only — LKR minor units, and for
    /// <see cref="PenaltyBases.FullFare"/> the <em>quoted</em> fare rather than the metered one
    /// (C035, D5' §7.1).
    /// </summary>
    long? AmountMinor = null,

    /// <summary>Which §11.12 rule accrued the debt: <c>cancellation_fee</c> | <c>no_show_fee</c> | <c>full_fare</c>.</summary>
    string? Basis = null,

    /// <summary>Who the money is owed to — the driver whose accepted ride was cancelled (AL-16).</summary>
    Guid? AffectedDriverId = null);

/// <summary>A coordinate as D6' §2.2 renders one.</summary>
public sealed record RideEventPoint(double Lat, double Lng);

/// <summary>The full <c>ride.events</c> envelope.</summary>
public sealed record RideEventEnvelope(
    Guid EventId,
    string EventType,
    Guid RideId,
    long Version,
    DateTimeOffset Ts,
    RideEventPayload? Payload)
{
    /// <summary>Parses one message, or <see langword="null"/> when it is not a usable envelope.</summary>
    public static RideEventEnvelope? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<RideEventEnvelope>(json, MageRideJson.StorageOptions);

            return envelope is null || string.IsNullOrWhiteSpace(envelope.EventType) || envelope.RideId == Guid.Empty
                ? null
                : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The envelope as a dispatch round's input, or <see langword="null"/> when it does not carry
    /// enough to build a candidate set (no pickup, or no tier).
    /// </summary>
    public RideDispatchRequest? ToDispatchRequest()
    {
        if (Payload?.Pickup is not { } pickup ||
            string.IsNullOrWhiteSpace(Payload.VehicleType) ||
            Math.Abs(pickup.Lat) > 90 || Math.Abs(pickup.Lng) > 180)
        {
            return null;
        }

        return new RideDispatchRequest(
            RideId: RideId,
            Pickup: new GeoPoint(pickup.Lat, pickup.Lng),
            VehicleType: Payload.VehicleType,
            PaymentMethod: Payload.PaymentMethod ?? "cash",
            FareEstimateMinor: Payload.FareEstimateMinor,
            Currency: Payload.Currency ?? "LKR",
            PassengerId: Payload.PassengerId,
            Kind: Payload.Kind ?? Domain.RideKinds.Passenger,
            PackageSize: Payload.PackageSize,

            // The DT-02 predicate's second point (Δ C036). A malformed drop-off is dropped rather
            // than rejected: it is not an input to *whether* the ride can be dispatched, only to
            // whether a driver with a Destination Filter is kept in the round, and refusing to
            // dispatch a ride at all over it would be the tail wagging the dog.
            Dropoff: Payload.Dropoff is { } dropoff && Math.Abs(dropoff.Lat) <= 90 && Math.Abs(dropoff.Lng) <= 180
                ? new GeoPoint(dropoff.Lat, dropoff.Lng)
                : null);
    }
}
