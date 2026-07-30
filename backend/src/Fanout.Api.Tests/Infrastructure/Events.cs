namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>
/// The envelopes ride-svc and registry-svc put on their topics, spelled here so a test can produce
/// one without standing either service up.
/// </summary>
/// <remarks>
/// <b>These shapes are the contract, and copying them is the risk.</b> ride-svc's
/// <c>RideEventEnvelope</c>/<c>RideEventPayload</c> (C022/C037) and registry-svc's
/// <c>ShareEvents</c> (C028) are the producers; neither service may be referenced from here, and a
/// field renamed on either side would leave this suite green. What the shapes are checked against
/// is the field names in each producer's own tests plus C118's contract suite — and the members used
/// here are the small, spec-named subset (<c>passengerId</c>, <c>vehicleId</c>, <c>state</c>,
/// <c>passengerId</c> on a share) that D6' §2.2 and the C028 handoff both print.
/// </remarks>
internal static class Events
{
    /// <summary>A <c>ride.events</c> state snapshot (D6' §2.2).</summary>
    public static object Ride(
        Guid rideId,
        string eventType,
        string state,
        Guid passengerId,
        Guid? driverId = null,
        Guid? vehicleId = null,
        Guid? bookerId = null,
        Guid? riderId = null,
        long version = 1) =>
        new
        {
            eventId = Guid.NewGuid(),
            eventType,
            rideId,
            version,
            ts = DateTimeOffset.UtcNow,
            payload = new
            {
                passengerId,
                bookerId = bookerId ?? passengerId,
                riderId,
                driverId,
                vehicleId,
                kind = "passenger",
                isProxy = bookerId is not null && bookerId != passengerId,
                state,
                vehicleType = "three_wheeler",
                paymentMethod = "cash",
                currency = "LKR",
                pickup = new { lat = 6.9344, lng = 79.8428 },
                dropoff = new { lat = 6.8514, lng = 79.8653 },
            },
        };

    /// <summary>A <c>location.request.*</c> envelope — keyed by the request, not by a ride (P-13).</summary>
    public static object LocationRequest(
        Guid requestId, Guid bookerId, string eventType, double? lat = null, double? lng = null) =>
        new
        {
            eventId = Guid.NewGuid(),
            eventType,
            requestId,
            ts = DateTimeOffset.UtcNow,
            payload = new
            {
                requestId,
                bookerId,
                riderId = (Guid?)null,
                state = "Confirmed",
                issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(4),
                geo = lat is { } latitude && lng is { } longitude ? new { lat = latitude, lng = longitude } : null,
            },
        };

    /// <summary>A <c>package.*</c> envelope (ADD §11.16, US-20.7).</summary>
    public static object Package(Guid rideId, string eventType, string packageStatus, Guid passengerId) =>
        new
        {
            eventId = Guid.NewGuid(),
            eventType,
            rideId,
            version = 5,
            ts = DateTimeOffset.UtcNow,
            payload = new
            {
                passengerId,
                state = "InProgress",
                packageStatus,
                paymentMethod = "cash",
            },
        };

    /// <summary>
    /// A <c>registry.events</c> share payload (C028, D-22/D-23).
    /// </summary>
    /// <remarks>
    /// <b>Flat, with no envelope around it</b>, because that is what the outbox dispatcher puts on
    /// the wire: it publishes the payload column verbatim and carries the type in an
    /// <c>eventType</c> Kafka header. ride-svc's rows look like envelopes only because
    /// <c>RideEvents.Build</c> serialises one into that column.
    /// </remarks>
    public static object Share(Guid vehicleId, Guid passengerId) =>
        new
        {
            grantId = Guid.NewGuid(),
            vehicleId,

            // The field D6' §5.1 omits and §5.2 needs — registry-svc adds it deliberately, and
            // without it a directed RemoveFromGroupAsync has nobody to address.
            passengerId,
            reason = "revoked",
            revokedAt = DateTimeOffset.UtcNow,
        };
}
