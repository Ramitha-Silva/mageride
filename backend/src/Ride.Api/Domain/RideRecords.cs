using System.Collections.Frozen;
using MageRide.Shared.Primitives;

namespace MageRide.Ride.Domain;

/// <summary>
/// <c>rides.rides.kind</c> (C004 migration 0601): 0=passenger, 1=proxy, 2=package.
/// </summary>
/// <remarks>
/// The state machine is kind-agnostic (ADD Appendix B.2 invariant 6): all three traverse the same
/// eighteen states. What differs is the sub-flow each brings — <see cref="Proxy"/> the P-02
/// location-request round-trip and the <c>booker_id ≠ rider</c> invariant, <see cref="Package"/> the
/// two OTP gates (P-07) and the COD terminal (P-08). Both landed in C037; C022's fence, which made
/// them <c>400 validation-failed</c>, is gone.
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
/// <para>
/// <c>dispatch_algorithm_version</c> (R-11) is omitted: it is dispatch-svc's audit of which scoring
/// formula produced the offer and nothing here writes or reads it. The <b>OTP hashes</b> are
/// omitted too, deliberately — see <see cref="PickupOtpAttempts"/>.
/// </para>
/// <para>
/// Δ C037 added the proxy and package columns. <c>rider_phone_hash</c> is present because P-03 makes
/// it the only handle an unregistered rider has, and both attempt counters because they are what the
/// driver's app renders as "3 tries left".
/// </para>
/// </remarks>
/// <param name="PickupOtpAttempts">
/// How many wrong pickup codes this delivery has absorbed (P-07's budget of five). The
/// <b>hashes themselves are not on this record</b> and no query in this service selects them: a
/// digest that never leaves Postgres cannot be logged, serialised into an event or returned by a
/// read — the comparison happens inside the conditional <c>UPDATE</c> that consumes the attempt.
/// </param>
/// <param name="RecipientPhone">
/// The package recipient's E.164 number, in the clear (migration 0609). AL-21 SMSes it and AL-33
/// dials it, so unlike <paramref name="RiderPhoneHash"/> it cannot be a digest.
/// </param>
public sealed record RideRow(
    Guid Id,
    Guid PassengerId,
    Guid ClientRequestId,
    Guid BookerId,
    Guid? RiderId,
    byte[]? RiderPhoneHash,
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
    string? PackageSize,
    string? PackageDescription,
    string? RecipientName,
    string? RecipientPhone,
    short PickupOtpAttempts,
    short DeliveryOtpAttempts,
    long? FareEstimateMinor,
    long FareSurchargeMinor,
    string Currency,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt)
{
    public string KindName => RideKinds.FromDatabase(Kind);

    public bool IsPackage => Kind == RideKinds.ToDatabase(RideKinds.Package);

    /// <summary>Whether the fare is collected as cash on delivery (P-08).</summary>
    public bool IsCashOnDelivery =>
        IsPackage && string.Equals(PaymentMethod, RidePaymentMethods.CashOnDelivery, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="userId"/> may read this ride. The passenger, the booker (who is the
    /// passenger unless the ride is proxy, P-01), the rider, the driver who won it and the driver
    /// currently holding its offer — nobody else (<c>403 not-ride-participant</c>).
    /// </summary>
    /// <remarks>
    /// A package's <b>recipient is not a participant</b>, even when the number belongs to an
    /// account. AL-21 gives them the AL-44 <c>package_recipient</c> share token instead, which is
    /// scope-shaped and TTL-bounded — a recipient who could read the ride aggregate would also see
    /// the sender's payment method and the whole transition log.
    /// </remarks>
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
