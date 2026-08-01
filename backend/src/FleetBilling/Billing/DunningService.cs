using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Events;
using MageRide.FleetBilling.Notifications;
using MageRide.FleetBilling.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Billing;

/// <summary>Dunning: noticing an invoice nobody paid, and saying so twice.</summary>
internal interface IDunningService
{
    /// <summary>One pass: claim what has lapsed, remind about what has not been paid since.</summary>
    Task<DunningRunResult> RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDunningService"/>
/// <remarks>
/// <para>
/// <b>Two signals, two audiences, and they are not the same mechanism.</b> The Fleet Portal's is a
/// <em>state</em> — <c>billing.fleet_invoices.status = 'OVERDUE'</c>, which SCR-FP-010 draws
/// whenever an operator opens the screen — plus a <c>fleet.invoice_overdue</c> event on
/// <c>fleet.events</c> for anything that wants to react without polling. notification-svc's is a
/// <em>push</em>, sent by a direct call to its internal plane. Doing the second through Kafka would
/// mean notification-svc growing a <c>fleet.events</c> consumer for one message type, which is a
/// second delivery path for something that already has one (C059 made the same call for the
/// departure alarm).
/// </para>
/// <para>
/// <b>The state change commits before anything is pushed.</b> A notification that failed to send
/// must not roll back the record that an invoice went overdue — the operator's own screen reads that
/// record, and an unsent push is still an overdue invoice. The same split C059 uses for the
/// departure alarm and registry-svc for E-03's document notices.
/// </para>
/// <para>
/// <b>The claim is what makes it exactly-once.</b> <c>UPDATE … WHERE status = 'DUE' … FOR UPDATE
/// SKIP LOCKED RETURNING</c>: every replica may run this, and each overdue invoice is announced by
/// exactly one of them. Without it an hourly sweep on three replicas would push an operator three
/// times an hour about one bill.
/// </para>
/// <para>
/// <b>And a reminder is not a second claim.</b> An invoice stays OVERDUE until it is paid, so the
/// second phase re-claims on <c>last_dunned_at</c> against
/// <c>FleetBilling:DunningInterval</c> — <c>overdue_at</c> is never moved, because "when this went
/// overdue" and "when we last said so" are different questions and one column would lose the first.
/// </para>
/// </remarks>
internal sealed class DunningService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetInvoiceRepository invoices,
    IOutboxWriter outbox,
    IDunningNotifier notifier,
    IOptions<FleetBillingOptions> options,
    TimeProvider clock,
    ILogger<DunningService> logger) : IDunningService
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DunningRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - _options.DunningInterval;

        IReadOnlyList<OverdueInvoice> notices;
        int markedOverdue;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var lapsed = await invoices.ClaimOverdueAsync(
                unitOfWork.Connection, unitOfWork.Transaction, now, _options.RunBatchSize, cancellationToken);

            var reminders = await invoices.ClaimRemindersAsync(
                unitOfWork.Connection, unitOfWork.Transaction, now, cutoff, _options.RunBatchSize, cancellationToken);

            markedOverdue = lapsed.Count;

            // The two claims cannot overlap: phase one leaves `last_dunned_at = now`, and phase two
            // takes only rows whose last reminder is at or before the cutoff, which `now` is not.
            var claimed = lapsed.Concat(reminders).ToArray();

            notices = await invoices.ReadOverdueDetailAsync(
                unitOfWork.Connection, unitOfWork.Transaction, claimed, cancellationToken);

            if (notices.Count > 0)
            {
                await outbox.WriteAsync(
                    unitOfWork,
                    [
                        .. notices.Select(notice => FleetBillingEvents.Overdue(
                            notice, DaysOverdue(notice, now), DunningNotifier.NotificationType, now)),
                    ],
                    cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }

        if (markedOverdue > 0)
        {
            logger.LogWarning(
                "{Count} fleet invoice(s) passed their payment term and are now OVERDUE.", markedOverdue);
        }

        // Committed first, on purpose: see the class remarks.
        var notified = 0;

        foreach (var notice in notices)
        {
            if (await notifier.NotifyAsync(notice, DaysOverdue(notice, now), cancellationToken))
            {
                notified++;
            }
        }

        return new DunningRunResult(markedOverdue, notified);
    }

    /// <remarks>
    /// Whole days, floored, and never negative: phase two can re-claim an invoice within the same
    /// day it lapsed if the interval is configured short, and "0 days overdue" is a truthful thing
    /// to render where a negative number is not.
    /// </remarks>
    private static int DaysOverdue(OverdueInvoice invoice, DateTimeOffset now) =>
        (int)Math.Max(0, Math.Floor((now - invoice.DueAt).TotalDays));
}
