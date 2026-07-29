using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// The <c>rides.timers.kind</c> values ride-svc arms and fires (migration 0605's
/// <c>ck_timers_kind</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Four of the eight, not all eight.</b> <c>offer_expiry</c> is dispatch-svc's: ADD §6 gives
/// dispatch "Quartz.NET (scheduled rides <b>+ offer backstop</b>)", D5' §3.5 files the durable
/// backstop under *Offer TTL &amp; cascade*, and C023 built it — arming the row and calling
/// <c>POST /v1/internal/rides/{id}/offer/expire</c> when it fires. ride-svc arming a second row for
/// the same offer would put two timers on one deadline and make "the ride's timer" ambiguous to
/// everything that reads the table. So the claim below is scoped by kind and the two services
/// share the table without sharing a row. <c>location_request_expiry</c>, <c>otp_attempt_window</c>
/// and <c>cod_uncollected</c> are C037's, for the same reason: the flows that arm them do not exist
/// yet.
/// </para>
/// <para>
/// <b>Why a lease-poll and not Quartz.</b> ADD §6 names "Quartz.NET clustered scheduler" and this
/// is the component that was to bring it. What R-04 actually requires is that the durable row —
/// not Redis, not a process — decides, and that a fire happens within about a second of the
/// deadline on any replica. Quartz's contribution to that would be a job store holding a single
/// recurring trigger whose job is to scan <c>rides.timers</c>; the scan itself is already
/// multi-replica safe because it claims <c>FOR UPDATE SKIP LOCKED</c>, so clustering the trigger
/// would remove parallelism rather than add safety, in exchange for eleven <c>qrtz_*</c> tables no
/// DDL spec declares and a second scheduler to operate. C023 reached the same conclusion for
/// <c>offer_expiry</c> and recorded it; this component matches it rather than running two different
/// timer mechanisms over one table. Recorded again in the C032 handoff.
/// </para>
/// <para>
/// <b>Δ C037: five of the eight, and the other three have owners.</b> <c>cod_uncollected</c> joined
/// the claim — it is P-14's 24-hour window, armed at the pickup of a COD package. The remaining two
/// are deliberately armed by nobody. <c>location_request_expiry</c> <b>cannot exist as a row</b>:
/// ADD §11.15 asks for one at <c>now()+5min</c>, but <c>rides.timers.ride_id</c> is <c>NOT NULL</c>
/// with a foreign key onto <c>rides.rides</c> (0605) and a location request is issued *before* any
/// ride does — <c>rides.location_requests.ride_id</c> is nullable for precisely that reason (0606).
/// The durable deadline is the request row itself, <c>issued_at + ttl_seconds</c>, swept over
/// <c>ix_location_requests_due</c> (0609) in the same worker pass; R-04's property — the durable row
/// decides, never a process — holds either way. <c>otp_attempt_window</c> has <b>no duration in any
/// spec</b>, and forgiveness is the wrong default: P-07 says "max 5 attempts each → admin queue" and
/// a timer that reset the counter would hand an attacker unlimited tries at a 10⁴ code by waiting.
/// A locked handoff is unlocked by support, not by the clock. Both raised in the C037 handoff.
/// </para>
/// </remarks>
public static class RideTimerKinds
{
    /// <summary>dispatch-svc's R-04 offer backstop. Named here only so the claim can exclude it.</summary>
    public const string OfferExpiry = "offer_expiry";

    /// <summary>
    /// Armed at <c>Accepted</c>: the driver said they were coming. If it fires the ride is still
    /// <c>Accepted</c>, which means they never arrived (§11.12's <c>NoShowDriver</c> row).
    /// </summary>
    public const string ArrivalGrace = "arrival_grace";

    /// <summary>
    /// Armed at <c>DriverArrived</c>: the rider has five minutes to appear (D5' §7).
    /// </summary>
    public const string NoShow = "no_show";

    /// <summary>
    /// Armed at <c>PaymentPending</c>: the R-20 <c>Completed+PaymentPending &gt; 10 min</c> watch,
    /// made per-ride so the alert names the ride rather than a count.
    /// </summary>
    public const string PaymentPending = "payment_pending";

    /// <summary>
    /// Armed when the driver's vehicle goes offline (R-15). Its window depends on the state the
    /// ride was in — <see cref="RideGracePolicy"/>.
    /// </summary>
    public const string OfflineGrace = "offline_grace";

    /// <summary>
    /// Armed when a COD package is picked up (Δ C037): P-14's "uncollected COD &gt; 24 h →
    /// <c>Disputed</c>", D5' §8.3's <c>cod_uncollected</c> verbatim. Retired by the driver
    /// confirming the cash, and — like <see cref="OfflineGrace"/> — <b>not</b> by the lifecycle
    /// moves in between, because the parcel is delivered long before the money is reconciled and
    /// AL-33 decouples the two on purpose.
    /// </summary>
    public const string CodUncollected = "cod_uncollected";

    /// <summary>The kinds this service claims. Anything else in the table belongs to someone else.</summary>
    public static readonly FrozenSet<string> Owned = new[]
    {
        ArrivalGrace, NoShow, PaymentPending, OfflineGrace, CodUncollected,
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// R-16's per-state grace windows for a driver whose MQTT last will fired (D5' §6.3).
/// </summary>
/// <remarks>
/// "offline-after-accept 60 s, after-arrive 120 s, in-progress 5 min, at-payment 10 min" — the four
/// numbers verbatim, and the only four. A ride in any other state has no driver to lose: before
/// acceptance nobody is assigned, and after a terminal state nothing can be taken away.
/// </remarks>
public static class RideGracePolicy
{
    private static readonly FrozenDictionary<string, TimeSpan> Windows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [RideStates.Accepted] = TimeSpan.FromSeconds(60),
        [RideStates.DriverArrived] = TimeSpan.FromSeconds(120),
        [RideStates.InProgress] = TimeSpan.FromMinutes(5),
        [RideStates.PaymentPending] = TimeSpan.FromMinutes(10),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>How long the ride tolerates an offline driver in <paramref name="state"/>.</summary>
    public static TimeSpan? For(string? state) =>
        state is not null && Windows.TryGetValue(state, out var window) ? window : null;

    /// <summary>The four states a last will means anything in, for the policy test.</summary>
    public static IEnumerable<KeyValuePair<string, TimeSpan>> All => Windows;
}
