using Dapper;

namespace MageRide.Query.Persistence;

/// <summary>A driver's earnings over a business-date range (US-9.22).</summary>
/// <param name="GrossMinor">Fares earned, net of the OnePay surcharge the passenger pays.</param>
/// <param name="DailyFeeMinor">D-13 daily fees charged in the range.</param>
/// <param name="PenaltyMinor">D-05 cancellation compensation credited to this driver.</param>
/// <param name="TipMinor">E-10 tips.</param>
/// <param name="Trips">Rides that reached an R-05 earning state.</param>
public sealed record EarningsTotals(
    long GrossMinor, long DailyFeeMinor, long PenaltyMinor, long TipMinor, int Trips)
{
    public static readonly EarningsTotals Zero = new(0, 0, 0, 0, 0);

    /// <summary>
    /// What the driver actually made.
    /// </summary>
    /// <remarks>
    /// The daily fee is subtracted and the penalty is <b>added</b>. See
    /// <see cref="EarningsRepository"/> for why the penalty adds — D-05 makes the affected driver
    /// the beneficiary, never the payer.
    /// </remarks>
    public long NetMinor => GrossMinor + TipMinor + PenaltyMinor - DailyFeeMinor;
}

/// <summary>One completed ride's contribution to a driver's earnings.</summary>
public sealed record SessionEarningRow(
    Guid TripId, long GrossMinor, long TipMinor, string Currency, DateTimeOffset EndedAt)
{
    /// <summary>Per-ride net. The daily fee and the D-05 penalty are not ride-level facts.</summary>
    public long NetMinor => GrossMinor + TipMinor;
}

/// <summary>Driver earnings reads.</summary>
public interface IEarningsRepository
{
    Task<EarningsTotals> TotalsAsync(
        Guid driverId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionEarningRow>> SessionsAsync(
        Guid driverId,
        DateOnly from,
        DateOnly to,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEarningsRepository"/>
/// <remarks>
/// <para>
/// <b>The gate is R-05, and it is read off the <em>ride</em>.</b> D5' §8.1: "driver earning posts only
/// on terminal <c>Paid</c>/<c>CashSettled</c>/<c>CashOnDeliveryCollected</c>" — three
/// <c>rides.rides.state</c> values, plus AL-47's driver-QR attestation which "settles like cash" and
/// lands the ride in the same place. Gating on the payment row instead would count a
/// <c>Succeeded</c> attempt on a ride that was later disputed, and would have to reason about the
/// D-10 retry chain to avoid counting one fare twice. Gating on the ride's own terminal state is one
/// predicate that cannot double-count, because a ride has one state.
/// </para>
/// <para>
/// <b>The surcharge is excluded from gross.</b> <c>fares.ride_payments.surcharge_minor</c> is US-8.11's
/// OnePay +5%, which the passenger pays and the gateway keeps. Including it would inflate every
/// card-paid fare on a driver's dashboard by 5% against what they are owed.
/// </para>
/// <para>
/// <b>The penalty line adds, and that is the direction D-05 actually specifies.</b> The only Rs 50
/// penalty on this platform is charged to a <em>passenger</em> for cancelling after an accept and paid
/// to the driver whose time was wasted — <c>dispatch.cancellation_penalties.affected_driver_id</c>,
/// and the comment on that table is explicit that the driver who later collects it is "a pass-through,
/// not the beneficiary". Nothing anywhere in D5' debits a driver a penalty. D3' calls the field
/// <c>penaltyMinor</c> and its prose says "the fee and any penalty netted out", which reads like a
/// deduction; reading it that way would make the field permanently zero and hide money the driver is
/// owed. Raised as a micro-change-set in the C042 handoff: <b>D3' should state the direction.</b>
/// </para>
/// <para>
/// <b><c>fares.driver_earnings</c> is deliberately not read.</b> Migration 1004 creates it as "the read
/// model behind the driver Earnings screen", a per-day rollup — and <em>nothing writes it</em>: its
/// writer is fare-svc's R-05 earning post (C049/C050), which does not exist yet. Reading an unwritten
/// rollup would answer every dashboard with zeros while the payment rows behind it hold real money,
/// which is the failure mode that looks exactly like a working screen. It also carries no tip and no
/// penalty column, so it could not answer the contract in full even once it is written. Recorded in
/// the handoff for C050 to either populate or drop.
/// </para>
/// <para>
/// Every read here is <see cref="ReadConsistency.Eventual"/>. A dashboard total short by one fare that
/// settled in the last second is a number that was true a moment ago; the primary is reserved for the
/// read where lag inverts the answer rather than ageing it.
/// </para>
/// </remarks>
public sealed class EarningsRepository(IQueryConnectionFactory connections) : IEarningsRepository
{
    /// <summary>
    /// The R-05 terminal set, as SQL sees it.
    /// </summary>
    /// <remarks>
    /// <c>CashOnDeliveryCollected</c> is P-08's package leg; <c>Disputed</c> is deliberately absent —
    /// R-05's own list names it, but D5' §8.3 sends an uncollected COD there and AL-47 sends an
    /// unresolved QR claim there, and neither is money the driver has. A disputed ride earns when
    /// Finance resolves it, which moves the ride out of <c>Disputed</c>.
    /// </remarks>
    private const string EarningStates = "('Paid','CashSettled','CashOnDeliveryCollected')";

    /// <summary>
    /// Gross, tips and the trip count, over the rides that reached an earning state in the range.
    /// </summary>
    /// <remarks>
    /// <c>terminal_at</c> converted to Asia/Colombo is the business date (D-13, D-38): a fare settled
    /// at 00:30 Colombo belongs to that day and not to the previous UTC one, and a driver comparing
    /// their app with their own evening is the whole point of the convention.
    /// <para>
    /// <c>DISTINCT ON (ride_id) … attempt_no DESC</c> picks the settling attempt: D-10 chains retries
    /// as new rows, so a ride paid on the third try has three, and summing them would triple the fare.
    /// </para>
    /// </remarks>
    private const string FaresSql =
        $"""
        WITH settled AS (
            SELECT DISTINCT ON (p.ride_id)
                   p.ride_id,
                   p.amount_minor,
                   p.surcharge_minor,
                   p.tip_amount_minor,
                   p.currency
              FROM fares.ride_payments p
              JOIN rides.rides r ON r.id = p.ride_id
             WHERE r.accepted_driver_id = @DriverId
               AND r.state IN {EarningStates}
               AND (r.terminal_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To
             ORDER BY p.ride_id, p.attempt_no DESC
        )
        SELECT COALESCE(SUM(GREATEST(amount_minor - surcharge_minor, 0)), 0)::BIGINT AS GrossMinor,
               COALESCE(SUM(tip_amount_minor), 0)::BIGINT                            AS TipMinor,
               COUNT(*)::INT                                                         AS Trips
          FROM settled;
        """;

    /// <summary>
    /// Rides that earned but have no payment row at all.
    /// </summary>
    /// <remarks>
    /// A cash ride settled by the driver marking it collected can reach <c>CashSettled</c> without
    /// fare-svc having written an attempt. Counted separately so the trip count is the number of
    /// journeys the driver completed and not the number of payment rows the platform happens to hold —
    /// a driver whose day was all cash must not see "0 trips".
    /// </remarks>
    private const string UnpaidRidesSql =
        $"""
        SELECT COUNT(*)::INT
          FROM rides.rides r
         WHERE r.accepted_driver_id = @DriverId
           AND r.state IN {EarningStates}
           AND (r.terminal_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To
           AND NOT EXISTS (SELECT 1 FROM fares.ride_payments p WHERE p.ride_id = r.id);
        """;

    /// <summary>D-13's daily fee. Already stored as an Asia/Colombo <c>DATE</c> (D-38).</summary>
    private const string DailyFeeSql =
        """
        SELECT COALESCE(SUM(amount_minor), 0)::BIGINT
          FROM billing.daily_fee_charges
         WHERE driver_id = @DriverId AND fee_date BETWEEN @From AND @To;
        """;

    /// <summary>
    /// D-05 compensation credited to this driver, counted on the day it was settled.
    /// </summary>
    /// <remarks>
    /// <c>created_at</c> is when the passenger cancelled; the money moves when the penalty is
    /// collected on their next trip, which the table records only as <c>status = 'SETTLED'</c> with no
    /// settled-at column. Attributing it to <c>created_at</c> is the only date the row carries and is
    /// noted as a schema gap in the C042 handoff — a penalty accrued in March and collected in April
    /// currently lands in March's earnings.
    /// </remarks>
    private const string PenaltySql =
        """
        SELECT COALESCE(SUM(amount_minor), 0)::BIGINT
          FROM dispatch.cancellation_penalties
         WHERE affected_driver_id = @DriverId
           AND status = 'SETTLED'
           AND (created_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To;
        """;

    private const string SessionsSql =
        $"""
        WITH settled AS (
            SELECT DISTINCT ON (r.id)
                   r.id                            AS TripId,
                   COALESCE(p.amount_minor, 0)     AS AmountMinor,
                   COALESCE(p.surcharge_minor, 0)  AS SurchargeMinor,
                   COALESCE(p.tip_amount_minor, 0) AS TipMinor,
                   COALESCE(p.currency, 'LKR')     AS Currency,
                   r.terminal_at                   AS EndedAt
              FROM rides.rides r
              LEFT JOIN fares.ride_payments p ON p.ride_id = r.id
             WHERE r.accepted_driver_id = @DriverId
               AND r.state IN {EarningStates}
               AND r.terminal_at IS NOT NULL
               AND (r.terminal_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To
             ORDER BY r.id, p.attempt_no DESC NULLS LAST
        )
        SELECT TripId,
               GREATEST(AmountMinor - SurchargeMinor, 0)::BIGINT AS GrossMinor,
               TipMinor::BIGINT                                  AS TipMinor,
               Currency,
               EndedAt
          FROM settled
         -- Cast for the same reason TripRepository casts: an untyped parameter in `IS NULL` is 42P08.
         WHERE @Before::timestamptz IS NULL
            OR (EndedAt, TripId) < (@Before::timestamptz, @BeforeId::uuid)
         ORDER BY EndedAt DESC, TripId DESC
         LIMIT @Limit;
        """;

    public async Task<EarningsTotals> TotalsAsync(
        Guid driverId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var range = new { DriverId = driverId, From = from, To = to };

        var fares = await connection.QuerySingleAsync<FareTotals>(
            new CommandDefinition(FaresSql, range, cancellationToken: cancellationToken));

        var cashOnly = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(UnpaidRidesSql, range, cancellationToken: cancellationToken));

        var dailyFee = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(DailyFeeSql, range, cancellationToken: cancellationToken));

        var penalty = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(PenaltySql, range, cancellationToken: cancellationToken));

        return new EarningsTotals(
            fares.GrossMinor, dailyFee, penalty, fares.TipMinor, fares.Trips + cashOnly);
    }

    public async Task<IReadOnlyList<SessionEarningRow>> SessionsAsync(
        Guid driverId,
        DateOnly from,
        DateOnly to,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<SessionEarningRow>(
            new CommandDefinition(
                SessionsSql,
                new
                {
                    DriverId = driverId,
                    From = from,
                    To = to,
                    Before = before,
                    BeforeId = beforeId ?? Guid.Empty,
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    private sealed record FareTotals(long GrossMinor, long TipMinor, int Trips);
}
