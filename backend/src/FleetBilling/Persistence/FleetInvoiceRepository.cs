using Dapper;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.FleetBilling.Persistence;

/// <summary>
/// <c>billing.fleet_invoices</c> and <c>billing.fleet_invoice_lines</c> (migrations 1106, 1108).
/// </summary>
internal interface IFleetInvoiceRepository
{
    /// <summary>
    /// Raises every missing invoice and every missing line for one Colombo month. Idempotent.
    /// </summary>
    Task<InvoiceRunResult> GenerateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateOnly periodMonth,
        DateTimeOffset now,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken);

    /// <summary>Invoice ids raised by the last <see cref="GenerateAsync"/> call, for the outbox.</summary>
    Task<IReadOnlyList<FleetInvoice>> ReadByIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> invoiceIds,
        CancellationToken cancellationToken);

    /// <summary>One page of one organisation's invoices, newest month first.</summary>
    Task<IReadOnlyList<FleetInvoice>> ListAsync(
        Guid fleetId, DateOnly? before, int limit, CancellationToken cancellationToken);

    /// <summary>One invoice of one organisation, or <see langword="null"/>.</summary>
    Task<FleetInvoice?> FindAsync(Guid fleetId, Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>One invoice's per-vehicle breakdown, ordered by plate.</summary>
    Task<IReadOnlyList<FleetInvoiceLine>> ReadLinesAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>Open invoices with something to pay, oldest month first.</summary>
    Task<IReadOnlyList<FleetInvoice>> ListPayableAsync(Guid? fleetId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an invoice to PAID against a posted journal entry. Guarded on the open statuses, so a
    /// second settlement of the same invoice writes nothing and reports it.
    /// </summary>
    Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invoiceId,
        Guid journalEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the per-vehicle charges an invoice consolidated as PAID (<c>billing.monthly_subscriptions</c>).
    /// </summary>
    Task<int> MarkChargesPaidAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>Claims DUE invoices past their term and moves them to OVERDUE. Exactly-once per invoice.</summary>
    Task<IReadOnlyList<Guid>> ClaimOverdueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Claims OVERDUE invoices whose last reminder is older than <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<Guid>> ClaimRemindersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>What a dunning notice needs to say, for the invoices just claimed.</summary>
    Task<IReadOnlyList<OverdueInvoice>> ReadOverdueDetailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> invoiceIds,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetInvoiceRepository"/>
/// <remarks>
/// <para>
/// <b>Generation is three statements and no loop.</b> One insert per fleet, one insert per
/// per-vehicle charge, one recompute of the totals — set-based, so a month with ten thousand
/// vehicles costs three round trips rather than ten thousand. Every one of the three is an upsert
/// that adds what is missing and restates nothing, which is what makes "re-running invoice
/// generation for a month is idempotent" a property of the SQL rather than of a guard somebody has
/// to remember.
/// </para>
/// <para>
/// <b>Mode A never reaches a line, and the filter is not the reason.</b> A line can only exist for a
/// charge <c>billing.monthly_subscriptions</c> already carries, and 1104's only writer
/// (subscription-svc, C047) inserts <c>WHERE v.mode = 'B'</c> — so there is no Mode A row to
/// exclude. The <c>v.mode = 'B'</c> in the line insert below is a second, independent lock on the
/// same fence: if a future writer ever raised a Mode A charge, it still could not become a line.
/// Mode C is excluded by a stronger thing than either — <c>registry.fleet_vehicles.mode</c> is
/// <c>CHECK (mode IN ('A','B'))</c>, so a fleet cannot own a Mode C vehicle at all (AL-03).
/// </para>
/// <para>
/// <b>A Mode-A-only fleet gets an invoice with no lines and a zero total.</b> 1106's own table
/// comment asks for it — "the row is the evidence the run considered them" — and it is also the
/// most direct possible statement of AL-03 on the operator's own screen: the month exists, it cost
/// nothing, and no bus is listed on it.
/// </para>
/// <para>
/// <b>A settled invoice is never appended to.</b> The line insert skips invoices that are already
/// PAID, because a line added after settlement would break the invariant that Σ lines =
/// <c>total_minor</c> for the amount that was actually paid. A charge raised for a month whose
/// invoice has already been settled is therefore left unconsolidated, and the run says so — see
/// <c>InvoiceRunService</c>.
/// </para>
/// </remarks>
internal sealed class FleetInvoiceRepository(INpgsqlConnectionFactory connections) : IFleetInvoiceRepository
{
    /// <summary>
    /// The invoice projection, with the line count <c>fleet.yaml</c>'s <c>vehicleCount</c> reports.
    /// </summary>
    /// <remarks>
    /// Counted rather than stored: a column would be a second opinion about the same rows, and one
    /// that could be right when the run wrote it and wrong the moment a line was added. Money is
    /// cast to <c>bigint</c> because <c>total_minor</c> is <c>INTEGER</c> in §10 while every
    /// contract types money as int64, and Dapper's constructor binding matches parameter types
    /// exactly — an <c>Int32</c> column against an <c>Int64</c> parameter does not fail to convert,
    /// it fails to materialise the record at all.
    /// </remarks>
    private const string InvoiceColumns = """
        i.id,
        i.fleet_id,
        i.period_month,
        i.period_month_tz_at,
        i.total_minor::bigint AS total_minor,
        i.currency,
        i.status,
        i.journal_entry_id,
        i.due_at,
        i.overdue_at,
        i.settled_at,
        i.created_at,
        (SELECT count(*)::int FROM billing.fleet_invoice_lines l WHERE l.invoice_id = i.id) AS vehicle_count
        """;

    public async Task<InvoiceRunResult> GenerateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateOnly periodMonth,
        DateTimeOffset now,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // (1) One invoice per APPROVED organisation that has any vehicle on its roster. Created
        // FREE with a zero total, which is the only state that satisfies ck_fleet_invoices_free
        // before the lines exist — CHECKs are immediate, so every statement has to leave the row
        // valid on its own. Statement (3) settles the real total and status.
        var raised = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO billing.fleet_invoices
                      (fleet_id, period_month, period_month_tz_at, total_minor, currency, status, due_at)
                SELECT f.id, @PeriodMonth, @Now, 0, 'LKR', 'FREE', @DueAt
                  FROM registry.fleets f
                 WHERE f.status = 'APPROVED'
                   AND EXISTS (SELECT 1 FROM registry.fleet_vehicles fv WHERE fv.fleet_id = f.id)
                ON CONFLICT (fleet_id, period_month) DO NOTHING
                RETURNING id;
                """,
                new { PeriodMonth = periodMonth, Now = now, DueAt = dueAt },
                transaction,
                cancellationToken: cancellationToken));

        // (2) One line per per-vehicle charge, snapshotting the plate, the type and the amount as
        // they are now. Bare `ON CONFLICT DO NOTHING`, with no target: two unique constraints can
        // refuse this row and both mean "already consolidated" —
        // ux_fleet_invoice_lines_vehicle (this vehicle is on this invoice) and
        // ux_fleet_invoice_lines_charge (this charge is on *some* invoice, which is what stops a
        // vehicle that changed fleets mid-month being billed to both).
        var lines = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO billing.fleet_invoice_lines
                      (invoice_id, vehicle_id, monthly_subscription_id,
                       registration_number, vehicle_type, amount_minor, currency, status)
                SELECT i.id,
                       ms.vehicle_id,
                       ms.id,
                       v.registration_number,
                       v.vehicle_type,
                       ms.amount_minor,
                       ms.currency,
                       ms.status
                  FROM billing.monthly_subscriptions ms
                  JOIN registry.vehicles v ON v.id = ms.vehicle_id
                  JOIN registry.fleet_vehicles fv ON fv.vehicle_id = ms.vehicle_id
                  JOIN billing.fleet_invoices i
                    ON i.fleet_id = fv.fleet_id AND i.period_month = ms.period_month
                 WHERE ms.period_month = @PeriodMonth
                   AND v.mode = 'B'
                   AND ms.status IN ('FREE','DUE')
                   AND i.status <> 'PAID'
                ON CONFLICT DO NOTHING
                RETURNING invoice_id;
                """,
                new { PeriodMonth = periodMonth },
                transaction,
                cancellationToken: cancellationToken));

        // (3) Σ lines, written onto the invoice. Unconditional for every unsettled invoice of the
        // month rather than only for the ones that changed: the write is deterministic, and a
        // conditional would need to reproduce the FREE/DUE rule in its own predicate.
        // An OVERDUE invoice keeps its status — dunning has already been signalled and a vehicle
        // added afterwards does not un-say it.
        var totals = await connection.QueryAsync<(Guid Id, long TotalMinor)>(
            new CommandDefinition(
                """
                UPDATE billing.fleet_invoices i
                   SET total_minor = sub.total,
                       status = CASE
                                  WHEN sub.total = 0 THEN 'FREE'
                                  WHEN i.status = 'OVERDUE' THEN 'OVERDUE'
                                  ELSE 'DUE'
                                END
                  FROM (SELECT fi.id,
                               coalesce((SELECT sum(l.amount_minor)
                                           FROM billing.fleet_invoice_lines l
                                          WHERE l.invoice_id = fi.id), 0)::int AS total
                          FROM billing.fleet_invoices fi
                         WHERE fi.period_month = @PeriodMonth
                           AND fi.status <> 'PAID') sub
                 WHERE i.id = sub.id
                RETURNING i.id, i.total_minor::bigint AS total_minor;
                """,
                new { PeriodMonth = periodMonth },
                transaction,
                cancellationToken: cancellationToken));

        var raisedIds = raised.ToArray();
        var lineRows = lines.ToArray();
        var totalRows = totals.ToArray();
        var raisedSet = raisedIds.ToHashSet();

        return new InvoiceRunResult(
            periodMonth,
            raisedIds,
            lineRows.Length,
            totalRows.Where(row => raisedSet.Contains(row.Id)).Sum(row => row.TotalMinor));
    }

    public async Task<IReadOnlyList<FleetInvoice>> ReadByIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(invoiceIds);

        if (invoiceIds.Count == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<FleetInvoice>(
            new CommandDefinition(
                $"""
                SELECT {InvoiceColumns}
                  FROM billing.fleet_invoices i
                 WHERE i.id = ANY(@Ids)
                 ORDER BY i.created_at, i.id;
                """,
                new { Ids = invoiceIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// Keyset on <c>period_month</c> alone, which is safe here and nowhere else in this codebase:
    /// <c>ux_fleet_invoices_fleet_period</c> makes (fleet, month) unique, so within one
    /// organisation the cursor column has no ties to straddle.
    /// </remarks>
    public async Task<IReadOnlyList<FleetInvoice>> ListAsync(
        Guid fleetId, DateOnly? before, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FleetInvoice>(
            new CommandDefinition(
                $"""
                SELECT {InvoiceColumns}
                  FROM billing.fleet_invoices i
                 WHERE i.fleet_id = @FleetId
                   AND (@Before::date IS NULL OR i.period_month < @Before)
                 ORDER BY i.period_month DESC
                 LIMIT @Limit;
                """,
                new { FleetId = fleetId, Before = before, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// The fleet is in the predicate, not checked afterwards: an invoice that belongs to another
    /// organisation must be a 404 and not a row this service then decides to hide, or a mistake in
    /// the handler turns into a cross-org read.
    /// </remarks>
    public async Task<FleetInvoice?> FindAsync(Guid fleetId, Guid invoiceId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetInvoice>(
            new CommandDefinition(
                $"""
                SELECT {InvoiceColumns}
                  FROM billing.fleet_invoices i
                 WHERE i.id = @InvoiceId AND i.fleet_id = @FleetId;
                """,
                new { FleetId = fleetId, InvoiceId = invoiceId },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FleetInvoiceLine>> ReadLinesAsync(
        Guid invoiceId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FleetInvoiceLine>(
            new CommandDefinition(
                """
                SELECT vehicle_id,
                       registration_number,
                       vehicle_type,
                       amount_minor::bigint AS amount_minor,
                       currency,
                       status
                  FROM billing.fleet_invoice_lines
                 WHERE invoice_id = @InvoiceId
                 ORDER BY registration_number;
                """,
                new { InvoiceId = invoiceId },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<FleetInvoice>> ListPayableAsync(
        Guid? fleetId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FleetInvoice>(
            new CommandDefinition(
                $"""
                SELECT {InvoiceColumns}
                  FROM billing.fleet_invoices i
                 WHERE i.status IN ('DUE','OVERDUE')
                   AND i.total_minor > 0
                   AND (@FleetId::uuid IS NULL OR i.fleet_id = @FleetId)
                 ORDER BY i.period_month, i.created_at
                 LIMIT @Limit;
                """,
                new { FleetId = fleetId, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// <c>WHERE status IN ('DUE','OVERDUE')</c> is the claim. Two replicas settling one invoice at
    /// the same instant both post — but the ledger's UNIQUE <c>idempotency_key</c> means the second
    /// one gets the first one's entry back and moves no money, and this guard means the second one
    /// writes no row. The order matters and is the caller's: debit first, record second (C047).
    /// </remarks>
    public async Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invoiceId,
        Guid journalEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var updated = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.fleet_invoices
                   SET status = 'PAID',
                       journal_entry_id = @JournalEntryId,
                       settled_at = @SettledAt
                 WHERE id = @InvoiceId
                   AND status IN ('DUE','OVERDUE');
                """,
                new { InvoiceId = invoiceId, JournalEntryId = journalEntryId, SettledAt = settledAt },
                transaction,
                cancellationToken: cancellationToken));

        return updated == 1;
    }

    /// <remarks>
    /// <b>The one column this service writes outside its own tables.</b> subscription-svc (C047)
    /// raises <c>billing.monthly_subscriptions</c> and its CLAUDE.md calls itself that table's only
    /// writer — but it raises rows as FREE or DUE and has no route that collects one, and its own
    /// handoff hands the fleet half here. A row that stays DUE for ever would leave
    /// <c>ix_monthly_subs_due</c> growing without bound and would tell the Fleet Portal that a
    /// vehicle on a settled invoice still owes for the month. <c>WHERE ms.status = 'DUE'</c>
    /// narrows it to exactly that transition: a FREE row is left alone, because a month that cost
    /// nothing was not paid for.
    /// </remarks>
    public Task<int> MarkChargesPaidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.monthly_subscriptions ms
                   SET status = 'PAID'
                  FROM billing.fleet_invoice_lines l
                 WHERE l.invoice_id = @InvoiceId
                   AND ms.id = l.monthly_subscription_id
                   AND ms.status = 'DUE';
                """,
                new { InvoiceId = invoiceId },
                transaction,
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>FOR UPDATE SKIP LOCKED</c> inside the sub-select, and the status in the outer predicate:
    /// the pair is what makes the OVERDUE transition exactly-once across replicas, which matters
    /// because each claimed row becomes a push to an operator's phone. The same shape C051's ack
    /// sweep and C059's alarm claim use.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> ClaimOverdueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                UPDATE billing.fleet_invoices
                   SET status = 'OVERDUE', overdue_at = @Now, last_dunned_at = @Now
                 WHERE id IN (
                         SELECT id FROM billing.fleet_invoices
                          WHERE status = 'DUE'
                            AND total_minor > 0
                            AND due_at IS NOT NULL
                            AND due_at <= @Now
                          ORDER BY due_at
                          LIMIT @Limit
                            FOR UPDATE SKIP LOCKED)
                RETURNING id;
                """,
                new { Now = now, Limit = limit },
                transaction,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<Guid>> ClaimRemindersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                UPDATE billing.fleet_invoices
                   SET last_dunned_at = @Now
                 WHERE id IN (
                         SELECT id FROM billing.fleet_invoices
                          WHERE status = 'OVERDUE'
                            AND (last_dunned_at IS NULL OR last_dunned_at <= @Cutoff)
                          ORDER BY last_dunned_at NULLS FIRST
                          LIMIT @Limit
                            FOR UPDATE SKIP LOCKED)
                RETURNING id;
                """,
                new { Now = now, Cutoff = cutoff, Limit = limit },
                transaction,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// The recipients are the organisation's <b>Owners</b>, from <c>iam.fleet_members</c>. Not
    /// every member: US-13.A5 gives billing to the Owner and takes it away from the Manager in the
    /// same sentence, so pushing a Manager about a bill they cannot pay is telling the wrong person.
    /// The organisation's <c>owner_id</c> is included even if no membership row names them, because
    /// C058 writes both and the registrant is who the platform has a relationship with.
    /// </remarks>
    public async Task<IReadOnlyList<OverdueInvoice>> ReadOverdueDetailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(invoiceIds);

        if (invoiceIds.Count == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<(
            Guid InvoiceId, Guid FleetId, string FleetName, DateOnly PeriodMonth, long TotalMinor,
            DateTimeOffset DueAt, Guid[] OwnerIds)>(
            new CommandDefinition(
                """
                SELECT i.id AS invoice_id,
                       i.fleet_id,
                       f.name AS fleet_name,
                       i.period_month,
                       i.total_minor::bigint AS total_minor,
                       i.due_at,
                       ARRAY(
                         SELECT DISTINCT u FROM (
                           SELECT f.owner_id AS u
                           UNION
                           SELECT m.user_id FROM iam.fleet_members m
                            WHERE m.fleet_id = i.fleet_id AND m.fleet_role = 'owner'
                         ) owners WHERE u IS NOT NULL) AS owner_ids
                  FROM billing.fleet_invoices i
                  JOIN registry.fleets f ON f.id = i.fleet_id
                 WHERE i.id = ANY(@Ids);
                """,
                new { Ids = invoiceIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new OverdueInvoice(
                row.InvoiceId,
                row.FleetId,
                row.FleetName,
                row.PeriodMonth,
                row.TotalMinor,
                row.DueAt,
                row.OwnerIds)),
        ];
    }
}
