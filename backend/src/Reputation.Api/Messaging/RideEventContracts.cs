using System.Text.Json;
using MageRide.Shared.Http;

namespace MageRide.Reputation.Messaging;

/// <summary>The <c>ride.events</c> types reputation-svc counts (D6' §2.1/§2.2, ADD §11.12).</summary>
/// <remarks>
/// D6' §2.1 lists reputation among <c>ride.events</c>' consumers and never says which types. These
/// six are the ones §11.12's Events column produces that move a counter; every other type on the
/// topic is read and ignored.
/// </remarks>
public static class RideEventTypes
{
    /// <summary>The only thing that resets the AL-16 consecutive run (D5' §7.2).</summary>
    public const string Completed = "ride.completed";

    /// <summary>Every cancellation terminal. The <c>reasonCode</c> decides whether it counts.</summary>
    public const string Cancelled = "ride.cancelled";

    /// <summary>ride-svc's dedicated driver-cancel event, carrying the driver and the from-state.</summary>
    public const string DriverCancelled = "reputation.driver_cancelled";

    public const string NoShowRider = "ride.no_show_rider";
    public const string NoShowDriver = "ride.no_show_driver";
}

/// <summary>The §11.12 reason codes that decide whether a cancellation counts.</summary>
/// <remarks>
/// D5' §7.2 is explicit: the counter moves "on each <b>post-acceptance</b> cancel only
/// (pre-acceptance cancels never count)". <c>RIDER_CANCELLED_BEFORE_ACCEPT</c> is therefore not
/// here, and its absence is the rule.
/// </remarks>
public static class RideReasonCodes
{
    public const string RiderCancelledAfterAccept = "RIDER_CANCELLED_AFTER_ACCEPT";
    public const string RiderCancelledInTrip = "RIDER_CANCELLED_IN_TRIP";
    public const string DriverCancelled = "DRIVER_CANCELLED";
    public const string DriverOfflineGraceExpired = "DRIVER_OFFLINE_GRACE_EXPIRED";

    /// <summary>The rider-side cancels that move the AL-16 run.</summary>
    public static bool IsPostAcceptanceRiderCancel(string? reasonCode) =>
        reasonCode is RiderCancelledAfterAccept or RiderCancelledInTrip;
}

/// <summary>
/// The <c>payload</c> member of a <c>ride.events</c> envelope, as much of it as this service reads.
/// </summary>
/// <remarks>
/// A read model of ride-svc's <c>RideEventPayload</c>, not a shared type — the two services own
/// their halves of the wire format and D6' §2.3's at-least-once contract means this side must
/// tolerate a producer that has grown a field. Every member is nullable because the envelope omits
/// nulls (<c>MageRideJson.StorageOptions</c>): an absent <c>driverId</c> is an absent member, not a
/// JSON null.
/// <para>
/// The union covers two payload shapes. <c>ride.completed</c> and friends carry ride-svc's
/// <c>RideEventPayload</c>; <c>reputation.driver_cancelled</c> carries its
/// <c>RideReputationPayload</c>, which names <c>driverId</c>, <c>fromState</c> and
/// <c>systemInitiated</c> and no pickup. Both fit here, and which fields are present is what tells
/// them apart.
/// </para>
/// </remarks>
public sealed record RideEventPayload(
    Guid? PassengerId,
    Guid? DriverId,
    Guid? VehicleId,
    string? State,
    string? FromState,
    string? ReasonCode,
    string? CancellationReason,
    bool? SystemInitiated);

/// <summary>The full <c>ride.events</c> envelope (D6' §2.2).</summary>
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

            return envelope is null
                   || string.IsNullOrWhiteSpace(envelope.EventType)
                   || envelope.RideId == Guid.Empty
                   || envelope.EventId == Guid.Empty
                ? null
                : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
