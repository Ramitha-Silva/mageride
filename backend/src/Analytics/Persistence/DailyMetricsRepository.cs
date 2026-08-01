using Dapper;
using MageRide.Analytics.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Analytics.Persistence;

/// <summary>
/// <c>analytics.daily_metrics</c> (migration 1405) — the only table this component writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement per metric day, and it is an upsert.</b> "A rollup re-run for the same day is
/// idempotent" is therefore a property of the SQL rather than of a guard somebody has to remember:
/// the primary key is the arbiter, the five figures are recomputed from the sources every time, and
/// running it twice writes the same row twice. There is no delete-then-insert, which would leave a
/// window where the dashboard reported zero for a day that had numbers.
/// </para>
/// <para>
/// <b>Every source read is a SELECT and every source table belongs to somebody else.</b>
/// <c>rides.transitions</c> is ride-svc's, <c>fares.ride_payments</c> is fare-svc's,
/// <c>iam.user_roles</c> is iam-svc's, <c>billing.daily_fee_charges</c> is subscription-svc's. This
/// component contributes a derived row and nothing else, which is C061's fence stated as code.
/// </para>
/// </remarks>
internal sealed class DailyMetricsRepository(INpgsqlConnectionFactory connections)
{
    /// <summary>
    /// Recomputes one Colombo day from the source tables and upserts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The day's bounds arrive as UTC instants, computed by <c>BusinessCalendar</c>.</b> Nothing
    /// here casts a timestamp to a date, and there is no <c>AT TIME ZONE</c> in the statement: the
    /// Colombo boundary is resolved once, in one place, by the type that knows Sri Lanka changed
    /// offset in 1996 and again in 2006 (D-38). Half-open — <c>ts &gt;= start AND ts &lt; end</c> —
    /// so no event can land in two days or in neither.
    /// </para>
    /// <para>
    /// <b><c>billing.daily_fee_charges</c> is the exception, and it is not one.</b> Its
    /// <c>fee_date</c> is already an Asia/Colombo <c>DATE</c> written by subscription-svc under
    /// D-13, so matching on <c>metric_date</c> is comparing two values of the same business
    /// calendar. Converting its <c>charged_at</c> instead would re-derive a day the owning service
    /// already decided, and would disagree with it for any fee charged either side of midnight.
    /// </para>
    /// <para>
    /// <b>Gross fare is one payment per completed ride.</b> A retry chain is several
    /// <c>fares.ride_payments</c> rows for one fare (D-10), so summing the table would bill the
    /// dashboard for every attempt. <c>DISTINCT ON (ride_id) … ORDER BY attempt_no DESC</c> takes
    /// the latest settled attempt, which is the one that collected the money.
    /// </para>
    /// <para>
    /// <b><c>metric_date_tz_at</c> is written once and never moved.</b> Migration 1405 defines it as
    /// the instant the day was <em>first</em> rolled up — the D-38 audit companion for the business
    /// date — while <c>refreshed_at</c> is the last recompute. The <c>DO UPDATE</c> list omits it on
    /// purpose; including it would collapse the two columns into one and lose the first answer.
    /// </para>
    /// </remarks>
    internal const string UpsertSql =
        """
        WITH completed AS (
            SELECT DISTINCT t.ride_id
              FROM rides.transitions t
             WHERE t.to_state = @CompletedState
               AND t.ts >= @DayStart
               AND t.ts <  @DayEnd
        ),
        settled AS (
            SELECT DISTINCT ON (p.ride_id) p.ride_id, p.amount_minor
              FROM fares.ride_payments p
              JOIN completed c ON c.ride_id = p.ride_id
             WHERE p.state = ANY(@SettledStates)
             ORDER BY p.ride_id, p.attempt_no DESC, p.created_at DESC
        )
        INSERT INTO analytics.daily_metrics
            (metric_date, metric_date_tz_at, completed_trips, gross_fare_minor,
             new_riders, new_drivers, daily_fee_revenue_minor, currency, refreshed_at)
        SELECT
            @MetricDate,
            @Now,
            (SELECT count(*) FROM completed)::int,
            (SELECT coalesce(sum(s.amount_minor), 0) FROM settled s)::bigint,
            (SELECT count(*) FROM iam.user_roles r
              WHERE r.role = @PassengerRole AND r.granted_at >= @DayStart AND r.granted_at < @DayEnd)::int,
            (SELECT count(*) FROM iam.user_roles r
              WHERE r.role = @DriverRole AND r.granted_at >= @DayStart AND r.granted_at < @DayEnd)::int,
            (SELECT coalesce(sum(f.amount_minor), 0) FROM billing.daily_fee_charges f
              WHERE f.fee_date = @MetricDate AND f.status = @DailyFeePaid)::bigint,
            'LKR',
            @Now
        ON CONFLICT (metric_date) DO UPDATE
            SET completed_trips         = EXCLUDED.completed_trips,
                gross_fare_minor        = EXCLUDED.gross_fare_minor,
                new_riders              = EXCLUDED.new_riders,
                new_drivers             = EXCLUDED.new_drivers,
                daily_fee_revenue_minor = EXCLUDED.daily_fee_revenue_minor,
                refreshed_at            = EXCLUDED.refreshed_at;
        """;

    /// <summary>
    /// Σ over the metric days of one inclusive range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every column is summed as <c>bigint</c>.</b> The daily columns are <c>INT</c> and
    /// <c>BIGINT</c>; a year of the first summed is not an <c>INT</c>, and Dapper binds constructor
    /// parameters by exact type — an <c>Int32</c> result against an <c>Int64</c> parameter does not
    /// convert, it fails to materialise the record (C060's rule, same cause).
    /// </para>
    /// <para>
    /// <b>A range with no rows answers zeros, not null.</b> <c>coalesce</c> around every aggregate,
    /// because a period before the platform existed is a real question with the answer "nothing
    /// happened" — and because the previous period of an early range is exactly that.
    /// </para>
    /// </remarks>
    internal const string AggregateSql =
        """
        SELECT coalesce(sum(m.completed_trips), 0)::bigint         AS completed_trips,
               coalesce(sum(m.gross_fare_minor), 0)::bigint        AS gross_fare_minor,
               coalesce(sum(m.new_riders), 0)::bigint              AS new_riders,
               coalesce(sum(m.new_drivers), 0)::bigint             AS new_drivers,
               coalesce(sum(m.daily_fee_revenue_minor), 0)::bigint AS daily_fee_revenue_minor
          FROM analytics.daily_metrics m
         WHERE m.metric_date >= @From AND m.metric_date <= @To;
        """;

    /// <summary>One materialised day, for diagnostics and for the rollup's own tests.</summary>
    internal const string FindSql =
        """
        SELECT metric_date, completed_trips, gross_fare_minor, new_riders, new_drivers,
               daily_fee_revenue_minor, currency, metric_date_tz_at, refreshed_at
          FROM analytics.daily_metrics
         WHERE metric_date = @MetricDate;
        """;

    /// <summary>The materialised days of a range, oldest first.</summary>
    internal const string ListSql =
        """
        SELECT metric_date, completed_trips, gross_fare_minor, new_riders, new_drivers,
               daily_fee_revenue_minor, currency, metric_date_tz_at, refreshed_at
          FROM analytics.daily_metrics
         WHERE metric_date >= @From AND metric_date <= @To
         ORDER BY metric_date;
        """;

    /// <summary>Recomputes and upserts one Colombo business date.</summary>
    /// <param name="metricDate">The Colombo date to materialise.</param>
    /// <param name="dayStart">Inclusive UTC start of that Colombo day.</param>
    /// <param name="dayEnd">Exclusive UTC start of the next Colombo day.</param>
    /// <param name="now">The instant to stamp <c>refreshed_at</c> with, from the service clock.</param>
    public async Task RollupAsync(
        DateOnly metricDate,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            UpsertSql,
            new
            {
                MetricDate = metricDate,
                DayStart = dayStart,
                DayEnd = dayEnd,
                Now = now,
                CompletedState = AnalyticsVocabulary.RideCompleted,
                SettledStates = AnalyticsVocabulary.SettledPaymentStates,
                PassengerRole = AnalyticsVocabulary.PassengerRole,
                DriverRole = AnalyticsVocabulary.DriverRole,
                DailyFeePaid = AnalyticsVocabulary.DailyFeePaid,
            },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }

    /// <summary>Σ of the five KPIs over an inclusive range of Colombo dates.</summary>
    public async Task<DashboardKpis> AggregateAsync(StatsRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<DashboardKpis>(new CommandDefinition(
            AggregateSql,
            new { range.From, range.To },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }

    /// <summary>One materialised day, or null if the job has not reached it.</summary>
    public async Task<DailyMetric?> FindAsync(DateOnly metricDate, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DailyMetric>(new CommandDefinition(
            FindSql,
            new { MetricDate = metricDate },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }

    /// <summary>Every materialised day of a range, oldest first.</summary>
    public async Task<IReadOnlyList<DailyMetric>> ListAsync(StatsRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DailyMetric>(new CommandDefinition(
            ListSql,
            new { range.From, range.To },
            commandTimeout: connections.CommandTimeoutSeconds,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
