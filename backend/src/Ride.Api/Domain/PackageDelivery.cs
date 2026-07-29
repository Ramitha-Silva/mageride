using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// <c>rides.rides.package_size</c> (migration 0601's <c>ck_rides_package_size</c>) — D5' §11's
/// <c>package_size ∈ {S,M,L}</c>.
/// </summary>
/// <remarks>
/// The size is not a capacity this service reasons about. It is dispatch-svc's P-11 input (the
/// static <c>vehicle_type × package_size</c> table that keeps an L parcel off a motorbike) and the
/// description the driver reads before rejecting an offer they do not want. ride-svc records it and
/// puts it on <c>ride.requested</c>, which is the only message dispatch gets.
/// </remarks>
public static class PackageSizes
{
    public const string Small = "S";
    public const string Medium = "M";
    public const string Large = "L";

    public static readonly FrozenSet<string> All =
        new[] { Small, Medium, Large }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// <c>RideDetail.packageStatus</c> (<c>ride.yaml</c>) — the delivery seen from the outside.
/// </summary>
/// <remarks>
/// <para>
/// Derived from <c>rides.rides.state</c>, never stored: ADD Appendix B.2 invariant 6 makes the
/// machine kind-agnostic, so a package traverses the same eighteen states as a passenger ride and a
/// second status column would be a copy that can disagree with the first.
/// </para>
/// <para>
/// <b><see cref="PickedUp"/> is a moment, not a state.</b> The contract's enum names four values
/// and the aggregate has three distinguishable positions: before the pickup OTP, after it, and
/// after the delivery. <c>InProgress</c> is rendered <see cref="InTransit"/> because that is what
/// the ride *is* for its whole duration; the instant of pickup is the <c>package.picked_up</c>
/// event, which is what a consumer that cares about it subscribes to. Raised in the C037 handoff.
/// </para>
/// </remarks>
public static class PackageStatuses
{
    /// <summary>Booked, matching, offered, accepted or waiting at the sender — the pickup OTP has not been read out.</summary>
    public const string PickupPending = "PickupPending";

    /// <summary>Declared by <c>package.picked_up</c>; see the class remarks.</summary>
    public const string PickedUp = "PickedUp";

    /// <summary>The driver holds the parcel and is moving (<c>InProgress</c>).</summary>
    public const string InTransit = "InTransit";

    /// <summary>Handed over by delivery OTP or by photo proof (P-10); the fare is owed or settled.</summary>
    public const string Delivered = "Delivered";

    /// <summary>Where a ride in <paramref name="state"/> stands as a delivery.</summary>
    /// <remarks>
    /// A cancelled or expired package has no delivery status at all — it is <see langword="null"/>
    /// rather than <see cref="PickupPending"/>, because "waiting to be picked up" is a promise the
    /// ride is no longer making.
    /// </remarks>
    public static string? For(string? state) => state switch
    {
        RideStates.Requested or RideStates.Matching or RideStates.Offered
            or RideStates.Accepted or RideStates.DriverArrived => PickupPending,

        RideStates.InProgress => InTransit,

        RideStates.Completed or RideStates.PaymentPending or RideStates.Paid
            or RideStates.CashSettled or RideStates.CashOnDeliveryCollected => Delivered,

        _ => null,
    };
}

/// <summary>Which of the two OTP gates a driver is answering (P-07).</summary>
/// <remarks>
/// The purpose is part of the HMAC message, so the same four digits do not hash to the same value
/// at both ends of one delivery — a driver who was told the pickup code cannot spend it at the
/// door.
/// </remarks>
public enum PackageOtpPurpose
{
    /// <summary>The sender's code. <c>Accepted | DriverArrived → InProgress</c>.</summary>
    Pickup,

    /// <summary>The recipient's code. <c>InProgress → Completed → PaymentPending</c>.</summary>
    Delivery,
}

/// <summary>
/// <c>rides.proof_artifacts.kind</c> (migration 0607) — the values this service writes.
/// </summary>
/// <remarks>
/// <c>signature</c> and <c>pickup_photo</c> are in the CHECK and are written by nobody: no contract
/// route captures either, and a kind no endpoint produces is better absent from the code than
/// present and unreachable. <c>qr_receipt</c> is fare-svc's (AL-47).
/// </remarks>
public static class ProofArtifactKinds
{
    /// <summary>P-10's recipient-absent fallback for the delivery OTP.</summary>
    public const string DeliveryPhoto = "delivery_photo";
}
