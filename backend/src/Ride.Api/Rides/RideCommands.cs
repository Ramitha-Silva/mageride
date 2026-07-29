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
    string? RiderName,
    string? RiderPhone,
    string? PackageSize,
    string? PackageDescription,
    string? RecipientName,
    string? RecipientPhone);

/// <summary>
/// The body of <c>POST /v1/internal/rides/scheduled</c> (Δ C035) — dispatch-svc's T-30 min
/// materialisation of a <c>dispatch.scheduled_rides</c> row.
/// </summary>
/// <param name="ScheduledRideId">
/// The scheduled booking's id, used verbatim as the ride's <c>clientRequestId</c>. That is what
/// makes the call idempotent under R-18 without a second key: a scheduler that retried after a
/// timeout gets the ride its first attempt created.
/// </param>
/// <remarks>
/// No <c>fareEstimateToken</c>: a quote taken when the passenger booked yesterday is not the price
/// of the ride that is about to run (D5' §1.4), and the caller is a trusted service rather than a
/// client that could be quoting itself a discount. The ride therefore carries no
/// <c>fare_estimate_minor</c> and fare-svc meters it.
/// </remarks>
public sealed record MaterialiseScheduledRideCommand(
    Guid ScheduledRideId,
    Guid PassengerId,
    RidePlace? Pickup,
    RidePlace? Dropoff,
    string? VehicleType,
    string? PaymentMethod);

/// <param name="Replayed">
/// <see langword="true"/> when <c>(passengerId, clientRequestId)</c> already existed and this is
/// R-18's retry rather than a new booking.
/// </param>
/// <param name="PickupOtp">
/// The package sender's four digits, on a fresh booking and never again (P-07: "the plaintext leaves
/// the server exactly once"). <see langword="null"/> on every other kind, and on an R-18 replay —
/// only the digest survives the first response, so a retry <em>under a different</em>
/// <c>Idempotency-Key</c> gets the ride without the code. A retry under the <b>same</b> header key
/// replays the original 202 verbatim out of <c>rides.command_log</c> (R-14), which is the path a
/// client that lost its answer actually takes.
/// </param>
public sealed record RideBooking(RideRow Ride, bool Replayed, string? PickupOtp = null);

/// <summary>A ride plus the driver projection <c>RideDetail.driver</c> renders once one is assigned.</summary>
public sealed record RideView(RideRow Ride, RideDriverSummary? Driver, RideContacts Contacts);

/// <summary>
/// The real numbers a ride detail carries once a driver has been assigned (AL-48).
/// </summary>
/// <remarks>
/// <para>
/// <b>AL-48 withdrew masking.</b> D5' BR-28.3 as amended: "Normal call = direct cellular dial of the
/// counterparty's real MSISDN, which the API exposes <b>only after driver acceptance</b>; withheld
/// for rides cancelled before assignment." So every field here is empty until
/// <c>accepted_driver_id</c> is set, and stays filled afterwards — including on a ride that was
/// cancelled <em>after</em> assignment, because that is exactly when the two parties still have to
/// reach each other.
/// </para>
/// <para>
/// <b>P-05 is the fence.</b> The driver's counterparty is the <b>rider</b>, never the booker. On a
/// proxy ride those are two different people and the booker's number is not on this record at all —
/// there is no field it could be put in by accident.
/// </para>
/// </remarks>
/// <param name="CounterpartyPhone">
/// What the caller dials: the driver's number for the passenger side, the rider's for the driver.
/// </param>
/// <param name="SenderPhone">
/// AL-33's package sheet needs two numbers, and <c>RideDetail</c> carries one. The sender is the
/// account that booked the delivery; present only on a package ride.
/// </param>
/// <param name="RecipientPhone">The other half of the same sheet: whoever is receiving the parcel.</param>
public sealed record RideContacts(string? CounterpartyPhone, string? SenderPhone, string? RecipientPhone)
{
    /// <summary>Before acceptance, and for a caller who is only holding an offer.</summary>
    public static readonly RideContacts None = new(null, null, null);
}

/// <summary>The offer dispatch-svc reserved, as it reaches <c>POST /v1/internal/rides/{id}/offer</c>.</summary>
public sealed record PlaceOfferCommand(
    Guid RideId,
    Guid OfferId,
    Guid DriverId,
    Guid VehicleId,
    int? TtlSeconds,
    long? ExpectedVersion);
