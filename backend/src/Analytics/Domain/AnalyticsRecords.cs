namespace MageRide.Analytics.Domain;

/// <summary>
/// One <c>analytics.daily_metrics</c> row — the five figures of one Asia/Colombo day (§23, AL-38).
/// </summary>
/// <param name="MetricDate">The Colombo business date. Primary key.</param>
/// <param name="CompletedTrips">Mode C rides that reached <c>Completed</c> on this date.</param>
/// <param name="GrossFareMinor">Σ of the settled fare of those rides, in LKR minor units.</param>
/// <param name="NewRiders">Passenger role grants made on this date.</param>
/// <param name="NewDrivers">Driver role grants made on this date.</param>
/// <param name="DailyFeeRevenueMinor">Σ of the D-13 daily fees charged for this fee date.</param>
/// <param name="Currency">Always <c>LKR</c> (§0 Money).</param>
/// <param name="MetricDateTzAt">D-38 audit companion: when this day was <em>first</em> rolled up.</param>
/// <param name="RefreshedAt">When it was last recomputed.</param>
public sealed record DailyMetric(
    DateOnly MetricDate,
    int CompletedTrips,
    long GrossFareMinor,
    int NewRiders,
    int NewDrivers,
    long DailyFeeRevenueMinor,
    string Currency,
    DateTimeOffset MetricDateTzAt,
    DateTimeOffset RefreshedAt);

/// <summary>
/// The period KPIs (<c>admin-bff.yaml#DashboardKpis</c>). Σ over the metric days of one range.
/// </summary>
/// <remarks>
/// <c>int64</c> throughout, matching the contract: the daily columns are <c>INT</c>/<c>BIGINT</c>
/// but a year of them summed is not, and a number that overflows on the busiest range available is
/// not a number an operator can plan with.
/// </remarks>
public sealed record DashboardKpis(
    long CompletedTrips,
    long GrossFareMinor,
    long NewRiders,
    long NewDrivers,
    long DailyFeeRevenueMinor)
{
    /// <summary>A range with no rolled-up day at all.</summary>
    public static readonly DashboardKpis Zero = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Percentage change against the immediately preceding period of the same length
/// (<c>admin-bff.yaml#DashboardDeltas</c>).
/// </summary>
/// <remarks>
/// <b>Nullable, and null means undefined rather than zero.</b> Growth from a period that was zero
/// has no percentage — the honest answers are "undefined" or "∞", and every schema property here is
/// optional precisely so the field can be absent. Reporting 0 would say "no change" about a metric
/// that went from nothing to something, and 100 would invent a baseline. Both ends zero <em>is</em>
/// 0 %: nothing changed.
/// </remarks>
public sealed record DashboardDeltas(
    double? CompletedTripsPct,
    double? GrossFarePct,
    double? NewRidersPct,
    double? NewDriversPct,
    double? DailyFeeRevenuePct)
{
    /// <summary>Computes all five from the two periods' KPIs.</summary>
    public static DashboardDeltas Between(DashboardKpis current, DashboardKpis previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        return new DashboardDeltas(
            Pct(current.CompletedTrips, previous.CompletedTrips),
            Pct(current.GrossFareMinor, previous.GrossFareMinor),
            Pct(current.NewRiders, previous.NewRiders),
            Pct(current.NewDrivers, previous.NewDrivers),
            Pct(current.DailyFeeRevenueMinor, previous.DailyFeeRevenueMinor));
    }

    /// <summary>
    /// <c>(current − previous) / previous × 100</c>, rounded to two decimals; null when
    /// <paramref name="previous"/> is zero and <paramref name="current"/> is not.
    /// </summary>
    /// <remarks>
    /// Rounded because the figure is rendered as a percentage badge on a card and a double's tail
    /// would differ between two replicas computing the same quotient. Two decimals is finer than
    /// anything SCR-AP-002 draws and coarse enough to be stable.
    /// </remarks>
    internal static double? Pct(long current, long previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0d : null;
        }

        return Math.Round((current - previous) * 100d / previous, 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The real-time block (<c>admin-bff.yaml#DashboardLive</c>). <b>Never served from a rollup.</b>
/// </summary>
/// <remarks>
/// D6' §I-28.5: the live cards "bypass the period filter" and are fetched from the operational
/// tables. A rolled-up "currently online" is wrong by construction — it is a fact about this
/// instant, and yesterday's copy of it is not a smaller version of the same truth.
/// </remarks>
public sealed record DashboardLive(int OnlineDrivers, int PendingVerifications, int OpenTickets)
{
    public static readonly DashboardLive Zero = new(0, 0, 0);
}

/// <summary>
/// Everything <c>GET /v1/admin/dashboard/stats</c> answers with, in the contract's own shape.
/// </summary>
/// <remarks>
/// admin-bff (C062) serialises this directly: the property names are
/// <c>admin-bff.yaml#/paths/~1v1~1admin~1dashboard~1stats</c>'s field names, so the BFF's job is
/// authorization and the audit event, not reshaping.
/// </remarks>
public sealed record DashboardStats(
    string Period,
    StatsRange Range,
    DashboardKpis Kpis,
    DashboardDeltas DeltaVsPrev,
    DashboardLive Live)
{
    /// <summary>The comparison window the deltas were computed against.</summary>
    /// <remarks>
    /// Not in the JSON contract — <c>deltaVsPrev</c> carries only percentages — but the CSV export
    /// prints it, because a downloaded file has to say what it compared against or the percentages
    /// in it are unfalsifiable six months later.
    /// </remarks>
    public required StatsRange PreviousRange { get; init; }

    /// <summary>The previous period's KPIs. Also CSV-only, for the same reason.</summary>
    public required DashboardKpis PreviousKpis { get; init; }
}

/// <summary>What one pass of the materialisation job did.</summary>
/// <param name="From">First Colombo date recomputed.</param>
/// <param name="To">Last Colombo date recomputed, inclusive.</param>
/// <param name="DaysRolled">How many rows were written. Equals the range length; a day with no activity still gets a zero row.</param>
/// <param name="Elapsed">Wall-clock time of the pass — the number the "completes within its window" claim is about.</param>
public sealed record RollupRunResult(DateOnly From, DateOnly To, int DaysRolled, TimeSpan Elapsed)
{
    public static RollupRunResult Empty(DateOnly date) => new(date, date, 0, TimeSpan.Zero);
}
