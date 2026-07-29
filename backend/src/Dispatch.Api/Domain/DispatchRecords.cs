using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Domain;

/// <summary>The four values <c>dispatch.driver_presence.state</c>'s CHECK allows (migration 0701).</summary>
public static class PresenceStates
{
    public const string Offline = "OFFLINE";
    public const string Available = "AVAILABLE";
    public const string Offered = "OFFERED";
    public const string OnRide = "ON_RIDE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Offline, Available, Offered, OnRide,
    };
}

/// <summary>The four values <c>dispatch.offers.status</c>'s CHECK allows (migration 0702).</summary>
public static class OfferStatuses
{
    public const string Offered = "OFFERED";
    public const string Accepted = "ACCEPTED";
    public const string Declined = "DECLINED";
    public const string Expired = "EXPIRED";

    /// <summary>
    /// The two the <c>ux_offers_driver_live</c> partial unique index covers — R-10's "one live
    /// offer per driver". Kept as a set so the repository and the tests cannot drift apart.
    /// </summary>
    public static readonly IReadOnlySet<string> Live = new HashSet<string>(StringComparer.Ordinal)
    {
        Offered, Accepted,
    };
}

/// <summary>A row of <c>dispatch.driver_presence</c> (migration 0701).</summary>
public sealed record PresenceRow(
    Guid DriverId,
    Guid VehicleId,
    string VehicleType,
    string State,
    GeoPoint? Geo,
    GeoPoint? DriverHome,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A driver who survived both filters: the H3 pre-filter put them in the raw set, and PostGIS
/// <c>ST_DWithin</c> confirmed they are actually within the search radius.
/// </summary>
/// <param name="DistanceM">Exact great-circle metres from the pickup, from <c>ST_Distance</c>.</param>
/// <param name="Geo">
/// Where the post-filter found them. Carried so a failed offer can put the driver back into the
/// GEO index at the position it took them out of, without a second read.
/// </param>
public sealed record Candidate(Guid DriverId, Guid VehicleId, string VehicleType, double DistanceM, GeoPoint Geo);

/// <summary>
/// One candidate after D5' §3.2's hard gates and §3.3's weighted score have run over it.
/// </summary>
/// <remarks>
/// <b>A rejected candidate is kept, not dropped.</b> R-11 makes <c>dispatch.candidate_scores</c> the
/// audit of a dispatch decision, and "why was this driver not offered the ride" is the question it
/// is most often asked — a row that only records the survivors cannot answer it. Every evaluated
/// driver gets a row; <see cref="RejectedBy"/> names the gate, and only
/// <see cref="Eligible"/> candidates are ever offered anything.
/// </remarks>
/// <param name="RejectedBy">The <see cref="EligibilityGates"/> constant that excluded them, or
/// <see langword="null"/> when they survived.</param>
public sealed record ScoredCandidate(
    Candidate Candidate,
    bool Eligible,
    string? RejectedBy,
    double Score,
    ScoreBreakdown Breakdown)
{
    public Guid DriverId => Candidate.DriverId;

    public Guid VehicleId => Candidate.VehicleId;

    public string VehicleType => Candidate.VehicleType;

    public double DistanceM => Candidate.DistanceM;
}

/// <summary>
/// <c>dispatch.candidate_scores.breakdown</c> — everything needed to recompute
/// <see cref="ScoredCandidate.Score"/> from the row alone (R-11).
/// </summary>
/// <remarks>
/// <para>
/// The weights travel with the decision rather than being looked up from configuration at audit
/// time: <c>dispatch_algorithm_version</c> says which formula ran, and these say what it ran with,
/// so a decision stays reproducible even after an admin has retuned the live weights.
/// </para>
/// <para>
/// Serialised with <c>MageRideJson.StorageOptions</c> like every other stored envelope, which omits
/// nulls — so a candidate that survived every gate simply has no <c>rejectedBy</c> member, and a
/// passenger ride has no <c>packageSizeCompatible</c>. The authoritative home of the latter is
/// <c>dispatch.candidate_scores.package_size_compatible</c>, which P-11 names and which is a real
/// nullable column; the copy here is for reading one row without a second query.
/// </para>
/// </remarks>
/// <param name="Terms">Each term's normalised input in [0,1], before its weight.</param>
/// <param name="Weights">The weights that multiplied them.</param>
/// <param name="Ordering">
/// Which rule put the cascade in the order it ran, when it was not the score. Absent — and
/// therefore omitted from the JSON — for an ordinary Mode C round, where <see cref="Rank"/> follows
/// the score. Present on a Job Board dispatch, whose order is D5' §3.7's "closest intent-submitting
/// driver by Level" and not §3.3's: the score is still computed and stored, so the row stays
/// reproducible, but a <see cref="Rank"/> that disagreed with the score would otherwise read as a
/// bug rather than as a different rule.
/// </param>
public sealed record ScoreBreakdown(
    string Algorithm,
    int Rank,
    double DistanceM,
    int DistanceHalfLifeM,
    int DriverLevel,
    string BlockState,
    bool WalletOk,
    bool? PackageSizeCompatible,
    string? RejectedBy,
    ScoreTerms Terms,
    ScoreTerms Weights,
    DirectionalBreakdown? Directional = null,
    string? Ordering = null);

/// <summary>The three D5' §3.3 terms, used both for the inputs and for the weights.</summary>
public sealed record ScoreTerms(double Distance, double Level, double Category);

/// <summary>
/// The DT-02 audit: what the Directional Travel predicate computed for a candidate who had an
/// active Destination Filter, and which of its three clauses decided the outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent means "this driver had no filter"</b>, which is a different fact from "the predicate
/// ran and passed" — <c>MageRideJson.StorageOptions</c> omits nulls, so a candidate with no filter
/// simply has no <c>directional</c> member and the overwhelming majority of audit rows are
/// unchanged by this component.
/// </para>
/// <para>
/// The three thresholds travel with the decision for the same reason the scoring weights do
/// (R-11): they are admin-tunable at runtime through
/// <c>PUT /v1/admin/dispatch/directional-config</c>, so a row that carried only the measurements
/// would stop being reproducible the moment an operator retuned them.
/// </para>
/// </remarks>
/// <param name="Matched">Whether the ride survived the predicate. <see langword="false"/> means the
/// candidate was dropped from this round — and from nothing else (DT-05, US-6A.23).</param>
/// <param name="BearingDiffDeg">
/// <c>angularDiff(bearing(driver→destination), bearing(pickup→dropoff))</c>, in <c>[0, 180]</c>.
/// </param>
/// <param name="DetourM">
/// <c>dist(pickup, driver)</c> — the same <c>ST_Distance</c> metres the post-filter ranked by, not a
/// second measurement of it.
/// </param>
/// <param name="ProgressM">
/// <c>dist(pickup, destination) − dist(dropoff, destination)</c>: how much closer to the driver's
/// destination the drop-off leaves them. Negative when the ride heads away.
/// </param>
/// <param name="FailedOn">
/// Which clause of D5' §12.1 refused — <c>bearing</c>, <c>detour</c>, <c>progress</c> — or
/// <c>unevaluable</c> when the ride carried no drop-off to measure against. Absent when the
/// predicate passed.
/// </param>
public sealed record DirectionalBreakdown(
    bool Matched,
    double BearingDiffDeg,
    double DetourM,
    double ProgressM,
    double DriverBearingDeg,
    double RideBearingDeg,
    int ThetaMaxDeg,
    int DetourMaxM,
    int ProgressMinM,
    string? FailedOn = null);

/// <summary>The clause names <see cref="DirectionalBreakdown.FailedOn"/> carries.</summary>
public static class DirectionalClauses
{
    /// <summary><c>angularDiff(…) ≤ θ_max</c> — the ride points somewhere else.</summary>
    public const string Bearing = "bearing";

    /// <summary><c>dist(pickup, driver) ≤ detour_max</c> — the pickup is too far off the way.</summary>
    public const string Detour = "detour";

    /// <summary><c>dist(dropoff, dest) &lt; dist(pickup, dest) − progress_min</c> — it makes no headway.</summary>
    public const string Progress = "progress";

    /// <summary>
    /// The round's input carried no drop-off, so none of the three clauses could be computed. The
    /// candidate is <b>kept</b>: DT-05 bounds what this predicate may do, and dropping a driver on a
    /// measurement that was never taken is a decision made on no evidence.
    /// </summary>
    public const string Unevaluable = "unevaluable";
}

/// <summary>Why a candidate was excluded, as written to <c>candidate_scores.breakdown</c>.</summary>
/// <remarks>
/// Stable strings, because they end up in a JSONB column that outlives this assembly and are what
/// an operator greps for when a driver asks why they stopped receiving offers.
/// </remarks>
public static class EligibilityGates
{
    /// <summary><c>reputation.block_state ∈ {BOOKING_DISABLED, DELISTED}</c> (D-04, D5' §3.2).</summary>
    public const string BlockState = "block_state";

    /// <summary>D-08 / D5' §2.1 — 2nd trip of the Colombo day with a wallet below the daily fee.</summary>
    public const string Wallet = "wallet_daily_fee";

    /// <summary>P-11 — <c>vehicle_type × package_size</c> says this vehicle cannot carry it.</summary>
    public const string PackageSize = "package_size";

    /// <summary>US-12.10 — the passenger has blocked this driver (<c>safety.blocked_drivers</c>).</summary>
    public const string PassengerBlock = "passenger_block";

    /// <summary>E-03 — the vehicle's documents lapsed and registry set <c>DISPATCH_SUSPENDED</c>.</summary>
    public const string DispatchSuspended = "dispatch_suspended";

    /// <summary>
    /// DT-02 — the driver has an active Destination Filter and this ride does not head their way.
    /// </summary>
    /// <remarks>
    /// <b>Last in the gate order, and that ordering is DT-05.</b> A driver refused by the wallet or
    /// by their block state reads as refused by that, because the directional predicate runs
    /// <em>after</em> all hard gates and only ever narrows what they left. It is also the one
    /// "rejection" here that is not a fault of any kind: the driver asked for it, and US-6A.23
    /// forbids any reputation or acceptance-rate consequence — no offer row is written, so there is
    /// nothing for the acceptance rate to count.
    /// </remarks>
    public const string Directional = "directional";
}

/// <summary>The three package sizes <c>rides.rides.package_size</c>'s CHECK allows (P-06).</summary>
public static class PackageSizes
{
    public const string Small = "S";
    public const string Medium = "M";
    public const string Large = "L";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Small, Medium, Large };
}

/// <summary>The <c>kind</c> values <c>rides.rides</c> carries (D5' §10, §11).</summary>
public static class RideKinds
{
    public const string Passenger = "passenger";
    public const string Proxy = "proxy";
    public const string Package = "package";
}

/// <summary>The <c>dispatch.timers.kind</c> values this service arms (migrations 0708, 0711).</summary>
/// <remarks>
/// 0708 deliberately left <c>kind</c> without a CHECK — "both specs print it open, and dispatch-svc
/// adds kinds without a migration". The first two are C034's; <c>directional_expiry</c> is 0708's
/// own and <c>directional_reminder</c> is C036's, which is what that sentence was written for.
/// </remarks>
public static class DispatchTimerKinds
{
    /// <summary>US-6A.11's 120 s global cascade deadline. Subject: a ride.</summary>
    public const string RideTimeout = "ride_timeout";

    /// <summary>R-15's EMQX last-will grace before a dropped driver's offer is released.</summary>
    public const string OfferReleaseGrace = "offer_release_grace";

    /// <summary>DT-04's Destination Filter expiry — 0708's original kind, armed by C036.</summary>
    public const string DirectionalExpiry = "directional_expiry";

    /// <summary>
    /// DT-08 / US-10.14's 10-minute pre-expiry reminder. Its own kind rather than a flag on the
    /// expiry row because <c>ux_dispatch_timers_driver_live</c> is one live timer per
    /// <em>(driver, kind)</em> — one row cannot carry two fire times, and a filter needs both.
    /// </summary>
    public const string DirectionalReminder = "directional_reminder";

    /// <summary>The two a Destination Filter arms, retired together when it clears.</summary>
    public static readonly IReadOnlyList<string> Directional = [DirectionalExpiry, DirectionalReminder];
}

/// <summary>A due row of <c>dispatch.timers</c> (migrations 0708, 0711).</summary>
/// <param name="RideId">Set for <see cref="DispatchTimerKinds.RideTimeout"/>; null otherwise.</param>
/// <param name="DriverId">Set for the driver-subject kinds; null for a ride timeout.</param>
public sealed record DueDispatchTimer(
    Guid Id, string Kind, Guid? RideId, Guid? DriverId, DateTimeOffset FireAt, string? Payload);

/// <summary>A row of <c>dispatch.offers</c> (migration 0702).</summary>
public sealed record OfferRow(
    Guid Id,
    Guid RideId,
    Guid DriverId,
    string Status,
    DateTimeOffset SentAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt);

/// <summary>An <c>offer_expiry</c> row of <c>rides.timers</c> that is due (migration 0605, R-04).</summary>
public sealed record DueOfferTimer(Guid Id, Guid RideId, Guid OfferId, Guid DriverId, DateTimeOffset FireAt);

/// <summary>
/// The eight Driver-App vehicle tiers (AL-09), which are also the only tiers the candidate index
/// is keyed by.
/// </summary>
/// <remarks>
/// A third copy of the same list (ride-svc's <c>RideVehicleTypes</c> and registry-svc's
/// <c>VehicleTypes.DriverApp</c> are the other two) because the index key
/// <c>geo:drivers:available:{vehicleType}:{cell}</c> embeds the tier verbatim: a value the writer
/// and the reader spell differently does not fail, it silently produces an empty candidate set.
/// <c>bus</c> and <c>train</c> are canonical but Mode A, and are refused by the <c>mode = 'C'</c>
/// gate on the vehicle rather than by this list.
/// </remarks>
public static class DispatchVehicleTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck",
    };

    public static bool IsKnown(string? vehicleType) => vehicleType is not null && All.Contains(vehicleType);
}
