using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Persistence;

/// <summary>The window a finance read is bounded by — half-open, Asia/Colombo business dates (D-38).</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Inclusive. The query adds the day, so the caller never has to remember to.</param>
public sealed record FinanceWindow(DateOnly From, DateOnly To);

/// <summary>
/// SCR-AP-006's read models: gateway settlement, refunds, the transactions report, and the two
/// queues that are neither (E-05, R-19, E-03, E-07, US-9A.15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only — this repository has no <c>INSERT</c> and no <c>UPDATE</c>, and that is the C065
/// fence as a class.</b> Every movement of money on this surface is executed by the service that
/// owns the table: wallet-svc posts the reversal (it is the only writer of
/// <c>billing.journal_postings</c>), fare-svc raises the refund and its balanced entry, reputation-svc
/// resolves a fraud flag. What is written here is the <c>audit.events</c> row, which is admin-bff's
/// own table. A Finance back-office that could move money itself would be a second writer over the
/// one thing the platform is least able to reconcile after the fact.
/// </para>
/// <para>
/// <b>Every window is bounded and every list is paged.</b> `billing.journal_entries` and
/// `billing.topups` grow for ever; a report with no date filter is a report that gets slower every
/// day until somebody notices. The date filter is required by the contract for exactly that reason
/// and is enforced here as a bound parameter on an indexed column, never as a post-filter.
/// </para>
/// <para>
/// <b>Business dates are Colombo's, computed in the database (D-38).</b> `AT TIME ZONE
/// 'Asia/Colombo'` on the stored instant, the same expression `billing.daily_fee_charges.fee_date`
/// defaults to — so a top-up at 23:00 UTC belongs to the next Sri Lankan day on this screen and on
/// the driver's, rather than to whichever day the reporting process happened to be running in.
/// </para>
/// </remarks>
public interface IFinanceRepository
{
    /// <summary>Per-rail, per-day settlement against the ledger (D6' §7.2).</summary>
    Task<IReadOnlyList<SettlementDayRow>> SettlementAsync(
        FinanceWindow window, string? method, CancellationToken cancellationToken);

    /// <summary>The gateway sessions that need a human, oldest first.</summary>
    /// <param name="staleBefore">
    /// A <c>Pending</c> session opened before this instant is <see cref="SettlementExceptionKinds.Unsettled"/>.
    /// </param>
    Task<IReadOnlyList<SettlementExceptionRow>> SettlementExceptionsAsync(
        string? kind, DateTimeOffset staleBefore, int limit, CancellationToken cancellationToken);

    /// <summary>The refund queue — raised refunds and R-19's unraised <c>Overpaid</c> payments.</summary>
    Task<IReadOnlyList<RefundQueueRow>> RefundQueueAsync(
        string? source, string? status, int limit, CancellationToken cancellationToken);

    /// <summary>One payment attempt as the refund decision needs to see it, or null.</summary>
    Task<RefundQueueRow?> FindPaymentAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>The four-kind transactions report, newest first.</summary>
    Task<IReadOnlyList<TransactionRow>> TransactionsAsync(
        FinanceWindow window, string? kind, Guid? partyId, int limit, CancellationToken cancellationToken);

    /// <summary>Documents inside <paramref name="withinDays"/> of expiry, or already past it (E-03).</summary>
    Task<IReadOnlyList<DocumentExpiryRow>> DocumentExpiryQueueAsync(
        int withinDays, string? kind, int limit, CancellationToken cancellationToken);

    /// <summary>The E-07 anti-collusion review queue (<c>fraud.suspected</c>).</summary>
    Task<IReadOnlyList<FraudFlagRow>> FraudQueueAsync(
        string? status, string? kind, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFinanceRepository"/>
internal sealed class FinanceRepository(INpgsqlConnectionFactory connections) : IFinanceRepository
{
    /// <summary>Asia/Colombo, spelled once (D-38).</summary>
    private const string ColomboZone = "Asia/Colombo";

    // ---------------------------------------------------------------------------------------
    // Gateway settlement reconciliation (D6' §7.1/§7.2, AL-05)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b><c>billing.topups</c> is the whole of "gateway settlement" on this platform, and that is a
    /// consequence of AL-57 rather than a simplification.</b> `onepay` and platform-merchant
    /// `lankaqr` were removed as <em>ride</em> payment methods — a card fare had nowhere to land, so
    /// card acceptance moved one step earlier to a wallet top-up where MageRide legitimately is the
    /// payee. What OnePay and LankaQR settle is therefore top-ups, and `ck_topups_method` admits
    /// exactly those two (AL-05: there is no bank-transfer rail to reconcile).
    /// </para>
    /// <para>
    /// <b><c>posted</c> is read off the ledger's own credit leg, not off the session.</b> Summing
    /// <c>amount_minor</c> for settled sessions twice under two names would reconcile a number
    /// against itself and agree every time. The join goes session → its journal entry → the positive
    /// posting, so the two figures come from the two systems that are supposed to agree.
    /// </para>
    /// </remarks>
    private const string SettlementSql =
        """
        SELECT (t.created_at AT TIME ZONE @Zone)::date            AS "BusinessDate",
               t.method                                           AS "Method",
               count(*)::int                                      AS "OpenedCount",
               count(*) FILTER (WHERE t.state = 'Succeeded')::int AS "SettledCount",
               count(*) FILTER (WHERE t.state = 'Failed')::int    AS "FailedCount",
               count(*) FILTER (WHERE t.state = 'Pending')::int   AS "PendingCount",
               COALESCE(sum(t.amount_minor) FILTER (WHERE t.state = 'Succeeded'), 0)::bigint
                                                                  AS "SettledMinor",
               COALESCE(sum(p.posted_minor), 0)::bigint           AS "PostedMinor",
               max(t.currency)                                    AS "Currency"
          FROM billing.topups t
          LEFT JOIN LATERAL (
                SELECT sum(jp.amount_minor)::bigint AS posted_minor
                  FROM billing.journal_postings jp
                  JOIN billing.accounts a ON a.id = jp.account_id
                 WHERE jp.entry_id = t.journal_entry_id
                   AND jp.amount_minor > 0
                   AND a.id = t.account_id) p ON true
         WHERE (t.created_at AT TIME ZONE @Zone)::date BETWEEN @From AND @To
           AND (@Method::text IS NULL OR t.method = @Method)
         GROUP BY 1, 2
         ORDER BY 1 DESC, 2;
        """;

    public async Task<IReadOnlyList<SettlementDayRow>> SettlementAsync(
        FinanceWindow window, string? method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SettlementDayRow>(new CommandDefinition(
            SettlementSql,
            new { Zone = ColomboZone, window.From, window.To, Method = method },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// <para>
    /// <b>The classification is derived from the row, because nothing records it.</b> wallet-svc's
    /// <c>TopupService.SettleAsync</c> refuses an amount that disagrees with its session, logs the
    /// numbers and leaves the session <c>Pending</c> — there is no exception column and adding one
    /// would give this component a write on another service's table. So the four kinds are a
    /// <c>CASE</c> over the state, the ledger and the clock, and a session that resolves itself
    /// leaves the queue without anybody having to close it.
    /// </para>
    /// <para>
    /// <b>The order of the branches is the order of severity.</b> A settled session whose ledger
    /// figure disagrees is the one that costs money and is tested first; a session with no posting at
    /// all is next; the two "the gateway and we never finished talking" cases are last.
    /// </para>
    /// </remarks>
    private const string SettlementExceptionsSql =
        """
        WITH classified AS (
          SELECT t.id, t.method, t.state, t.driver_id, t.amount_minor, t.currency,
                 t.provider_transaction_id, t.provider_order_id, t.failure_reason,
                 t.created_at, t.settled_at, p.posted_minor,
                 CASE
                   WHEN t.state = 'Succeeded' AND p.posted_minor IS NOT NULL
                        AND p.posted_minor <> t.amount_minor          THEN 'amount-mismatch'
                   WHEN t.state = 'Succeeded' AND p.posted_minor IS NULL THEN 'settled-not-posted'
                   WHEN t.state = 'Pending'   AND t.created_at < @StaleBefore THEN 'unsettled'
                   WHEN t.state = 'Failed'    AND t.provider_transaction_id IS NOT NULL
                                                                      THEN 'gateway-failed'
                 END AS kind
            FROM billing.topups t
            LEFT JOIN LATERAL (
                  SELECT sum(jp.amount_minor)::bigint AS posted_minor
                    FROM billing.journal_postings jp
                   WHERE jp.entry_id = t.journal_entry_id
                     AND jp.account_id = t.account_id) p ON true
        )
        SELECT c.id                       AS "TopupId",
               c.kind                     AS "Kind",
               c.method                   AS "Method",
               c.state                    AS "State",
               c.driver_id                AS "DriverId",
               u.first_name               AS "DriverName",
               c.amount_minor::bigint     AS "AmountMinor",
               c.posted_minor::bigint     AS "PostedMinor",
               c.currency                 AS "Currency",
               c.provider_transaction_id  AS "ProviderTransactionId",
               c.provider_order_id        AS "ProviderOrderId",
               c.failure_reason           AS "FailureReason",
               c.created_at               AS "CreatedAt",
               c.settled_at               AS "SettledAt"
          FROM classified c
          LEFT JOIN iam.users u ON u.id = c.driver_id
         WHERE c.kind IS NOT NULL
           AND (@Kind::text IS NULL OR c.kind = @Kind)
         ORDER BY c.created_at
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<SettlementExceptionRow>> SettlementExceptionsAsync(
        string? kind, DateTimeOffset staleBefore, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SettlementExceptionRow>(new CommandDefinition(
            SettlementExceptionsSql,
            new { Kind = kind, StaleBefore = staleBefore, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    // ---------------------------------------------------------------------------------------
    // Refund queue (E-05, R-19 · ADD §11.14)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>Two populations, one queue, and the union is the point.</b> §11.14's late callback writes
    /// <c>fares.ride_payments.state = 'Overpaid'</c> <em>and</em> a
    /// <c>fares.refunds(kind='overpaid_reversal')</c> row — but a rider-initiated dispute produces a
    /// refund with no Overpaid payment, and a payment that reached Overpaid before the refund row was
    /// written (or whose refund row was never written, which is the R-19 failure the queue exists to
    /// catch) produces the reverse. Showing only <c>fares.refunds</c> would hide exactly the case
    /// that needs an operator.
    /// </para>
    /// <para>
    /// <b>The Overpaid half excludes payments a refund already covers</b>, so a normally-handled
    /// late callback appears once — as the refund it became — rather than twice.
    /// </para>
    /// </remarks>
    private const string RefundQueueSql =
        """
        WITH raised AS (
          SELECT f.id           AS refund_id,
                 'refund'::text AS source,
                 f.ride_payment_id, f.kind, f.status, f.amount_minor::bigint AS amount_minor, f.currency,
                 f.reason_code, f.provider_refund_id, f.requested_at, f.settled_at
            FROM fares.refunds f
           WHERE (@Status::text IS NULL OR f.status = @Status)
             AND (@Status::text IS NOT NULL OR f.status IN ('Requested','Submitted'))
             AND (@Source::text IS NULL OR @Source = 'refund')
           ORDER BY f.requested_at
           LIMIT @Limit
        ),
        overpaid AS (
          SELECT NULL::uuid         AS refund_id,
                 'overpaid'::text   AS source,
                 rp.id              AS ride_payment_id,
                 NULL::text         AS kind,
                 NULL::text         AS status,
                 rp.amount_minor::bigint AS amount_minor, rp.currency,
                 NULL::text         AS reason_code,
                 NULL::text         AS provider_refund_id,
                 rp.created_at      AS requested_at,
                 NULL::timestamptz  AS settled_at
            FROM fares.ride_payments rp
           WHERE rp.state = 'Overpaid'
             AND (@Source::text IS NULL OR @Source = 'overpaid')
             AND @Status::text IS NULL
             AND NOT EXISTS (SELECT 1 FROM fares.refunds f WHERE f.ride_payment_id = rp.id)
           ORDER BY rp.created_at
           LIMIT @Limit
        ),
        queue AS (SELECT * FROM raised UNION ALL SELECT * FROM overpaid)
        SELECT q.refund_id            AS "RefundId",
               q.source               AS "Source",
               q.ride_payment_id      AS "PaymentId",
               rp.ride_id             AS "RideId",
               rp.state               AS "PaymentState",
               rp.method              AS "Method",
               q.kind                 AS "Kind",
               q.status               AS "Status",
               q.amount_minor::bigint AS "AmountMinor",
               rp.amount_minor::bigint AS "PaymentAmountMinor",
               q.currency             AS "Currency",
               q.reason_code          AS "ReasonCode",
               q.provider_refund_id   AS "ProviderRefundId",
               r.passenger_id         AS "PassengerId",
               u.first_name           AS "PassengerName",
               q.requested_at         AS "RequestedAt",
               q.settled_at           AS "SettledAt"
          FROM queue q
          JOIN fares.ride_payments rp ON rp.id = q.ride_payment_id
          LEFT JOIN rides.rides r     ON r.id = rp.ride_id
          LEFT JOIN iam.users u       ON u.id = r.passenger_id
         ORDER BY q.requested_at
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<RefundQueueRow>> RefundQueueAsync(
        string? source, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<RefundQueueRow>(new CommandDefinition(
            RefundQueueSql,
            new { Source = source, Status = status, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// Read before the decision is forwarded, for the audit row's <c>before</c> image: fare-svc's
    /// answer says what the refund became and this says what it was raised against. An operator
    /// reading the trail a year later has both halves without joining two services' tables.
    /// </remarks>
    private const string FindPaymentSql =
        """
        SELECT f.id                  AS "RefundId",
               CASE WHEN f.id IS NULL THEN 'overpaid' ELSE 'refund' END AS "Source",
               rp.id                 AS "PaymentId",
               rp.ride_id            AS "RideId",
               rp.state              AS "PaymentState",
               rp.method             AS "Method",
               f.kind                AS "Kind",
               f.status              AS "Status",
               COALESCE(f.amount_minor, rp.amount_minor)::bigint AS "AmountMinor",
               rp.amount_minor::bigint AS "PaymentAmountMinor",
               rp.currency           AS "Currency",
               f.reason_code         AS "ReasonCode",
               f.provider_refund_id  AS "ProviderRefundId",
               r.passenger_id        AS "PassengerId",
               u.first_name          AS "PassengerName",
               COALESCE(f.requested_at, rp.created_at) AS "RequestedAt",
               f.settled_at          AS "SettledAt"
          FROM fares.ride_payments rp
          LEFT JOIN LATERAL (
                SELECT * FROM fares.refunds fr
                 WHERE fr.ride_payment_id = rp.id
                 ORDER BY fr.requested_at DESC LIMIT 1) f ON true
          LEFT JOIN rides.rides r ON r.id = rp.ride_id
          LEFT JOIN iam.users u   ON u.id = r.passenger_id
         WHERE rp.id = @PaymentId;
        """;

    public async Task<RefundQueueRow?> FindPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<RefundQueueRow>(new CommandDefinition(
            FindPaymentSql, new { PaymentId = paymentId }, cancellationToken: cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Transactions report (US-9A.15)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>Read from the journal, not from <c>billing.wallet_transactions</c>.</b> The projection is
    /// one row per <em>account leg</em>, so a driver-to-driver transfer appears twice and a report
    /// that summed it would double the platform's transfer volume. The journal is one row per money
    /// <em>event</em>, which is what a transactions report is a list of — and
    /// <c>ix_journal_entries_kind(kind, ts DESC)</c> (1101) is exactly this query's index.
    /// </para>
    /// <para>
    /// <b>The two parties are the two legs, named by sign.</b> Σ legs = 0 is guaranteed by
    /// <c>trg_balanced</c>, so every entry has a negative side and a positive side whatever the kind:
    /// a top-up is platform → driver, a daily fee is driver → platform, a voucher purchase is
    /// platform → buyer, a transfer is sender → recipient. One projection covers all four rather than
    /// four <c>CASE</c> arms that would have to be revisited every time a kind is added.
    /// </para>
    /// </remarks>
    private const string TransactionsSql =
        """
        WITH page AS (
          SELECT e.id, e.kind, e.description, e.ts
            FROM billing.journal_entries e
           WHERE e.kind = ANY(@Kinds)
             AND (e.ts AT TIME ZONE @Zone)::date BETWEEN @From AND @To
           ORDER BY e.ts DESC, e.id DESC
           LIMIT @Limit
        ),
        legs AS (
          SELECT p.id, p.kind, p.description, p.ts,
                 max(jp.amount_minor) AS amount_minor,
                 (array_agg(a.owner_id  ORDER BY jp.amount_minor))[1] AS from_party,
                 (array_agg(a.owner_type ORDER BY jp.amount_minor))[1] AS from_type,
                 (array_agg(a.owner_id  ORDER BY jp.amount_minor DESC))[1] AS to_party,
                 (array_agg(a.owner_type ORDER BY jp.amount_minor DESC))[1] AS to_type,
                 (array_agg(a.currency  ORDER BY jp.amount_minor DESC))[1] AS currency
            FROM page p
            JOIN billing.journal_postings jp ON jp.entry_id = p.id
            JOIN billing.accounts a          ON a.id = jp.account_id
           GROUP BY p.id, p.kind, p.description, p.ts
        )
        SELECT l.id                             AS "EntryId",
               l.kind                           AS "Kind",
               COALESCE(l.amount_minor, 0)::bigint AS "AmountMinor",
               COALESCE(l.currency, 'LKR')      AS "Currency",
               l.from_party                     AS "FromPartyId",
               fu.first_name                    AS "FromName",
               COALESCE(l.from_type, 'platform') AS "FromAccountType",
               l.to_party                       AS "ToPartyId",
               tu.first_name                    AS "ToName",
               COALESCE(l.to_type, 'platform')  AS "ToAccountType",
               l.description                    AS "Description",
               l.ts                             AS "Ts"
          FROM legs l
          LEFT JOIN iam.users fu ON fu.id = l.from_party
          LEFT JOIN iam.users tu ON tu.id = l.to_party
         WHERE (@PartyId::uuid IS NULL OR l.from_party = @PartyId OR l.to_party = @PartyId)
         ORDER BY l.ts DESC, l.id DESC;
        """;

    public async Task<IReadOnlyList<TransactionRow>> TransactionsAsync(
        FinanceWindow window, string? kind, Guid? partyId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<TransactionRow>(new CommandDefinition(
            TransactionsSql,
            new
            {
                Zone = ColomboZone,
                window.From,
                window.To,
                Kinds = kind is null ? TransactionKinds.All.ToArray() : new[] { kind },
                PartyId = partyId,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    // ---------------------------------------------------------------------------------------
    // Document expiry (E-03, AL-10)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>Only the current document of each (subject, kind) is in the queue.</b> A renewal
    /// supersedes rather than replaces — 0312's own comment — so the superseded row stays behind with
    /// a date in the past, and a queue that listed every expired row would show an operator a
    /// backlog of paperwork that has already been dealt with. The <c>DISTINCT ON</c> keeps the newest
    /// per <c>(vehicle, kind)</c>, or per <c>(driver, kind)</c> for the vehicle-less identity
    /// documents AL-27 leaves without one.
    /// </para>
    /// <para>
    /// <b><c>REJECTED</c> documents are excluded and expiry is not the reason.</b> A refused document
    /// is C063's queue, not this one; putting it here would make one upload appear on two officers'
    /// screens under two different verbs.
    /// </para>
    /// </remarks>
    private const string DocumentExpirySql =
        """
        WITH current AS (
          SELECT DISTINCT ON (COALESCE(d.vehicle_id, d.driver_id, d.fleet_id), d.kind)
                 d.id, d.kind, d.status, d.expires_at, d.driver_id, d.fleet_id, d.vehicle_id
            FROM registry.documents d
           WHERE d.expires_at IS NOT NULL
             AND d.status <> 'REJECTED'
           ORDER BY COALESCE(d.vehicle_id, d.driver_id, d.fleet_id), d.kind, d.created_at DESC
        )
        SELECT c.id                                       AS "DocId",
               c.kind                                     AS "Kind",
               c.status                                   AS "Status",
               c.expires_at                               AS "ExpiresAt",
               (((c.expires_at AT TIME ZONE @Zone)::date)
                 - ((now() AT TIME ZONE @Zone)::date))::int AS "DaysRemaining",
               n.threshold_days                           AS "ThresholdDays",
               c.driver_id                                AS "DriverId",
               du.first_name                              AS "DriverName",
               c.fleet_id                                 AS "FleetId",
               fl.name                                    AS "FleetName",
               c.vehicle_id                               AS "VehicleId",
               v.registration_number                      AS "RegNo",
               v.dispatch_state                           AS "DispatchState"
          FROM current c
          LEFT JOIN iam.users du         ON du.id = c.driver_id
          LEFT JOIN registry.fleets fl   ON fl.id = c.fleet_id
          LEFT JOIN registry.vehicles v  ON v.id = c.vehicle_id
          LEFT JOIN LATERAL (
                SELECT min(dn.threshold_days)::smallint AS threshold_days
                  FROM registry.document_notices dn
                 WHERE dn.document_id = c.id) n ON true
         WHERE c.expires_at < (now() + make_interval(days => @WithinDays))
           AND (@Kind::text IS NULL OR c.kind = @Kind)
         ORDER BY c.expires_at
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<DocumentExpiryRow>> DocumentExpiryQueueAsync(
        int withinDays, string? kind, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DocumentExpiryRow>(new CommandDefinition(
            DocumentExpirySql,
            new { Zone = ColomboZone, WithinDays = withinDays, Kind = kind, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    // ---------------------------------------------------------------------------------------
    // Fraud review (E-07)
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// <para>
    /// <b>Read directly, and the decision is not.</b> reputation-svc owns
    /// <c>reputation.fraud_flags</c> and exposes both <c>GET /v1/admin/reputation/flags</c> and the
    /// <c>/resolve</c> that moves a flag off <c>open</c>; the gateway routes both there at Order 20.
    /// What that surface cannot do is put a <em>name</em> beside a subject id — reputation-svc holds
    /// no identity — and a queue of bare UUIDs is a queue nobody can work. So the review list is
    /// joined here, exactly as the three directories join five other services' tables, and the button
    /// on the row posts to reputation-svc's own resolve route. One reader, one writer, no second
    /// opinion about what a flag means.
    /// </para>
    /// <para>
    /// <c>ix_fraud_flags_status(status, ts DESC)</c> (migration 0804) is this query's index, and its
    /// comment names this screen.
    /// </para>
    /// </remarks>
    private const string FraudQueueSql =
        """
        SELECT f.id               AS "FlagId",
               f.kind             AS "Kind",
               f.status           AS "Status",
               f.subject_id       AS "SubjectId",
               f.subject_type     AS "SubjectType",
               su.first_name      AS "SubjectName",
               f.related_id       AS "RelatedId",
               ru.first_name      AS "RelatedName",
               f.window_key       AS "WindowKey",
               f.detail::text     AS "Detail",
               f.resolved_by      AS "ResolvedBy",
               f.resolved_at      AS "ResolvedAt",
               f.ts               AS "Ts"
          FROM reputation.fraud_flags f
          LEFT JOIN iam.users su ON su.id = f.subject_id
          LEFT JOIN iam.users ru ON ru.id = f.related_id
         WHERE f.status = @Status
           AND (@Kind::text IS NULL OR f.kind = @Kind)
         ORDER BY f.ts DESC
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<FraudFlagRow>> FraudQueueAsync(
        string? status, string? kind, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FraudFlagRow>(new CommandDefinition(
            FraudQueueSql,
            // Open by default: a review queue's job is what has not been reviewed, and the closed
            // history is a filter away rather than the thing an operator has to page past.
            new { Status = status ?? "open", Kind = kind, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
