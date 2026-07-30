using System.ComponentModel.DataAnnotations;

namespace MageRide.FleetHealth.Configuration;

/// <summary>
/// fleet-health-svc's settings. D7' §4.2 gives this service two — <c>Health__OfflinePct</c>=10 and
/// <c>Health__WindowMin</c>=5 — and everything else here is argued at its declaration.
/// </summary>
/// <remarks>
/// <para>
/// The section is <c>Health</c> rather than <c>FleetHealth</c> because D7' §4.2's two variables are
/// spelled <c>Health__*</c> and <c>.env.app.example</c> ships them that way; inventing a second
/// prefix would leave an operator setting a key nothing reads.
/// </para>
/// <para>
/// <b>Two of these numbers change the meaning of stored data and are not free to retune.</b>
/// <see cref="StaleAfter"/> and <see cref="OfflineAfter"/> are US-3.13's definitions of the words
/// on an operator's screen; moving them moves every device's state at once, which is why they are
/// returned to the client (<c>thresholds</c>) rather than assumed by it.
/// </para>
/// </remarks>
public sealed class FleetHealthOptions
{
    public const string SectionName = "Health";

    /// <summary>
    /// Master switch. Off leaves the HTTP surface mapped and every input switched off, so the
    /// dashboard answers from whatever the rollup last held — which is why it is announced loudly
    /// at start-up.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // US-3.13's state ladder
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Silence after which a tracker is <c>Stale</c> — US-3.13's "no ping &gt; 5 min", verbatim.
    /// </summary>
    /// <remarks>
    /// Measured against the platform receive clock, not the device's GNSS clock. A tracker whose
    /// clock is a year fast would otherwise be permanently Online and one a year slow permanently
    /// Offline, and a wrong clock is common enough that C039 has a <c>MaxClockSkewAhead</c> gate
    /// for it.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:10", "24:00:00")]
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Silence after which a tracker is <c>Offline</c> — US-3.13's "no ping &gt; 30 min", verbatim.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> D6' §4.5's 30 <i>seconds</i>. That is dispatch-svc's fallback
    /// threshold — "tracker offline &gt; 30 s → fall back to phone GPS or mark unavailable" — a
    /// decision about one ride taken on the hot path. This is a fleet operator's dashboard, where a
    /// bus in a tunnel must not read as a device failure.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:20", "168:00:00")]
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromMinutes(30);

    // -------------------------------------------------------------------------------------------
    // US-3.16's device-down alert
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Share of a fleet's <c>ACTIVE</c> trackers that must be missing from one window before an
    /// alert is raised — D7' §4.2's <c>Health__OfflinePct</c>=10.
    /// </summary>
    /// <remarks>
    /// The comparison is <c>&gt;=</c>, not <c>&gt;</c>. The deliverable writes "&gt;10 % of a fleet
    /// offline" and this component's definition of done writes "a simulated 10 % fleet outage
    /// raises exactly one alert per window" — with a strict comparison a fleet configured at 10
    /// that loses exactly 10 % is silent, which is the case the DoD names. Recorded in the C044
    /// handoff.
    /// </remarks>
    [Range(0.1, 100)]
    public double OfflinePct { get; set; } = 10;

    /// <summary>
    /// The window the share is measured over — D7' §4.2's <c>Health__WindowMin</c>=5, in minutes.
    /// </summary>
    /// <remarks>
    /// Minutes rather than a <see cref="TimeSpan"/> because the variable is named <c>WindowMin</c>
    /// and an operator who sets it to <c>5</c> must not get five seconds. It also has to stay equal
    /// to <c>telemetry.fleet_health_5m</c>'s <c>time_bucket</c> width: the numerator is that
    /// aggregate's bucket, so a service configured to 3 minutes would compare a 3-minute
    /// expectation against a 5-minute count. <see cref="Window"/> is the derived span and
    /// <c>ContinuousAggregateMaintainer</c> refuses to start when the two disagree.
    /// </remarks>
    [Range(1, 60)]
    public int WindowMin { get; set; } = 5;

    /// <summary><see cref="WindowMin"/> as a span.</summary>
    public TimeSpan Window => TimeSpan.FromMinutes(WindowMin);

    /// <summary>Run the window evaluation and raise alerts.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>
    /// How often the worker looks for a newly closed window.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> Not the window itself: a tick aligned to the window would evaluate the bucket
    /// the instant it closed, and the aggregate's own refresh policy has an <c>end_offset</c> of five
    /// minutes, so the materialised rows for it may not exist yet. Checking every minute means a
    /// closed window is evaluated at most a minute late and re-checked if the refresh was still
    /// catching up; the alert is idempotent per window, so a re-check costs nothing.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan AlertCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Smallest fleet an alert may be raised for.
    /// </summary>
    /// <remarks>
    /// <b>No spec — 1, so nothing is suppressed by default.</b> US-3.16 is written as a percentage,
    /// and on a two-tracker fleet one device is 50 %: correct arithmetic and, for some operators,
    /// noise. The knob exists so that decision is visible in configuration rather than argued in
    /// code, and it is off in the same sense <c>Dispatch:BatchMatchingEnabled</c> is.
    /// </remarks>
    [Range(1, 10_000)]
    public int MinFleetSize { get; set; } = 1;

    /// <summary>
    /// Raise only when the share <i>crosses</i> the threshold — the previous window was below it.
    /// </summary>
    /// <remarks>
    /// US-3.16 is "N % of my fleet <b>goes</b> offline within a 5-minute window", which is a
    /// transition and not a level. Level-triggered, a fleet with a fifth of its vehicles parked for
    /// the season would alert every five minutes for ever and the alert would be muted within a day.
    /// Turning this off makes it level-triggered, still one alert per window.
    /// </remarks>
    public bool AlertOnCrossingOnly { get; set; } = true;

    /// <summary>
    /// Refresh the trailing <c>telemetry.fleet_health_5m</c> bucket before evaluating it.
    /// </summary>
    /// <remarks>
    /// This is the "continuous-aggregate maintenance" half of the component. Migration 1802 gives
    /// the aggregate a policy with a five-minute <c>end_offset</c>, so the bucket that has just
    /// closed is materialised by TimescaleDB's own scheduler eventually and not necessarily yet;
    /// <c>materialized_only = false</c> means a read still answers correctly by rescanning raw
    /// chunks for the tail, which is precisely the scan the rollup exists to avoid. Calling
    /// <c>refresh_continuous_aggregate</c> for the closed window is running the aggregate's own
    /// procedure, not forming a second opinion about it.
    /// </remarks>
    public bool RefreshAggregateEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // Ingest — `telemetry.normalized`
    // -------------------------------------------------------------------------------------------

    /// <summary>Consume <c>telemetry.normalized</c>, the ping clock every state is measured from.</summary>
    public bool PingConsumerEnabled { get; set; } = true;

    /// <summary>Consumer group for <c>telemetry.normalized</c> (D6' §2: "consumer group per service").</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "fleet-health";

    /// <summary>Consumer group for <c>provisioning.events</c>.</summary>
    /// <remarks>
    /// A second group because it is a second topic. Sharing one group across two topics would make a
    /// slow binding event delay the ping clock, and the two need opposite offset resets.
    /// </remarks>
    [Required]
    public string ProvisioningConsumerGroup { get; set; } = "fleet-health-provisioning";

    /// <summary>Consume <c>provisioning.events</c> — the IMEI, the binding state and US-3.8's decommission.</summary>
    public bool ProvisioningConsumerEnabled { get; set; } = true;

    /// <summary>
    /// How often the accumulated per-device pings are written.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> The ingest plane peaks at 20k msg/s (T-10) and health is a fact with a
    /// five-minute grain, so a row write per sample would be four orders of magnitude more database
    /// work than the question needs. Samples are collapsed to the newest per vehicle in memory and
    /// flushed as one set-based upsert; five seconds is a thousandth of the stale window, so no
    /// state transition is ever late because of it.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Devices held in the pending-flush map before new ones are dropped and counted.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> T-10 sizes the plane at 100k trackers, so a flush interval's worth of distinct
    /// vehicles is bounded by the fleet and this is twice it. A dropped tick makes one device look up
    /// to one flush staler than it is; an unbounded map would turn a database outage into an OOM
    /// kill, which is the trade C040 makes the same way.
    /// </remarks>
    [Range(1_000, 5_000_000)]
    public int MaxBufferedDevices { get; set; } = 200_000;

    /// <summary>
    /// Read <c>telemetry.normalized</c> from the earliest offset instead of the latest.
    /// </summary>
    /// <remarks>
    /// Off, for C039's reason: this is a current-state rollup, and replaying a day of samples would
    /// spend the whole replay writing values the very next sample overwrites. The upsert takes the
    /// <c>GREATEST</c> of old and new, so a replay cannot make a device look fresher than it is —
    /// it is simply work with no product. The test harness is the only thing that sets this.
    /// </remarks>
    public bool StartFromEarliest { get; set; }

    // -------------------------------------------------------------------------------------------
    // Ingest — the device plane (EMQX)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Hold the <c>veh/+/status</c> and <c>sys/diag/+</c> subscriptions.
    /// </summary>
    /// <remarks>
    /// <b>Off by default</b>, the same call trip-state-svc makes for its own last-will worker: this
    /// is the only part of the service that needs a broker, and a deployment without EMQX reachable
    /// should serve the dashboard rather than log a connection failure every thirty seconds. The
    /// states still work without it — they are thresholds on silence, and the last will only makes
    /// the verdict prompter.
    /// </remarks>
    public bool DevicePlaneEnabled { get; set; }

    /// <summary>Consume the retained <c>veh/{vehicleId}/status</c> last will (R-15, T-04).</summary>
    public bool StatusEnabled { get; set; } = true;

    /// <summary>Consume <c>sys/diag/{vehicleId}</c> (D6' §3.1, QoS 0) — US-3.12's four fields.</summary>
    public bool DiagnosticsEnabled { get; set; } = true;

    /// <summary>
    /// Service name the MQTT session token is minted for; the username becomes
    /// <c>svc-{name}</c>.
    /// </summary>
    /// <remarks>
    /// <c>acl.conf</c> grants <c>^svc-</c> everything under <c>veh/#</c> and <c>sys/#</c>, so this
    /// component added no ACL rule.
    /// </remarks>
    [Required]
    public string MqttServiceName { get; set; } = "fleet-health";

    // -------------------------------------------------------------------------------------------
    // The transition sweep
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Run the sweep that notices a device has changed state.
    /// </summary>
    /// <remarks>
    /// <b>A device going quiet produces no event, so only a clock can move it.</b> That is the whole
    /// reason this exists — and it is deliberately not what the dashboard depends on: the read
    /// derives every state fresh, so with the sweep off the counts are still right and only the
    /// transition counters, the <c>since</c> timestamp and the diagnostics sync stop.
    /// </remarks>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>
    /// How often the sweep looks for devices whose derived state no longer matches the recorded one.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> A minute against a five-minute stale window: a state change is recorded well
    /// inside the grain of the fact, and the scan is one pass over the health table.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Rows the sweep moves per pass.</summary>
    /// <remarks>
    /// <b>No spec.</b> A bound, not a throttle: a regional GSM outage flips a whole fleet at once and
    /// the pass should be one bounded statement rather than a hundred-thousand-row transaction. The
    /// remainder is picked up by the next pass a minute later, and both passes are idempotent.
    /// </remarks>
    [Range(1, 100_000)]
    public int SweepBatchSize { get; set; } = 5_000;

    /// <summary>
    /// Push <c>last_seen_at</c>, <c>signal_strength</c>, <c>battery_mv</c> and <c>sat_count</c> back
    /// onto <c>prov.tracker_bindings</c> (US-3.12).
    /// </summary>
    /// <remarks>
    /// C030's own CLAUDE.md hands those four columns to this service — "the columns are read here and
    /// written there" — and until now they had a reader (<c>GET /v1/trackers/{imei}</c>) and no
    /// writer, so the Admin Portal's per-tracker panel was permanently blank. Done on the sweep and
    /// not on the ping path: a per-sample update to another context's table at 20k/s is not a thing
    /// worth doing for a value C030 itself says may be stale.
    /// </remarks>
    public bool BindingSyncEnabled { get; set; } = true;

    /// <summary>How often the diagnostics sync runs, independently of the sweep.</summary>
    /// <remarks>
    /// <b>No spec.</b> Separate from <see cref="SweepInterval"/> because the sweep touches only the
    /// devices that moved while this touches every device whose ping advanced — at T-10's 100k trackers
    /// that is 100k rows an interval, each one a real write because <c>prov.tracker_bindings</c> carries
    /// an <c>updated_at</c> trigger. Five minutes keeps it to a few hundred rows a second at full scale
    /// and is well inside "may be stale", which is what C030 already says about the column.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "06:00:00")]
    public TimeSpan BindingSyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    // -------------------------------------------------------------------------------------------
    // The read
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Devices returned in one <c>GET /v1/fleets/{fleetId}/health</c> body.
    /// </summary>
    /// <remarks>
    /// <b>No spec, and the contract has no pagination</b> — D3' gives the operation a flat <c>items</c>
    /// array. 5 000 is US-3.2's bulk-onboarding ceiling, so the largest fleet the platform lets an
    /// operator create fits in one answer. Beyond it the response sets <c>itemsTruncated</c>: the
    /// counts always cover the whole fleet, so a truncated list never reads as a smaller fleet.
    /// </remarks>
    [Range(1, 100_000)]
    public int MaxItems { get; set; } = 5_000;
}
