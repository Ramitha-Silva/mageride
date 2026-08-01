using System.Globalization;
using MageRide.Analytics.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Time;

namespace MageRide.Analytics.Domain;

/// <summary>The four values of <c>?period=</c> (D3' AL-38, <c>admin-bff.yaml#StatsPeriod</c>).</summary>
public static class StatsPeriods
{
    public const string Today = "today";
    public const string Week = "week";
    public const string Month = "month";
    public const string Custom = "custom";

    /// <summary>The enum, in the contract's order. <c>today</c> is the contract's default.</summary>
    public static readonly IReadOnlyList<string> All = [Today, Week, Month, Custom];

    public static bool IsKnown(string? period) =>
        period is not null && All.Contains(period, StringComparer.Ordinal);
}

/// <summary>
/// An inclusive range of Asia/Colombo business dates, and the period immediately before it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inclusive at both ends</b>, because that is what the contract's <c>range:{from,to}</c> is: an
/// operator who asks for 1–7 July gets seven days. Every day in the range contributes exactly one
/// <c>analytics.daily_metrics</c> row, or none — a day the job has not reached yet contributes zero
/// rather than being an error.
/// </para>
/// <para>
/// <b>The previous period is defined by the contract, not chosen here.</b>
/// <c>admin-bff.yaml#DashboardDeltas</c> reads "percentage change against the immediately preceding
/// period of the same length", so it is <c>[From − N, From − 1]</c> where <c>N</c> is this range's
/// length in days. That rule is deliberately arithmetic rather than calendar-aware: a 31-day custom
/// range starting mid-July compares against the 31 days before it, and a month-to-date range on the
/// 5th compares against the five days before the 1st — which is what makes a range that spans a
/// month boundary a matter of subtraction rather than of special cases.
/// </para>
/// </remarks>
public sealed record StatsRange(DateOnly From, DateOnly To)
{
    /// <summary>Length in days, both ends included. Always at least 1.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>The equally long range ending the day before this one starts.</summary>
    public StatsRange Previous() => new(From.AddDays(-Days), From.AddDays(-1));

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{From:yyyy-MM-dd}..{To:yyyy-MM-dd}");
}

/// <summary>
/// A resolved statistics query: which period was asked for, the Colombo dates it covers, and the
/// period it is compared against.
/// </summary>
public sealed record StatsPeriod(string Period, StatsRange Range)
{
    /// <summary>The comparison window (<c>deltaVsPrev</c>).</summary>
    public StatsRange PreviousRange => Range.Previous();

    /// <summary>
    /// Turns <c>?period=&amp;from=&amp;to=</c> into the two ranges, in Asia/Colombo (D-38).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Today", "this week" and "this month" are calendar-anchored and end today</b>, because
    /// that is what SCR-AP-002 labels them. `week` runs from <see cref="AnalyticsOptions.WeekStartsOn"/>
    /// and `month` from the 1st, both to today inclusive — so on the 5th, "This month" is five days
    /// and not thirty-one. The alternative reading (a rolling 7 or 30 days) would make "This month"
    /// include days of the previous one, which is not a thing the label can mean.
    /// </para>
    /// <para>
    /// <b>Anything invalid is a 400 naming the parameter</b>, never a silently substituted default:
    /// a `custom` range missing its dates that quietly answered for today would put the wrong number
    /// under the right heading, and the operator would have no way to tell.
    /// </para>
    /// </remarks>
    /// <param name="period">One of <see cref="StatsPeriods"/>; null or empty means the contract's default, <c>today</c>.</param>
    /// <param name="from">Required when <paramref name="period"/> is <c>custom</c>, ignored otherwise.</param>
    /// <param name="to">Required when <paramref name="period"/> is <c>custom</c>, ignored otherwise.</param>
    /// <param name="today">Today's Colombo date, from the caller's <see cref="TimeProvider"/>.</param>
    /// <param name="options">Supplies the week start and the maximum custom range.</param>
    public static StatsPeriod Resolve(
        string? period, DateOnly? from, DateOnly? to, DateOnly today, AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var requested = string.IsNullOrWhiteSpace(period)
            ? StatsPeriods.Today
            : period.Trim().ToLowerInvariant();

        if (!StatsPeriods.IsKnown(requested))
        {
            throw Invalid("period", $"'{period}' is not a period. Use {string.Join(" | ", StatsPeriods.All)}.");
        }

        var range = requested switch
        {
            StatsPeriods.Today => new StatsRange(today, today),
            StatsPeriods.Week => new StatsRange(StartOfWeek(today, options.WeekStartsOn), today),
            StatsPeriods.Month => new StatsRange(new DateOnly(today.Year, today.Month, 1), today),
            _ => Custom(from, to, options),
        };

        return new StatsPeriod(requested, range);
    }

    /// <summary>
    /// The Colombo date the caller's clock is on. The one place this component reads "now" for a
    /// period.
    /// </summary>
    public static DateOnly TodayIn(TimeProvider clock) => BusinessCalendar.Today(clock);

    private static StatsRange Custom(DateOnly? from, DateOnly? to, AnalyticsOptions options)
    {
        if (from is null || to is null)
        {
            // Both, in one problem: a client that omitted one probably omitted both, and two
            // round trips to learn that is two round trips.
            var missing = new Dictionary<string, string[]>(StringComparer.Ordinal);

            if (from is null)
            {
                missing["from"] = ["from is required when period=custom."];
            }

            if (to is null)
            {
                missing["to"] = ["to is required when period=custom."];
            }

            throw new MageRideValidationException(missing, "A custom period needs both ends of its range.");
        }

        if (to.Value < from.Value)
        {
            throw Invalid("to", "to must not be before from.");
        }

        var days = to.Value.DayNumber - from.Value.DayNumber + 1;

        if (days > options.MaxRangeDays)
        {
            throw Invalid(
                "to",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A custom range covers at most {options.MaxRangeDays} days; this one covers {days}."));
        }

        return new StatsRange(from.Value, to.Value);
    }

    /// <summary>
    /// The most recent <paramref name="weekStartsOn"/> on or before <paramref name="date"/>.
    /// </summary>
    private static DateOnly StartOfWeek(DateOnly date, DayOfWeek weekStartsOn)
    {
        var back = ((int)date.DayOfWeek - (int)weekStartsOn + 7) % 7;

        return date.AddDays(-back);
    }

    private static MageRideValidationException Invalid(string parameter, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [parameter] = [message] });
}
