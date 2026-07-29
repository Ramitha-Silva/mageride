using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Domain;

/// <summary>The three values <c>dispatch.scheduled_rides.status</c>'s CHECK allows (migration 0704).</summary>
public static class ScheduledRideStatuses
{
    /// <summary>Booked and on the Job Board. The only status the T-30 sweep will claim.</summary>
    public const string Scheduled = "SCHEDULED";

    /// <summary>The <c>rides.rides</c> row exists; the ride is ride-svc's aggregate from here on.</summary>
    public const string Dispatched = "DISPATCHED";

    /// <summary>Withdrawn before dispatch, or abandoned after the T-30 grace ran out.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// The payment methods a scheduled ride may carry (Δ C035, <c>ck_scheduled_rides_payment_method</c>).
/// </summary>
/// <remarks>
/// <c>rides.rides.payment_method</c>'s set minus <c>cod</c>, which D3' makes package-only — a
/// scheduled booking is a passenger ride and <c>POST /v1/rides/schedule</c> takes no package
/// fields. The column's CHECK still admits <c>cod</c> so the two tables stay comparable; this list
/// is what the endpoint accepts.
/// </remarks>
public static class ScheduledPaymentMethods
{
    public const string Cash = "cash";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Cash, "lankaqr", "onepay" };

    public static bool IsKnown(string? method) => method is not null && All.Contains(method);
}

/// <summary>A row of <c>dispatch.scheduled_rides</c> (migrations 0704, 0713).</summary>
/// <param name="RideId">
/// <see langword="null"/> until the T-30 sweep materialises the ride through ride-svc — the
/// contract's <c>ScheduledRide.rideId</c>, verbatim.
/// </param>
public sealed record ScheduledRideRow(
    Guid Id,
    Guid? RideId,
    Guid PassengerId,
    GeoPoint Pickup,
    GeoPoint Dropoff,
    string VehicleType,
    string PaymentMethod,
    DateTimeOffset PickupTime,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// One Job Board card: a scheduled ride, how far its pickup is from the driver asking, and how many
/// drivers have already posted intent on it.
/// </summary>
/// <param name="DistanceM">
/// Exact metres from the querying driver's position to the pickup, from <c>ST_Distance</c> — the
/// same measure the D-06 <c>ST_DWithin</c> filtered on, so the card and the filter agree.
/// </param>
/// <param name="HasIntent">Whether <em>this</em> driver has already posted intent (the card's state).</param>
public sealed record JobBoardEntry(ScheduledRideRow Ride, double DistanceM, int IntentCount, bool HasIntent);

/// <summary>A row of <c>dispatch.driver_levels</c> (migrations 0705, 0713).</summary>
/// <param name="RatingPoints">
/// Points earned since the last level-up — the <em>remainder</em>, not a running total. D5' §4.2:
/// "on crossing threshold: level = min(level+1, 3), points -= 500".
/// </param>
/// <param name="PointsAwardedTotal">
/// Every point ever counted (migration 0713). The watermark that lets the engine recompute from
/// <c>trips.ratings</c> and apply only the delta, so a replay awards nothing twice.
/// </param>
public sealed record DriverLevelRow(
    Guid DriverId, int Level, int RatingPoints, int LevelUpThreshold, int PointsAwardedTotal)
{
    /// <summary>D5' §4.2: "Start Level 3."</summary>
    public const int StartingLevel = 3;

    /// <summary>The <c>ck_driver_levels_level</c> bounds.</summary>
    public const int MinLevel = 1;

    public const int MaxLevel = 3;

    /// <summary>What a driver with no row yet is (the column defaults, D5' §4.2).</summary>
    public static DriverLevelRow Default(Guid driverId, LevelConfigRow config) =>
        new(driverId, StartingLevel, 0, (config ?? LevelConfigRow.Defaults).LevelUpThreshold, 0);
}

/// <summary>The singleton <c>dispatch.level_config</c> row (migration 0713, US-14.12).</summary>
public sealed record LevelConfigRow(
    int LevelUpThreshold,
    int NoShowPenaltyPoints,
    int CancellationPenaltyPoints,
    int JobBoardMinLevel)
{
    /// <summary>
    /// The DDL's own defaults, used when the seed row is somehow missing. Kept in step with
    /// migration 0713 by <c>migrate-verify.sh</c>, which asserts the seeded values.
    /// </summary>
    public static readonly LevelConfigRow Defaults = new(500, 0, 0, 2);
}

/// <summary>The answer to <c>GET /v1/drivers/{driverId}/stats</c> (US-6A.14).</summary>
public sealed record DriverStats(double AcceptanceRate, int NoShows, int Points);

/// <summary>The three <c>dispatch.cancellation_penalties.basis</c> values (migration 0713).</summary>
/// <remarks>
/// ride-svc's own spellings, from <c>RideCancellationService.PenaltyBasisName</c> — this row is
/// built from its <c>cancellation.penalty.accrued</c> event and fare-svc reads both sides.
/// </remarks>
public static class PenaltyBases
{
    /// <summary>D-05's flat Rs 50, the one D5' §7.1 writes the settlement pseudocode for.</summary>
    public const string CancellationFee = "cancellation_fee";

    /// <summary>§11.12's Rs 100 rider no-show fee.</summary>
    public const string NoShowFee = "no_show_fee";

    /// <summary>
    /// §11.12's mid-trip cancel. <b>The stored amount is the <em>quoted</em> fare</b> — the only
    /// number ride-svc holds — and fare-svc settles the metered one instead.
    /// </summary>
    public const string FullFare = "full_fare";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CancellationFee, NoShowFee, FullFare };

    public static bool IsKnown(string? basis) => basis is not null && All.Contains(basis);
}

/// <summary>The two values <c>dispatch.cancellation_penalties.status</c>'s CHECK allows (0706).</summary>
public static class PenaltyStatuses
{
    public const string Outstanding = "OUTSTANDING";
    public const string Settled = "SETTLED";
}

/// <summary>A row of <c>dispatch.cancellation_penalties</c> (migrations 0706, 0713).</summary>
/// <param name="AffectedDriverId">
/// Who the money is owed to. The next trip's driver is a pass-through and never the beneficiary
/// (AL-16, US-6A.9).
/// </param>
public sealed record PenaltyRow(
    Guid Id,
    Guid PassengerId,
    Guid OriginalRideId,
    Guid AffectedDriverId,
    long AmountMinor,
    string Basis,
    string Status,
    Guid? AppliedRideId,
    DateTimeOffset CreatedAt);
