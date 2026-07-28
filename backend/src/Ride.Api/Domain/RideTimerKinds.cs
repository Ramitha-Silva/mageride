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

    /// <summary>The kinds this service claims. Anything else in the table belongs to someone else.</summary>
    public static readonly FrozenSet<string> Owned = new[]
    {
        ArrivalGrace, NoShow, PaymentPending, OfflineGrace,
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
