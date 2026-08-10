using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;

namespace MageRide.Subscriptions.Persistence;

/// <summary>One row of <c>billing.daily_fee_charges</c> (migration 1103).</summary>
public sealed record DailyFeeCharge(
    Guid DriverId,
    Guid VehicleId,
    DateOnly FeeDate,
    DateTimeOffset FeeDateTzAt,
    long AmountMinor,
    string Currency,
    int TripsThatDay,
    string Status,
    DateTimeOffset ChargedAt);

/// <summary>
/// <c>billing.daily_fee_charges</c>, plus the one cross-context read the D-13 rule needs.
/// </summary>
internal interface IDailyFeeRepository
{
    /// <summary>The charge row for one (driver, vehicle, Colombo day), or <see langword="null"/>.</summary>
    Task<DailyFeeCharge?> ReadAsync(
        Guid driverId, Guid vehicleId, DateOnly feeDate, CancellationToken cancellationToken);

    /// <summary>
    /// The driver's charge for a Colombo day across every vehicle, newest first — what
    /// <c>GET /v1/fees/{driverId}/today</c> reports when the driver has no vehicle selected.
    /// </summary>
    Task<DailyFeeCharge?> ReadForDayAsync(Guid driverId, DateOnly feeDate, CancellationToken cancellationToken);

    /// <summary>
    /// Mode C trips the driver has already taken on a Colombo day, counted across every vehicle.
    /// </summary>
    Task<int> CountTripsAsync(
        Guid driverId, DateOnly feeDate, Guid? excludingRideId, CancellationToken cancellationToken);

    /// <summary>Writes or upgrades the day's charge row and returns what the row now says.</summary>
    Task<DailyFeeCharge> UpsertAsync(DailyFeeCharge charge, CancellationToken cancellationToken);

    /// <summary>Deduction history, newest first, over an inclusive Colombo-date range (US-9A.6).</summary>
    /// <param name="after">
    /// Exclusive lower bound in the result's own ordering — the (date, vehicle) pair the previous page
    /// ended on.
    /// </param>
    Task<IReadOnlyList<DailyFeeCharge>> HistoryAsync(
        Guid driverId,
        DateOnly? from,
        DateOnly? to,
        (DateOnly FeeDate, Guid VehicleId)? after,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDailyFeeRepository"/>
internal sealed class DailyFeeRepository(INpgsqlConnectionFactory connections) : IDailyFeeRepository
{
    /// <remarks>
    /// <c>amount_minor</c> is an <c>INTEGER</c> in §10 and a <c>long</c> here, for the reason
    /// <see cref="PlanRepository"/> gives: money is int64 everywhere else on the platform, and Dapper's
    /// constructor binding matches parameter types exactly, so the cast is what makes the record
    /// materialise. The column order is the record's parameter order — Dapper's constructor binding is
    /// positional, so reordering one without the other stops the read working.
    /// </remarks>
    private const string SelectColumns =
        """
        driver_id, vehicle_id, fee_date, fee_date_tz_at, amount_minor::bigint AS amount_minor,
        currency, trips_that_day, status, charged_at
        """;

    public async Task<DailyFeeCharge?> ReadAsync(
        Guid driverId, Guid vehicleId, DateOnly feeDate, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DailyFeeCharge>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.daily_fee_charges
                 WHERE driver_id = @DriverId AND vehicle_id = @VehicleId AND fee_date = @FeeDate;
                """,
                new { DriverId = driverId, VehicleId = vehicleId, FeeDate = feeDate },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>PAID</c> ahead of <c>WAIVED_FIRST_TRIP</c>: a driver who used two vehicles in one Colombo day
    /// has a row for each, and the dashboard's question is "have I paid today?", which one paid row
    /// answers whatever the other says.
    /// </remarks>
    public async Task<DailyFeeCharge?> ReadForDayAsync(
        Guid driverId, DateOnly feeDate, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DailyFeeCharge>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.daily_fee_charges
                 WHERE driver_id = @DriverId AND fee_date = @FeeDate
                 ORDER BY (status = 'PAID') DESC, charged_at DESC
                 LIMIT 1;
                """,
                new { DriverId = driverId, FeeDate = feeDate },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>Counted from <c>dispatch.offers</c>, and that is not a free choice.</b> dispatch-svc's D-08
    /// pre-dispatch gate asks whether this charge <em>would</em> succeed, so that a driver who cannot
    /// pay is not offered the ride at all (US-9.1's "request missed: insufficient balance") — and it
    /// counts <c>status = 'ACCEPTED'</c> offers responded to on the Colombo day. If this service
    /// counted something else, the gate would mispredict its own subject: it would withhold an offer
    /// over a fee that would have been waived, or offer a trip whose accept then fails with a
    /// <c>402</c>. One number, read the same way in both places. C034's own repository makes the case
    /// for the source — an ACCEPTED offer is dispatch's record of exactly D5' §2.2's
    /// "completed+accepted today for driver", written by the service that owns the accept.
    /// </para>
    /// <para>
    /// A read across a bounded-context line, and read-only: nothing here writes <c>dispatch.*</c>. The
    /// alternative is a synchronous call on the accept path, inside D-08's latency budget — the same
    /// trade iam-svc's bootstrap and wallet-svc's <c>outstandingDebtMinor</c> make.
    /// </para>
    /// <para>
    /// <b>Bounded by the Colombo day as a half-open UTC range, not by <c>::date</c> on the column.</b>
    /// The two predicates select the same rows; this one can use
    /// <c>ix_offers_driver_responded</c> (migration 0713), which the function form cannot.
    /// </para>
    /// <para>
    /// <b><c>excludingRideId</c> is what keeps the first trip free.</b> D3' has ride-svc charge the fee
    /// during offer acceptance, after the conditional <c>UPDATE … AND version = :v</c> has already
    /// settled the offer — so by the time this service is asked, the trip being accepted is itself in
    /// the count, and "the first trip of the day" would arrive as <c>tripsToday = 1</c> and be charged.
    /// Excluding the caller's own ride makes the answer identical whichever side of the accept the call
    /// is made from.
    /// </para>
    /// <para>
    /// A ride cancelled or no-showed <em>after</em> its accept still counts: the offer stays
    /// <c>ACCEPTED</c> (0712 records the end of its liveness in <c>released_at</c>, not in the status),
    /// which is the literal reading of "accepted" and is what stops the free trip being farmed by a
    /// driver who accepts and lets the rider cancel. Package deliveries count with passenger rides
    /// (US-20.10) — the same offer log carries both, so no predicate is needed to include them and one
    /// would exclude them.
    /// </para>
    /// </remarks>
    public async Task<int> CountTripsAsync(
        Guid driverId, DateOnly feeDate, Guid? excludingRideId, CancellationToken cancellationToken)
    {
        var (start, end) = BusinessCalendar.DayRange(feeDate);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT count(*)::int
                  FROM dispatch.offers
                 WHERE driver_id = @DriverId
                   AND status = 'ACCEPTED'
                   AND responded_at >= @Start AND responded_at < @End
                   AND (@ExcludingRideId::uuid IS NULL OR ride_id <> @ExcludingRideId);
                """,
                new { DriverId = driverId, Start = start, End = end, ExcludingRideId = excludingRideId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>The composite primary key is the idempotency mechanism</b> (D-13, 1103's own comment), so this
    /// is an upsert and never an insert-then-update. Two replicas charging the same driver at the same
    /// instant produce one row.
    /// </para>
    /// <para>
    /// <b>The update is guarded by <c>status &lt;&gt; 'PAID'</c>.</b> A paid day is final: nothing in this
    /// service may rewrite the amount that was actually taken, because that amount is what
    /// <c>billing.journal_entries</c> already moved and what every revenue rollup sums. Without the
    /// guard a late redelivery of the *first* trip's waiver would overwrite a paid row with a zero and
    /// the money and the record would disagree.
    /// </para>
    /// <para>
    /// <b>Δ C124: the in-statement fallback cannot stand alone, and a re-read is why.</b> This method
    /// used to say that "<c>DO UPDATE</c> with no matching row still returns the existing one via the
    /// <c>RETURNING</c>-less read below", and under concurrency that is not true. Both branches can
    /// come back empty at once:
    /// <list type="number">
    /// <item>B's <c>INSERT</c> conflicts with the row A committed a moment earlier. Conflict detection
    /// re-reads, so it finds A's row — but A set <c>status = 'PAID'</c>, the <c>DO UPDATE</c> guard is
    /// false, nothing is updated and nothing is <c>RETURNING</c>ed. <c>upserted</c> is empty.</item>
    /// <item>The <c>UNION ALL</c> branch is a plain <c>SELECT</c> in the SAME statement, so it reads the
    /// statement's snapshot — taken before A committed. A's row is invisible to it.</item>
    /// </list>
    /// Zero rows, <c>QuerySingleAsync</c> throws, and the caller gets a 500. Six concurrent charges hit
    /// it about three runs in five, which is what
    /// <c>DailyFeeChargeTests.Concurrent_charges_for_one_colombo_day_take_the_fee_once</c> had been
    /// reporting intermittently as <c>Expected OK, Actual InternalServerError</c>.
    /// <para>
    /// So the fallback is a SECOND STATEMENT. A new statement takes a new snapshot in READ COMMITTED
    /// and does see the committed row. The in-statement branch is kept because it serves the ordinary
    /// redelivery — a row already PAID before this statement began — in one round trip; the re-read
    /// costs a second one only in the genuine race.
    /// </para>
    /// </para>
    /// </remarks>
    public async Task<DailyFeeCharge> UpsertAsync(DailyFeeCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var stored = await connection.QueryFirstOrDefaultAsync<DailyFeeCharge>(
            new CommandDefinition(
                $"""
                WITH upserted AS (
                  INSERT INTO billing.daily_fee_charges
                    (driver_id, vehicle_id, fee_date, fee_date_tz_at, amount_minor, currency,
                     trips_that_day, status, charged_at)
                  VALUES
                    (@DriverId, @VehicleId, @FeeDate, @FeeDateTzAt, @AmountMinor, @Currency,
                     @TripsThatDay, @Status, @ChargedAt)
                  ON CONFLICT (driver_id, vehicle_id, fee_date) DO UPDATE
                     SET amount_minor   = EXCLUDED.amount_minor,
                         trips_that_day = GREATEST(billing.daily_fee_charges.trips_that_day,
                                                   EXCLUDED.trips_that_day),
                         status         = EXCLUDED.status,
                         charged_at     = EXCLUDED.charged_at
                   WHERE billing.daily_fee_charges.status <> 'PAID'
                  RETURNING {SelectColumns})
                SELECT {SelectColumns} FROM upserted
                UNION ALL
                SELECT {SelectColumns} FROM billing.daily_fee_charges
                 WHERE driver_id = @DriverId AND vehicle_id = @VehicleId AND fee_date = @FeeDate
                   AND NOT EXISTS (SELECT 1 FROM upserted)
                 LIMIT 1;
                """,
                charge,
                cancellationToken: cancellationToken));

        if (stored is not null)
        {
            return stored;
        }

        // The race in the remark: another transaction inserted and PAID this (driver, vehicle, day)
        // after our statement's snapshot was taken, so the guard blocked the update and the snapshot
        // hid the row. A new statement sees it. Not `QueryFirstOrDefault` — by now the row must exist,
        // and a null here would mean the composite key stopped being the idempotency mechanism.
        return await connection.QuerySingleAsync<DailyFeeCharge>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.daily_fee_charges
                 WHERE driver_id = @DriverId AND vehicle_id = @VehicleId AND fee_date = @FeeDate;
                """,
                charge,
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <b>The cursor is the <c>(fee_date, vehicle_id)</c> pair, not the date alone.</b> A driver who
    /// used two vehicles on one Colombo day has two rows sharing a date, so a date-only cursor would
    /// drop whichever of them straddled a page boundary — silently, and only for the drivers US-9.6
    /// exists for. The row comparison matches the <c>ORDER BY</c> exactly, which is what makes it a
    /// position rather than a filter. The page is over-fetched by one so a full page can be told from a
    /// truncated one.
    /// </remarks>
    public async Task<IReadOnlyList<DailyFeeCharge>> HistoryAsync(
        Guid driverId,
        DateOnly? from,
        DateOnly? to,
        (DateOnly FeeDate, Guid VehicleId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DailyFeeCharge>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.daily_fee_charges
                 WHERE driver_id = @DriverId
                   AND (@From::date IS NULL OR fee_date >= @From)
                   AND (@To::date   IS NULL OR fee_date <= @To)
                   AND (@AfterDate::date IS NULL
                        OR (fee_date, vehicle_id) < (@AfterDate, @AfterVehicleId))
                 ORDER BY fee_date DESC, vehicle_id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    DriverId = driverId,
                    From = from,
                    To = to,
                    AfterDate = after?.FeeDate,
                    AfterVehicleId = after?.VehicleId ?? Guid.Empty,
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}
