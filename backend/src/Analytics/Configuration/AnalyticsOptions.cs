using System.ComponentModel.DataAnnotations;

namespace MageRide.Analytics.Configuration;

/// <summary>
/// Settings for the dashboard read model (AL-38). Bound from the <c>Analytics</c> configuration
/// section by <c>AddMageRideAnalytics</c>.
/// </summary>
/// <remarks>
/// <b>D7' §4.2 gives this component no variables</b> — it predates AL-38 — so every default below is
/// argued at its declaration rather than cited. Each one is also listed in this project's
/// <c>CLAUDE.md</c> with the consequence of changing it.
/// </remarks>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>
    /// Whether the rollup job runs. <b>Off means the dashboard freezes</b> at whatever was last
    /// materialised: every period query still answers, with figures that stop moving.
    /// </summary>
    /// <remarks>
    /// Kept as a switch rather than "just do not register the job" because the failure is silent
    /// from the outside — a dashboard showing yesterday's numbers looks exactly like a quiet day.
    /// <c>AnalyticsRollupJob</c> logs an error naming the consequence when it finds this false.
    /// </remarks>
    public bool RollupEnabled { get; set; } = true;

    /// <summary>How often the job recomputes its window.</summary>
    /// <remarks>
    /// <b>No spec pins it.</b> 15 minutes is chosen against what the number is for: SCR-AP-002's
    /// "Today" card is the freshest thing served from the rollup, and a quarter-hour is the coarsest
    /// staleness an operator watching a launch day would not notice. The pass is five aggregate
    /// queries over three days, so running it often is cheap; the live block is real-time regardless
    /// and is what an operator actually watches minute to minute.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:10", "24:00:00")]
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many days back from today each tick recomputes, inclusive of today.</summary>
    /// <remarks>
    /// <b>Not one, and the reason is that a metric day is not closed when the day ends.</b> Gross
    /// fare is attributed to the day a trip was completed, and a fare settles later — a cash ride
    /// confirmed the next morning, a driver-QR attestation the passenger claims overnight (AL-47), a
    /// gateway callback that arrived late (R-19). Three days is wide enough for every one of those
    /// and narrow enough that the pass stays five small aggregates. A day older than the window is
    /// rebuilt on demand through <see cref="Rollup.IAnalyticsRollupService.RunRangeAsync"/>.
    /// </remarks>
    [Range(1, 90)]
    public int RollupLookbackDays { get; set; } = 3;

    /// <summary>Largest range a single backfill may rebuild.</summary>
    /// <remarks>
    /// <b>A bound, not a working limit.</b> A rebuild is one round trip per day, so an accidental
    /// "since the beginning of time" would be thousands of them; 400 days covers any real correction
    /// (a full year plus a month of slack) and refuses a typo.
    /// </remarks>
    [Range(1, 4000)]
    public int MaxBackfillDays { get; set; } = 400;

    /// <summary>Which day "This week" starts on.</summary>
    /// <remarks>
    /// <b>Spec gap — raised in the C061 handoff.</b> D2 §SCR-AP-002 and US-24.7 both say "This week"
    /// and no document says which day that begins. ISO 8601's Monday is the default because it is
    /// the only definition with a standard behind it, and it is a setting rather than a constant
    /// because the answer is a local convention, not a platform invariant.
    /// </remarks>
    public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Monday;

    /// <summary>Largest custom range the stats query accepts, in days.</summary>
    /// <remarks>
    /// <b>No spec pins it.</b> The query is a sum over one row per day, so the cost is bounded by
    /// the row count and not by the range — the limit exists so a `from=1970-01-01` typo is a 400
    /// rather than a scan, and so the previous period it implies stays a period somebody meant.
    /// A year and a day, so "the last 12 months" and "this year" both fit.
    /// </remarks>
    [Range(1, 4000)]
    public int MaxRangeDays { get; set; } = 366;

    /// <summary>How recently a driver must have been seen to count as online.</summary>
    /// <remarks>
    /// <b>`dispatch.driver_presence.state` alone is not the answer.</b> A driver whose app was killed
    /// leaves the row saying `AVAILABLE` for ever, and an "online drivers" card that only grows is
    /// worse than no card. R-08 gives the Redis presence hash a 60 s TTL, so two minutes is two
    /// missed heartbeats — long enough to survive one bad cellular moment, short enough that a dead
    /// app leaves the count within a couple of minutes. The cutoff is computed from
    /// <see cref="TimeProvider"/> and passed as a parameter rather than being <c>now()</c> in the
    /// SQL, so the boundary is the service's clock and a test can state it.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "24:00:00")]
    public TimeSpan PresenceFreshness { get; set; } = TimeSpan.FromMinutes(2);
}
