using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Subscriptions.Persistence;

/// <summary>One vehicle's platform charge for one Colombo month, with the fleet that owns it.</summary>
/// <param name="FleetId">
/// <see langword="null"/> for an individually-owned Mode B vehicle. A fleet's lines are what C060
/// consolidates into a <c>billing.fleet_invoices</c> row with a per-vehicle breakdown (AL-03).
/// </param>
public sealed record ModeBCharge(
    Guid VehicleId,
    string RegistrationNumber,
    string VehicleType,
    Guid OwnerId,
    Guid? FleetId,
    DateOnly PeriodMonth,
    long AmountMinor,
    string Currency,
    string Status);

/// <summary>What one run of the monthly charge raised.</summary>
public sealed record ModeBRunResult(DateOnly PeriodMonth, int Raised, int FreeMonths, long TotalMinor);

/// <summary><c>billing.monthly_subscriptions</c> — the PLATFORM's Mode B charge (AL-03, D5' §2.1).</summary>
internal interface IModeBBillingRepository
{
    /// <summary>Raises every missing per-vehicle charge for a Colombo month. Idempotent.</summary>
    Task<ModeBRunResult> RaiseMonthAsync(
        DateOnly periodMonth, long feeMinor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The month's charge lines, optionally narrowed to one fleet.</summary>
    Task<IReadOnlyList<ModeBCharge>> ListAsync(
        DateOnly periodMonth, Guid? fleetId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IModeBBillingRepository"/>
/// <remarks>
/// <para>
/// <b>This service raises the charge and posts nothing.</b> §10 gives
/// <c>billing.monthly_subscriptions</c> no <c>journal_entry_id</c> while giving
/// <c>billing.fleet_invoices</c> one, and the journal <c>kind</c> vocabulary has no value a monthly
/// platform fee could be recorded under — so the per-vehicle row is deliberately unledgered and the
/// consolidated invoice is where the money is. That invoice, the fleet wallet it settles against and
/// the per-vehicle breakdown are <b>C060 fleet-billing-svc's</b> deliverables; this is the hand-off
/// they consume.
/// </para>
/// <para>
/// <b>Mode A contributes nothing and is not a zero line.</b> <c>WHERE v.mode = 'B'</c> is inside the
/// insert's SELECT, so a Mode A vehicle never gets a row at all — AL-03's "Mode A vehicles never appear
/// as a charged line", held by the row not existing rather than by a filter somebody could forget
/// downstream. Mode C is never fleet-billed either: those drivers pay the daily fee from their own
/// wallet.
/// </para>
/// </remarks>
internal sealed class ModeBBillingRepository(INpgsqlConnectionFactory connections) : IModeBBillingRepository
{
    /// <summary>
    /// The Colombo month a vehicle was registered in — the one month it is billed nothing for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "First month free" (§20) has to be anchored to something durable, and the only candidate §10
    /// offers is the vehicle's own age: 1104's comment expects the FREE row to be "seeded per vehicle at
    /// registration", but registry-svc (C029) creates no billing row and knows nothing about money.
    /// Deriving it from <c>created_at</c> gets the same answer without a second writer, and — the part
    /// that matters — gets it right whenever billing is first switched on. Anchoring to "the first row
    /// this run creates" would hand a free month to every vehicle already on the platform the day the
    /// runner is deployed.
    /// </para>
    /// <para>
    /// The <c>&lt;=</c> in the WHERE is what stops the run billing a vehicle for a month that ended
    /// before it existed.
    /// </para>
    /// </remarks>
    private const string FirstMonth =
        "date_trunc('month', v.created_at AT TIME ZONE 'Asia/Colombo')::date";

    public async Task<ModeBRunResult> RaiseMonthAsync(
        DateOnly periodMonth, long feeMinor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // ON CONFLICT DO NOTHING, never DO UPDATE: a charge already raised for a month is that month's
        // answer, and re-running must not restate it. That is both "re-running invoice generation for a
        // month is idempotent" and the no-retro-billing rule the daily fee holds the same way — a rate
        // change reaches the next period and never rewrites a past one.
        var rows = await connection.QueryAsync<(string Status, long AmountMinor)>(
            new CommandDefinition(
                $"""
                INSERT INTO billing.monthly_subscriptions
                  (vehicle_id, period_month, period_month_tz_at, amount_minor, status)
                SELECT v.id,
                       @PeriodMonth,
                       @Now,
                       CASE WHEN {FirstMonth} = @PeriodMonth THEN 0 ELSE @FeeMinor END,
                       CASE WHEN {FirstMonth} = @PeriodMonth THEN 'FREE' ELSE 'DUE' END
                  FROM registry.vehicles v
                 WHERE v.mode = 'B'
                   AND v.status = 'APPROVED'
                   AND {FirstMonth} <= @PeriodMonth
                ON CONFLICT (vehicle_id, period_month) DO NOTHING
                RETURNING status, amount_minor::bigint AS amount_minor;
                """,
                new { PeriodMonth = periodMonth, Now = now, FeeMinor = feeMinor },
                cancellationToken: cancellationToken));

        var raised = rows.ToArray();

        return new ModeBRunResult(
            periodMonth,
            raised.Length,
            raised.Count(row => row.Status == "FREE"),
            raised.Sum(row => row.AmountMinor));
    }

    public async Task<IReadOnlyList<ModeBCharge>> ListAsync(
        DateOnly periodMonth, Guid? fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ModeBCharge>(
            new CommandDefinition(
                """
                SELECT ms.vehicle_id,
                       v.registration_number,
                       v.vehicle_type,
                       v.owner_id,
                       fv.fleet_id,
                       ms.period_month,
                       -- INTEGER in §10, int64 in every contract. Dapper's constructor binding
                       -- matches parameter types exactly, so the cast is what makes the record read.
                       ms.amount_minor::bigint AS amount_minor,
                       ms.currency,
                       ms.status
                  FROM billing.monthly_subscriptions ms
                  JOIN registry.vehicles v ON v.id = ms.vehicle_id
                  LEFT JOIN registry.fleet_vehicles fv ON fv.vehicle_id = v.id
                 WHERE ms.period_month = @PeriodMonth
                   AND (@FleetId::uuid IS NULL OR fv.fleet_id = @FleetId)
                 ORDER BY fv.fleet_id NULLS LAST, v.registration_number;
                """,
                new { PeriodMonth = periodMonth, FleetId = fleetId },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}
