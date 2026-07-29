using System.ComponentModel.DataAnnotations;

namespace MageRide.Reputation.Configuration;

/// <summary>
/// Everything reputation-svc is allowed to have an opinion about.
/// </summary>
/// <remarks>
/// <para>
/// The thresholds a spec pins are here as defaults with the spec named beside them. The ones
/// <b>no spec pins</b> are marked as such and are argued at their declaration rather than buried:
/// D5' §4.2 makes the 3-report delisting "temporary" without saying for how long, §11.12 calls the
/// driver-cancel delisting "brief" without saying how brief, AL-16 makes the booking-disable
/// re-enable "after a configurable cooldown" without giving one, E-07 writes "> N rides / 30 d"
/// without giving N, and nothing anywhere says when WARN is entered.
/// </para>
/// <para>
/// Every one of them is admin-configurable in D3' (<c>PUT /v1/admin/drivers/level-config</c>,
/// US-14.12) — which is exactly why they are configuration here and not constants.
/// </para>
/// </remarks>
public sealed class ReputationOptions
{
    public const string SectionName = "Reputation";

    // -------------------------------------------------------------------------------------
    // The rolling window (D-04: "counters with rolling-window reset")
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// How long <c>reports_total</c> and <c>no_shows</c> are counted over before they clear.
    /// 30 days matches the only window any spec gives (E-07's "N rides / 30 d").
    /// </summary>
    /// <remarks>
    /// <c>cancellations_continuous</c> is <b>not</b> window-scoped. D5' §7.2 makes it a consecutive
    /// run reset by any completed ride, and a window that also cleared it would let a passenger
    /// wait out a booking-disable rather than complete a ride to lift it.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan CounterWindow { get; set; } = TimeSpan.FromDays(30);

    // -------------------------------------------------------------------------------------
    // Thresholds
    // -------------------------------------------------------------------------------------

    /// <summary>3 consecutive post-acceptance cancels → BOOKING_DISABLED (US-6A.10b, AL-16).</summary>
    [Range(1, 100)]
    public int CancellationDisableThreshold { get; set; } = 3;

    /// <summary>3 confirmed reports → temporary DELISTED + level−1 (US-12.6, D5' §4.2).</summary>
    [Range(1, 100)]
    public int ReportDelistThreshold { get; set; } = 3;

    /// <summary>
    /// Cancellations at which WARN is entered. <b>No spec pins this.</b> One short of the hard
    /// threshold: a warning that fires at the same count as the block is not a warning.
    /// </summary>
    [Range(1, 100)]
    public int CancellationWarnThreshold { get; set; } = 2;

    /// <summary>Reports at which WARN is entered. <b>No spec pins this.</b></summary>
    [Range(1, 100)]
    public int ReportWarnThreshold { get; set; } = 2;

    /// <summary>
    /// No-shows at which WARN is entered. <b>No spec pins this.</b> A no-show costs a driver a
    /// level immediately (US-6A.7) and never blocks by itself, so this is the only effect
    /// no_shows has on the block state.
    /// </summary>
    [Range(1, 100)]
    public int NoShowWarnThreshold { get; set; } = 3;

    // -------------------------------------------------------------------------------------
    // Durations
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// How long the 3-report delisting holds. D5' §4.2 says "temporary delisting … time-boxed" and
    /// gives no number; <b>7 days is this component's</b>. Long enough to matter, short enough that
    /// a wrongly-reported driver is not waiting on an appeal to work again.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan ReportDelistDuration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// §11.12's "brief delist" after a driver-side cancel. <b>No spec gives a number.</b> 30
    /// minutes: long enough that cancelling to cherry-pick the next ride does not pay, short
    /// enough that a driver whose phone died is not off the road for the evening — the same event
    /// covers both (see <c>systemInitiated</c> on the event).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan DriverCancelDelistDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// AL-16's "configurable cooldown" on a booking-disable. <b>No spec gives a number.</b>
    /// </summary>
    /// <remarks>
    /// AL-16's full re-enable rule is "clear outstanding Rs 50 balance → access restored after a
    /// configurable cooldown or admin/CSR reinstatement". The balance half is billing's and
    /// reputation-svc cannot see it, so what is implemented here is the cooldown, the admin
    /// reinstatement, and D5' §7.2's "counter resets to 0 on any completed ride". Recorded in the
    /// C033 handoff.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan BookingDisableCooldown { get; set; } = TimeSpan.FromHours(24);

    // -------------------------------------------------------------------------------------
    // Driver level (D5' §4.2)
    // -------------------------------------------------------------------------------------

    /// <summary>500 points = +1 level (D5' §4.2). Written onto a level row when one is created.</summary>
    [Range(1, 100_000)]
    public int LevelUpThreshold { get; set; } = 500;

    // -------------------------------------------------------------------------------------
    // The gRPC hot path
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// How long a block status stays in Redis. dispatch-svc calls <c>GetBlockStatus</c> on every
    /// candidate build, and the C033 DoD asks for under 20 ms p95 "against a warm cache".
    /// </summary>
    /// <remarks>
    /// 5 s matches D-08's <c>Wallet__CacheTtlSec=5</c>, the other hard gate on the same hot path.
    /// The TTL is a backstop, not the invalidation: every write deletes the key inside the
    /// transaction's aftermath, so a block takes effect on the next call and not five seconds later.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan BlockStatusCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Dedicated HTTP/2 port for <c>reputation.v1</c> (D7' §4.2 <c>Grpc__ListenPort</c>=5005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>gRPC has to have a port of its own, and that is not a preference.</b> HTTP/1.1 and HTTP/2
    /// cannot be negotiated on a cleartext socket — there is no ALPN to negotiate with — so a
    /// Kestrel endpoint serving the admin routes answers an HTTP/2 preface with
    /// <c>GOAWAY HTTP_1_1_REQUIRED</c>. D7' §4.2 gives this service the port for exactly that
    /// reason, and D3' never exposes the gRPC service through the gateway, so a separate port is
    /// also what a NetworkPolicy can be written against.
    /// </para>
    /// <para>
    /// <b>0 binds an ephemeral port</b>, which is what the tests use so two suites can run at once.
    /// </para>
    /// </remarks>
    [Range(0, 65_535)]
    public int GrpcListenPort { get; set; } = 5005;

    /// <summary>
    /// Port for the admin HTTP routes when neither <c>urls</c> nor <c>ASPNETCORE_URLS</c> says
    /// otherwise. 5000 is the address <c>gateway-routes.json</c> points the reputation-svc cluster
    /// at (C008).
    /// </summary>
    [Range(0, 65_535)]
    public int HttpListenPort { get; set; } = 5000;

    /// <summary>
    /// Guards <c>reputation.v1</c> and <c>/v1/internal/**</c> until the mesh lands (C042).
    /// </summary>
    /// <remarks>
    /// Unset means gRPC answers any in-cluster caller and the internal HTTP route is not mapped —
    /// which is safe for a dev stack on one host and is said loudly at start-up because it is not
    /// safe anywhere else.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------
    // Background work
    // -------------------------------------------------------------------------------------

    /// <summary>Consume <c>ride.events</c> in this process (D6' §2.1 lists this service).</summary>
    public bool ConsumerEnabled { get; set; } = true;

    /// <summary>Consumer group. D6' §2: "consumer group per service".</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "reputation-svc";

    /// <summary>Run the block-state expiry and window sweep in this process.</summary>
    public bool ExpiryWorkerEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Rows the expiry sweep settles per pass.</summary>
    [Range(1, 10_000)]
    public int ExpiryBatchSize { get; set; } = 200;

    /// <summary>Run the E-07 detector in this process.</summary>
    public bool DetectorEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan DetectorInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an IP observation is kept. Personal data under PDPA (E-06); the sweep deletes
    /// anything older, and an erasure request deletes by user.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan NetworkObservationRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>The E-07 detector's own knobs.</summary>
    public CollusionOptions Collusion { get; set; } = new();
}

/// <summary>
/// Thresholds for the three E-07 detectors (ADD §12.6, D5' §15).
/// </summary>
/// <remarks>
/// Every one is deliberately loose. A flag is a review item and never a block — ADD §12.6's
/// auto-suspend is a "Tier-2" decision an admin makes — so a false positive costs an admin thirty
/// seconds, while a false negative is ride-farming that pays out. What must not happen is the same
/// pattern filling the queue every pass, and that is <c>ux_fraud_flags_window</c>'s job, not a
/// threshold's.
/// </remarks>
public sealed class CollusionOptions
{
    /// <summary>
    /// E-07's "same <c>(passenger, driver)</c> &gt; N rides / 30 d". <b>No spec gives N.</b> 8 is
    /// roughly a twice-weekly commute — the honest pattern this detector is most likely to hit,
    /// which is why the flag says how many and lets a human decide.
    /// </summary>
    [Range(2, 1_000)]
    public int PairRideThreshold { get; set; } = 8;

    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan PairWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Distinct accounts sharing one <c>iam.devices.device_key</c> before it is a signal. 2 —
    /// AL-08 binds one device to one session per app, so two accounts on one install is already
    /// the thing being looked for.
    /// </summary>
    [Range(2, 100)]
    public int DeviceSharingThreshold { get; set; } = 2;

    /// <summary>
    /// Distinct accounts on one address inside <see cref="NetworkWindow"/> before it is a signal.
    /// 4, and this is the loosest of the three on purpose: a shared 4G NAT, an office or a
    /// university puts dozens of unrelated users behind one address in Sri Lanka.
    /// </summary>
    [Range(2, 1_000)]
    public int NetworkClusterThreshold { get; set; } = 4;

    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00")]
    public TimeSpan NetworkWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The bucket <c>reputation.fraud_flags.window_key</c> is computed from — the "detection
    /// window" this component's DoD requires a signal to be raised exactly once per.
    /// </summary>
    /// <remarks>
    /// A day, so a pattern that persists is re-raised daily (an admin who dismissed yesterday's
    /// flag and sees it again tomorrow is being told it is still happening) while the detector's
    /// own 15-minute cadence cannot flood the queue.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:15:00", "365.00:00:00")]
    public TimeSpan DetectionWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Signals raised per detector per pass. A bound, not a target.</summary>
    [Range(1, 10_000)]
    public int MaxSignalsPerPass { get; set; } = 200;
}
