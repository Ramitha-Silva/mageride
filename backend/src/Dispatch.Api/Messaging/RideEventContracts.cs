using System.Text.Json;
using MageRide.Dispatch.Dispatching;
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
    DateTimeOffset? OfferExpiresAt);

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
            Currency: Payload.Currency ?? "LKR");
    }
}
