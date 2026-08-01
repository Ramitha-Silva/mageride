using Dapper;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Events;
using MageRide.FleetBilling.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Billing;

/// <summary>Generation: turning a month's per-vehicle charges into one invoice per organisation.</summary>
internal interface IInvoiceRunService
{
    /// <summary>The Colombo month an instant falls in — the first of that month.</summary>
    DateOnly CurrentPeriod();

    /// <summary>
    /// Raises every missing invoice and line for a month, and queues one
    /// <c>fleet.invoice_issued</c> per invoice actually raised. Safe to call as often as you like.
    /// </summary>
    Task<InvoiceRunResult> RunAsync(DateOnly periodMonth, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IInvoiceRunService"/>
/// <remarks>
/// <para>
/// <b>Idempotence is three <c>ON CONFLICT</c>s and a deterministic recompute, not a guard.</b>
/// <c>ux_fleet_invoices_fleet_period</c> makes the invoice single per (fleet, month);
/// <c>ux_fleet_invoice_lines_vehicle</c> and <c>ux_fleet_invoice_lines_charge</c> make the line
/// single per vehicle and per raised charge; the total is Σ of whatever lines exist. Running the
/// generator twice therefore produces the same invoice with the same total, and running it after a
/// vehicle was approved on the 9th produces the same invoice with one more line — which is the
/// behaviour that makes an hourly runner correct where a monthly alarm would be fragile.
/// </para>
/// <para>
/// <b>One transaction for the whole month.</b> The three statements have to agree: an invoice whose
/// lines were inserted and whose total was not recomputed would be an invoice that says zero and
/// bills for twelve buses. It is also the transaction the <c>fleet.invoice_issued</c> rows are
/// written in (R-13), so no event can describe an invoice that was rolled back.
/// </para>
/// <para>
/// <b>Every replica runs this and there is no lease.</b> The upserts are the arbiter, so a lock
/// would be protecting an operation that is already idempotent — subscription-svc's argument for
/// the Mode B charge run this consolidates, one level up.
/// </para>
/// </remarks>
internal sealed class InvoiceRunService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetInvoiceRepository invoices,
    IOutboxWriter outbox,
    IOptions<FleetBillingOptions> options,
    TimeProvider clock,
    ILogger<InvoiceRunService> logger) : IInvoiceRunService
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public DateOnly CurrentPeriod()
    {
        var today = BusinessCalendar.Today(clock);
        return new DateOnly(today.Year, today.Month, 1);
    }

    public async Task<InvoiceRunResult> RunAsync(DateOnly periodMonth, CancellationToken cancellationToken)
    {
        var period = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        var now = clock.GetUtcNow();
        var dueAt = now + _options.PaymentTerm;

        InvoiceRunResult result;
        IReadOnlyList<FleetInvoice> raised;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            result = await invoices.GenerateAsync(
                unitOfWork.Connection, unitOfWork.Transaction, period, now, dueAt, cancellationToken);

            // Read back after the recompute, so the event carries the total and the line count the
            // invoice actually ended up with rather than the zero it was created with.
            raised = await invoices.ReadByIdsAsync(
                unitOfWork.Connection, unitOfWork.Transaction, result.RaisedIds, cancellationToken);

            if (raised.Count > 0)
            {
                await outbox.WriteAsync(
                    unitOfWork,
                    [.. raised.Select(invoice => FleetBillingEvents.Issued(invoice, now))],
                    cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }

        if (result.InvoicesRaised > 0 || result.LinesAdded > 0)
        {
            logger.LogInformation(
                "Fleet billing for {Period}: {Invoices} invoice(s) raised, {Lines} per-vehicle line(s) added, "
                + "{TotalMinor} minor units on the new invoices. Mode A vehicles contribute no line (AL-03).",
                period,
                result.InvoicesRaised,
                result.LinesAdded,
                result.TotalMinor);
        }

        await WarnAboutStrandedChargesAsync(period, cancellationToken);

        return result;
    }

    /// <summary>
    /// Says so when a raised charge could not be consolidated.
    /// </summary>
    /// <remarks>
    /// A per-vehicle charge for a month whose invoice has already been settled is deliberately left
    /// off that invoice — appending to it would break Σ lines = the amount that was paid — so it is
    /// stranded until somebody decides what to do with it. That decision is Finance's and there is
    /// no route in any spec that makes it, so this logs the count rather than inventing one.
    /// Silence would be worse: the platform would simply not be paid for those vehicles and nothing
    /// anywhere would say so.
    /// </remarks>
    private async Task WarnAboutStrandedChargesAsync(DateOnly period, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var stranded = await unitOfWork.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT count(*)::int
                  FROM billing.monthly_subscriptions ms
                  JOIN registry.vehicles v ON v.id = ms.vehicle_id
                  JOIN registry.fleet_vehicles fv ON fv.vehicle_id = ms.vehicle_id
                 WHERE ms.period_month = @Period
                   AND v.mode = 'B'
                   AND ms.status = 'DUE'
                   AND NOT EXISTS (SELECT 1 FROM billing.fleet_invoice_lines l
                                    WHERE l.monthly_subscription_id = ms.id);
                """,
                new { Period = period },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));

        await unitOfWork.CommitAsync(cancellationToken);

        if (stranded > 0)
        {
            logger.LogWarning(
                "{Count} Mode B charge(s) for {Period} are on no invoice: their organisation's invoice for "
                + "the month has already been settled, and appending to a settled invoice would make its "
                + "lines stop summing to the amount that was paid. They will not be collected without a "
                + "Finance decision.",
                stranded,
                period);
        }
    }
}
