using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Dispatch.Dispatching;

/// <summary>The <c>dispatch.events</c> types this slice produces (D6' §2.1/§2.2).</summary>
public static class DispatchEventTypes
{
    public const string OfferCreated = "offer.created";
}

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
/// The fields this slice cannot fill are carried at their honest values rather than omitted, so a
/// client written against D6' §2.2 finds every member it expects: <c>isProxy</c> and
/// <c>isPackage</c> are false because only <c>kind: passenger</c> is bookable (C022 decision 9),
/// <c>riderName</c>/<c>riderPhoneMasked</c> are the P-05 proxy fields, <c>packageSize</c> is P-06,
/// and <c>directionalMatched</c> is false because no filter can be set until C036.
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
        double distanceToPickupM)
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
            IsProxy: false,
            RiderName: null,
            RiderPhoneMasked: null,
            IsPackage: false,
            PackageSize: null,
            DirectionalMatched: false,
            FareEstimateMinor: fareEstimateMinor,
            Currency: currency,
            PaymentMethod: paymentMethod,
            DistanceToPickupM: (int)Math.Round(distanceToPickupM));

        return OutboxRecord.Create(
            rideId,
            DispatchEventTypes.OfferCreated,
            JsonSerializer.Serialize(envelope, MageRideJson.StorageOptions));
    }
}
