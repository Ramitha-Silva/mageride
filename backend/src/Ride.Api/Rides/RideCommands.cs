using MageRide.Ride.Domain;

namespace MageRide.Ride.Rides;

/// <summary>A coordinate plus its optional human address (D3' <c>Place</c>).</summary>
/// <remarks>
/// <c>Address</c> is accepted and dropped: <c>rides.rides</c> stores geography only and
/// <c>Place.address</c> is optional in the contract, so nothing is promised that is not kept.
/// Recorded as contract gap (d) in the C022 handoff.
/// </remarks>
public sealed record RidePlace(double? Lat, double? Lng, string? Address);

/// <summary>The body of <c>POST /v1/rides/request</c>, before validation.</summary>
public sealed record RequestRideCommand(
    Guid PassengerId,
    string? ClientRequestId,
    string? Kind,
    RidePlace? Pickup,
    RidePlace? Dropoff,
    string? VehicleType,
    string? FareEstimateToken,
    string? PaymentMethod,
    DateTimeOffset? ScheduledAt,
    bool? IsProxy,
    string? PackageSize);

/// <param name="Replayed">
/// <see langword="true"/> when <c>(passengerId, clientRequestId)</c> already existed and this is
/// R-18's retry rather than a new booking.
/// </param>
public sealed record RideBooking(RideRow Ride, bool Replayed);

/// <summary>A ride plus the driver projection <c>RideDetail.driver</c> renders once one is assigned.</summary>
public sealed record RideView(RideRow Ride, RideDriverSummary? Driver);

/// <summary>The offer dispatch-svc reserved, as it reaches <c>POST /v1/internal/rides/{id}/offer</c>.</summary>
public sealed record PlaceOfferCommand(
    Guid RideId,
    Guid OfferId,
    Guid DriverId,
    Guid VehicleId,
    int? TtlSeconds,
    long? ExpectedVersion);
