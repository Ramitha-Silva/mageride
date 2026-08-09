using MageRide.Shared.Primitives;

namespace MageRide.E2E.Infrastructure;

/// <summary>A passenger account, as iam-svc would have left it.</summary>
internal sealed record Passenger(Guid Id, string Phone, string Bearer);

/// <summary>
/// A driver plus the one APPROVED Mode C vehicle they are live on (US-9.6) — what registry-svc's
/// <c>POST /v1/vehicles</c> and the dev approve path produce (C021).
/// </summary>
internal sealed record Driver(Guid DriverId, Guid VehicleId, string Plate, string Phone, string Bearer);

/// <summary>
/// A ride in flight, with everything a scenario needs to act on it.
/// </summary>
/// <param name="Version">
/// What the *next* mutation must echo. Every ride-svc mutation carries the version the client last
/// saw (D3' ride-svc header), and a scenario that let it drift would be testing 409 handling
/// instead of what it came for.
/// </param>
internal sealed record LiveRide(
    Guid RideId,
    Passenger Passenger,
    Driver Driver,
    long Version,
    GeoPoint Pickup,
    GeoPoint Dropoff)
{
    /// <summary>The pickup OTP a package booking showed its sender, once (P-07).</summary>
    public string? PickupOtp { get; init; }
}

/// <summary>The slice of <c>rides.rides</c> a scenario asserts against.</summary>
internal sealed record RideSnapshot(
    string State,
    long Version,
    Guid? CurrentOfferId,
    Guid? OfferedDriverId,
    Guid? AcceptedDriverId,
    Guid? AcceptedVehicleId,
    DateTimeOffset? OfferExpiresAt,
    DateTimeOffset? TerminalAt);

/// <summary>
/// What <c>POST /v1/fare/calculate</c> produced.
/// </summary>
/// <param name="AmountMinor">What the passenger owes — the trip plus any D-05 debt settled onto it.</param>
/// <param name="TripMinor">
/// The trip alone, recomputed from the returned breakdown with fare-svc's own D5' §1.1 formula.
/// The difference between the two is what the cross-trip settlement collected.
/// </param>
internal sealed record FinalFare(Guid PaymentId, long AmountMinor, long TripMinor);

/// <summary>A <c>dispatch.offers</c> row, as a scenario reads it back.</summary>
internal sealed record OfferSnapshot(
    Guid Id, Guid DriverId, string Status, DateTimeOffset SentAt, DateTimeOffset ExpiresAt);

/// <summary>A <c>dispatch.cancellation_penalties</c> row — the passenger's outstanding balance (D-05).</summary>
internal sealed record PenaltySnapshot(
    Guid Id, long AmountMinor, string Basis, string Status, Guid OriginalRideId, Guid? AppliedRideId);

/// <summary>A <c>rides.timers</c> row, as a scenario reads it back (R-04).</summary>
internal sealed record RideTimerSnapshot(Guid Id, string Kind, DateTimeOffset FireAt, DateTimeOffset? FiredAt);
