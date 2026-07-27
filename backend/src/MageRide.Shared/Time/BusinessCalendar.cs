namespace MageRide.Shared.Time;

/// <summary>
/// Business-date arithmetic in <c>Asia/Colombo</c> (D-38, D-13).
/// </summary>
/// <remarks>
/// <para>
/// Every instant the platform stores is UTC <c>TIMESTAMPTZ</c>, but the rules that decide
/// <em>which day</em> something belongs to are local: the daily driver fee is once per Colombo
/// day (D-13), Directional Travel's use counter resets on the Colombo date (DT-03), peak-hour
/// windows, scheduled-ride cutoffs and monthly Mode-B billing all follow suit. Getting this wrong
/// double-charges a driver either side of midnight UTC, which is 05:30 local.
/// </para>
/// <para>
/// ADD §9.1 persists these as a <c>DATE</c> plus a <c>tz_at TIMESTAMPTZ</c> audit field; this type
/// produces both halves.
/// </para>
/// </remarks>
public static class BusinessCalendar
{
    /// <summary>IANA id for the platform's business timezone.</summary>
    public const string TimeZoneId = "Asia/Colombo";

    /// <summary>
    /// The Colombo zone. Resolved from the tz database, not hard-coded to +05:30 — Sri Lanka has
    /// changed offset before (+06:00 from 1996 to 2006) and historical instants must still map to
    /// the right local date.
    /// </summary>
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>The Colombo calendar date an instant falls on.</summary>
    public static DateOnly BusinessDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>The Colombo local time of an instant.</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>Today's Colombo date, per <paramref name="clock"/>.</summary>
    public static DateOnly Today(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return BusinessDate(clock.GetUtcNow());
    }

    /// <summary>The instant a Colombo business day begins (00:00:00 local), as UTC.</summary>
    public static DateTimeOffset StartOfDay(DateOnly businessDate)
    {
        var local = businessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// The instant the next Colombo business day begins, as UTC. Exclusive upper bound — use
    /// <c>ts &gt;= start AND ts &lt; end</c> so no sample can land in two days or neither.
    /// </summary>
    public static DateTimeOffset EndOfDay(DateOnly businessDate) => StartOfDay(businessDate.AddDays(1));

    /// <summary>Half-open UTC range covering one Colombo business day.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly businessDate) =>
        (StartOfDay(businessDate), EndOfDay(businessDate));

    /// <summary>
    /// Half-open UTC range covering a Colombo calendar month — the window monthly per-vehicle
    /// Mode-B billing settles over (AL-03).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) MonthRange(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (StartOfDay(first), StartOfDay(first.AddMonths(1)));
    }

    /// <summary>The Colombo date <paramref name="days"/> after the one <paramref name="instant"/> falls on.</summary>
    public static DateOnly AddBusinessDays(DateTimeOffset instant, int days) => BusinessDate(instant).AddDays(days);

    /// <summary>Whether two instants fall on the same Colombo date.</summary>
    public static bool IsSameBusinessDate(DateTimeOffset left, DateTimeOffset right) =>
        BusinessDate(left) == BusinessDate(right);

    /// <summary>
    /// The pair ADD §9.1 stores together: the Colombo <c>DATE</c> and the <c>tz_at</c> instant it
    /// was derived from.
    /// </summary>
    public static (DateOnly BusinessDate, DateTimeOffset TzAt) Stamp(DateTimeOffset instant) =>
        (BusinessDate(instant), instant.ToUniversalTime());

    /// <summary>Key suffix for the per-day Redis counters (ADD §9.4 <c>…:{yyyy-mm-dd}</c>, DT-03).</summary>
    public static string DateKey(DateOnly businessDate) => businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
