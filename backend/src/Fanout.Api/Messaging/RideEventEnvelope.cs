using System.Text.Json;
using MageRide.Shared.Http;

namespace MageRide.Fanout.Messaging;

/// <summary>
/// The <c>ride.events</c> types fanout-svc reacts to.
/// </summary>
/// <remarks>
/// A subset. ride-svc publishes twenty-four types on this topic and most of them are somebody
/// else's business — a penalty accrual is fare-svc's, a reputation hit is reputation-svc's. What is
/// read here is what changes something a socket is showing.
/// </remarks>
public static class RideEventTypes
{
    public const string LocationRequestConfirmed = "location.request.confirmed";
    public const string LocationRequestDeclined = "location.request.declined";
    public const string LocationRequestExpired = "location.request.expired";

    public const string PackagePickedUp = "package.picked_up";
    public const string PackageDelivered = "package.delivered";

    /// <summary>Everything <c>location.request.*</c> or <c>package.*</c> is not: a state snapshot.</summary>
    public static bool IsLocationRequest(string eventType) =>
        eventType is LocationRequestConfirmed or LocationRequestDeclined or LocationRequestExpired;

    public static bool IsPackage(string eventType) =>
        eventType is PackagePickedUp or PackageDelivered;
}

/// <summary>
/// The ride states in which a Mode C vehicle is on an active hire (US-7.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the state, not from the event type.</b> ride-svc emits a state snapshot on every
/// transition, so reading the state answers "is this vehicle engaged" for every event including
/// ones this service has never heard of — and a ride that reaches fanout-svc mid-life (a replica
/// that started late, a topic read from an offset) is classified correctly from the first message
/// rather than only from its accept.
/// </para>
/// <para>
/// <b><c>Completed</c> and <c>PaymentPending</c> are not on the list.</b> The passenger is out of
/// the car and dispatch-svc releases the driver on <c>ride.completed</c>; keeping the vehicle hidden
/// until the money settled would take a driver off the public map for as long as a card
/// authorisation takes, while they are already available to be booked.
/// </para>
/// </remarks>
public static class EngagedRideStates
{
    public const string Accepted = "Accepted";
    public const string DriverArrived = "DriverArrived";
    public const string InProgress = "InProgress";

    public static bool Includes(string? state) =>
        state is Accepted or DriverArrived or InProgress;
}

/// <summary>
/// One message off <c>ride.events</c>, decoded only as far as this service needs.
/// </summary>
/// <remarks>
/// <para>
/// The payload is left as a <see cref="JsonElement"/> because the topic carries three different
/// payload shapes: the ride snapshot, <c>LocationRequestPayload</c> (keyed by <c>requestId</c>,
/// because the P-13 round-trip happens before a ride exists) and <c>PackageEventPayload</c>.
/// Deserialising into one record with every member of all three would make a missing field
/// indistinguishable from a field the shape does not have.
/// </para>
/// <para>
/// <b>Unknown members are ignored, not refused.</b> D6' §2.3's contract is that a consumer tolerates
/// a producer that has grown a field; ride-svc has added eight event types since this topic's first
/// consumer was written.
/// </para>
/// </remarks>
public sealed record RideEventEnvelope(
    Guid EventId,
    string EventType,
    Guid? RideId,
    Guid? RequestId,
    long Version,
    DateTimeOffset Ts,
    JsonElement Payload)
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

            return envelope is null || string.IsNullOrWhiteSpace(envelope.EventType) ? null : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A <see cref="Guid"/> member of the payload, or <see langword="null"/>.</summary>
    public Guid? PayloadGuid(string name) =>
        Payload.ValueKind == JsonValueKind.Object
        && Payload.TryGetProperty(name, out var member)
        && member.ValueKind == JsonValueKind.String
        && Guid.TryParse(member.GetString(), out var parsed)
            ? parsed
            : null;

    /// <summary>A string member of the payload, or <see langword="null"/>.</summary>
    public string? PayloadString(string name) =>
        Payload.ValueKind == JsonValueKind.Object
        && Payload.TryGetProperty(name, out var member)
        && member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;

    /// <summary>A <c>{lat,lng}</c> member of the payload, or <see langword="null"/>.</summary>
    public (double Lat, double Lng)? PayloadPoint(string name)
    {
        if (Payload.ValueKind != JsonValueKind.Object
            || !Payload.TryGetProperty(name, out var member)
            || member.ValueKind != JsonValueKind.Object
            || !member.TryGetProperty("lat", out var lat)
            || !member.TryGetProperty("lng", out var lng)
            || lat.ValueKind != JsonValueKind.Number
            || lng.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return (lat.GetDouble(), lng.GetDouble());
    }
}
