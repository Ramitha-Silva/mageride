using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Dispatch.Dispatching;

/// <summary>The <c>dispatch.events</c> types this slice produces (D6' §2.1/§2.2).</summary>
public static class DispatchEventTypes
{
    public const string OfferCreated = "offer.created";

    /// <summary>
    /// DT-04: a Destination Filter has stopped applying — it expired, the driver turned it off, they
    /// went offline, or their EMQX session stayed down past the R-15 grace. The driver is back in the
    /// full eligible pool, and notification-svc tells them so.
    /// </summary>
    public const string DirectionalCleared = "directional.cleared";

    /// <summary>
    /// DT-08 / US-10.14: the 10-minute pre-expiry reminder. Carries the
    /// <c>DIRECTIONAL_EXPIRING</c> push type notification-svc's template registry is keyed by
    /// (D3' notification-svc, D5' §14.4).
    /// </summary>
    public const string DirectionalExpiring = "directional.expiring";
}

/// <summary>
/// The <c>dispatch.events</c> envelope for the two driver-subject Directional Travel events
/// (DT-04, DT-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither event has a ride, and <c>dispatch.events</c> is keyed by <c>rideId</c></b> (D6' §2.1).
/// The outbox row's aggregate id — which is the Kafka partition key — is therefore the
/// <b>driver</b>, which is the aggregate these two are actually about and the ordering that matters:
/// a driver's <c>directional.expiring</c> must not overtake their own <c>directional.cleared</c>.
/// Raised as a micro-change-set in the C036 handoff, because D6' §2.2 prints no schema for either
/// event and its §2.1 partition-key column has no cell for a dispatch event that is not about a
/// ride.
/// </para>
/// <para>
/// <c>usesRemaining</c> rides along on the cleared event so the driver's app can repaint the banner
/// (US-6A.21) from the push alone, without a round trip back to
/// <c>GET /v1/standby/directional</c> — and so a reader can see that a manual turn-off did not give
/// the use back (US-6A.19).
/// </para>
/// </remarks>
public sealed record DirectionalEvent(
    string EventType,
    Guid EventId,
    Guid DriverId,
    Guid FilterId,
    DateTimeOffset Ts,
    DateTimeOffset ExpiresAt,
    string? Reason,
    int? UsesRemaining,
    int? MinutesRemaining,
    string? NotificationType);

/// <summary>
/// The <c>dispatch.events</c> offer envelope, exactly as D6' §2.2 prints it — plus
/// <c>version</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>version</c> is here and not in the spec.</b> C013's note (6) records that the printed
/// envelope carries none, which forces the KMP <c>OfferSession.accept()</c> to spend a
/// <c>GET /v1/rides/{rideId}/state</c> just to learn the number the ADD §11.11 accept demands —
/// a round trip inside a 15-second window, on the one path where latency decides who wins. The
/// C022 handoff asks C023 to put it on. It is the ride version *at the moment the offer was
/// armed*, which is precisely the <c>expectedVersion</c> the accept wants.
/// </para>
/// <para>
/// The fields this service cannot fill are carried at their honest values rather than omitted, so a
/// client written against D6' §2.2 finds every member it expects: <c>isProxy</c> is false and
/// <c>riderName</c>/<c>riderPhoneMasked</c> are absent because the P-05 proxy fields are not on
/// <c>ride.requested</c> (C037 adds them). <c>isPackage</c> and <c>packageSize</c> are C034's: the
/// P-11 gate that decided the driver could carry it puts the size on the offer card, which is what
/// P-11's "drivers still see incoming requests with size + description and can reject" requires.
/// </para>
/// <para>
/// <b><c>directionalMatched</c> is the DT-08 badge</b> (Δ C036). True means this driver had an
/// active Destination Filter and the DT-02 predicate kept them <em>because</em> the ride heads their
/// way — which is what the badge on the incoming-request overlay tells them (D2' §SCR-DA-013,
/// D1' B.1). False on every offer to a driver with no filter: they were never filtered, so nothing
/// matched.
/// </para>
/// </remarks>
public sealed record OfferCreatedEvent(
    string EventType,
    Guid EventId,
    Guid RideId,
    Guid OfferId,
    Guid DriverId,
    Guid VehicleId,
    long Version,
    DateTimeOffset Ts,
    DateTimeOffset ExpiresAt,
    bool IsProxy,
    string? RiderName,
    string? RiderPhoneMasked,
    bool IsPackage,
    string? PackageSize,
    bool DirectionalMatched,
    long? FareEstimateMinor,
    string Currency,
    string PaymentMethod,
    int DistanceToPickupM);

/// <summary>Builds the outbox row for an armed offer.</summary>
public static class DispatchEvents
{
    /// <summary>
    /// R-13: written inside the transaction that armed the offer, so the driver's push cannot
    /// precede the commit — there is no such thing as a phantom offer.
    /// </summary>
    public static OutboxRecord OfferCreated(
        Guid rideId,
        Guid offerId,
        Guid driverId,
        Guid vehicleId,
        long version,
        DateTimeOffset ts,
        DateTimeOffset expiresAt,
        long? fareEstimateMinor,
        string currency,
        string paymentMethod,
        double distanceToPickupM,
        string kind,
        string? packageSize,
        bool directionalMatched)
    {
        var envelope = new OfferCreatedEvent(
            EventType: DispatchEventTypes.OfferCreated,
            EventId: Guid.NewGuid(),
            RideId: rideId,
            OfferId: offerId,
            DriverId: driverId,
            VehicleId: vehicleId,
            Version: version,
            Ts: ts,
            ExpiresAt: expiresAt,
            IsProxy: string.Equals(kind, Domain.RideKinds.Proxy, StringComparison.Ordinal),
            RiderName: null,
            RiderPhoneMasked: null,
            IsPackage: string.Equals(kind, Domain.RideKinds.Package, StringComparison.Ordinal),
            PackageSize: packageSize,
            DirectionalMatched: directionalMatched,
            FareEstimateMinor: fareEstimateMinor,
            Currency: currency,
            PaymentMethod: paymentMethod,
            DistanceToPickupM: (int)Math.Round(distanceToPickupM));

        return OutboxRecord.Create(
            rideId,
            DispatchEventTypes.OfferCreated,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }

    /// <summary>
    /// DT-04: written in the same transaction that marked the filter cleared, so the driver cannot
    /// be told they are back in the pool before the row says they are (the R-13 rule, applied to a
    /// smaller fact).
    /// </summary>
    public static OutboxRecord DirectionalCleared(
        Domain.DirectionalFilterRow filter, string reason, int usesRemaining, DateTimeOffset ts)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var envelope = new DirectionalEvent(
            EventType: DispatchEventTypes.DirectionalCleared,
            EventId: Guid.NewGuid(),
            DriverId: filter.DriverId,
            FilterId: filter.Id,
            Ts: ts,
            ExpiresAt: filter.ExpiresAt,
            Reason: reason,
            UsesRemaining: usesRemaining,
            MinutesRemaining: null,
            NotificationType: null);

        return OutboxRecord.Create(
            filter.DriverId,
            DispatchEventTypes.DirectionalCleared,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }

    /// <summary>
    /// DT-08 / US-10.14's 10-minute warning. dispatch-svc owns the <em>clock</em>; notification-svc
    /// owns the push, its trilingual template and the driver's channel preferences — so this event
    /// is the whole of the hand-off and carries the push type rather than any rendered text.
    /// </summary>
    public static OutboxRecord DirectionalExpiring(
        Domain.DirectionalFilterRow filter, TimeSpan remaining, DateTimeOffset ts)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var envelope = new DirectionalEvent(
            EventType: DispatchEventTypes.DirectionalExpiring,
            EventId: Guid.NewGuid(),
            DriverId: filter.DriverId,
            FilterId: filter.Id,
            Ts: ts,
            ExpiresAt: filter.ExpiresAt,
            Reason: null,
            UsesRemaining: null,

            // Rounded up, because "0 minutes remaining" on a reminder that fired a second early
            // reads as a filter that has already gone.
            MinutesRemaining: (int)Math.Ceiling(Math.Max(0d, remaining.TotalMinutes)),
            NotificationType: DirectionalPushTypes.Expiring);

        return OutboxRecord.Create(
            filter.DriverId,
            DispatchEventTypes.DirectionalExpiring,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }
}

/// <summary>The push types notification-svc keys its templates by (D3' notification-svc, D1' B.5).</summary>
public static class DirectionalPushTypes
{
    /// <summary>US-10.14's 10-minute Directional Travel reminder.</summary>
    public const string Expiring = "DIRECTIONAL_EXPIRING";
}
