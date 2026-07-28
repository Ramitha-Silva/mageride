using System.Collections.Frozen;
using MageRide.Shared.Primitives;

namespace MageRide.Ride.Domain;

/// <summary>
/// <c>rides.rides.kind</c> (C004 migration 0601): 0=passenger, 1=proxy, 2=package.
/// </summary>
/// <remarks>
/// The state machine is kind-agnostic (ADD Appendix B.2 invariant 6), but proxy and package each
/// bring a sub-flow C022 is fenced out of — the P-02 location request and the two OTP gates. Only
/// <see cref="Passenger"/> is bookable here; the other two are <c>400 validation-failed</c> until
/// C032/C037 land the sub-flows, because a package booked with no OTP would fail the
/// <c>ck_rides_package_complete</c> CHECK and a proxy with no rider identity the
/// <c>ck_rides_proxy_identity</c> one.
/// </remarks>
public static class RideKinds
{
    public const string Passenger = "passenger";
    public const string Proxy = "proxy";
    public const string Package = "package";

    public static readonly FrozenSet<string> All =
        new[] { Passenger, Proxy, Package }.ToFrozenSet(StringComparer.Ordinal);

    public static short ToDatabase(string kind) => kind switch
    {
        Passenger => 0,
        Proxy => 1,
        Package => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a rides.rides.kind value."),
    };

    public static string FromDatabase(short kind) => kind switch
    {
        0 => Passenger,
        1 => Proxy,
        2 => Package,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a rides.rides.kind value."),
    };
}

/// <summary>
/// <c>rides.rides.payment_method</c> (C004 migration 0601) — the booking-time choice.
/// </summary>
/// <remarks>
/// Narrower than <c>fares.ride_payments.method</c> on purpose: <c>scan_driver_qr</c> (AL-22) is a
/// settlement choice made after the trip and lives on the payment row, not here (C004 note (f)).
/// </remarks>
public static class RidePaymentMethods
{
    public const string Cash = "cash";
    public const string LankaQr = "lankaqr";
    public const string OnePay = "onepay";
    public const string CashOnDelivery = "cod";

    public static readonly FrozenSet<string> All =
        new[] { Cash, LankaQr, OnePay, CashOnDelivery }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// The Mode C bookable tiers (AL-09, <c>_shared.yaml#RideVehicleType</c>) — the six passenger
/// types plus the two delivery ones. <c>bus</c> and <c>train</c> are Mode A and are never booked.
/// </summary>
public static class RideVehicleTypes
{
    public static readonly FrozenSet<string> Passenger = new[]
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> Delivery = new[]
    {
        "truck", "mini_truck",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsBookable(string? vehicleType) =>
        vehicleType is not null && (Passenger.Contains(vehicleType) || Delivery.Contains(vehicleType));
}

/// <summary>
/// A row of <c>rides.rides</c> — the whole Mode C aggregate as this slice reads it.
/// </summary>
/// <remarks>
/// The package columns (<c>package_size</c>, the two OTP hashes and their attempt counters), the
/// proxy phone hash and <c>dispatch_algorithm_version</c> are omitted: nothing in C022 writes or
/// reads them, and a field this service cannot keep correct is worse than an absent one.
/// </remarks>
public sealed record RideRow(
    Guid Id,
    Guid PassengerId,
    Guid ClientRequestId,
    Guid BookerId,
    Guid? RiderId,
    string? RiderName,
    bool IsProxy,
    short Kind,
    string VehicleType,
    GeoPoint PickupGeo,
    GeoPoint DropoffGeo,
    string State,
    Guid? AcceptedDriverId,
    Guid? AcceptedVehicleId,
    Guid? OfferedDriverId,
    Guid? OfferedVehicleId,
    Guid? CurrentOfferId,
    DateTimeOffset? OfferExpiresAt,
    string PaymentMethod,
    long? FareEstimateMinor,
    long FareSurchargeMinor,
    string Currency,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt)
{
    public string KindName => RideKinds.FromDatabase(Kind);

    /// <summary>
    /// Whether <paramref name="userId"/> may read this ride. The passenger, the booker (who is the
    /// passenger unless the ride is proxy, P-01), the rider, the driver who won it and the driver
    /// currently holding its offer — nobody else (<c>403 not-ride-participant</c>).
    /// </summary>
    public bool IsParticipant(Guid userId) =>
        PassengerId == userId
        || BookerId == userId
        || RiderId == userId
        || AcceptedDriverId == userId
        || OfferedDriverId == userId;
}

/// <summary>
/// The driver-side projection <c>RideDetail.driver</c> carries once a ride is accepted.
/// </summary>
/// <remarks>
/// Read from <c>registry.vehicles</c>, which owns vehicle identity and the driver's display name
/// and photo (C021). A read, never a write — the row belongs to registry-svc. query-svc (C048)
/// owns the read model this anticipates; until it exists the passenger's live-ride screen has
/// nowhere else to learn who is coming.
/// </remarks>
public sealed record RideDriverSummary(
    Guid DriverId,
    string Name,
    string? PhotoUrl,
    string VehicleType,
    string RegistrationNumber);
