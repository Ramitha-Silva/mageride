using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Events;
using MageRide.FleetBilling.Persistence;
using MageRide.FleetBilling.Wallet;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Billing;

/// <summary>What one attempt to settle one invoice did.</summary>
/// <param name="Settled">
/// <see langword="true"/> when this call moved the invoice to PAID — including the replay case,
/// where an earlier attempt had already posted the money and only the row was missing.
/// </param>
public sealed record SettlementOutcome(FleetInvoice Invoice, bool Settled, bool Insufficient, Guid? JournalEntryId);

/// <summary>Settlement: taking one invoice's total out of the fleet wallet, once.</summary>
internal interface IInvoiceSettlementService
{
    /// <summary>Settles one invoice, or reports why it did not.</summary>
    Task<SettlementOutcome> SettleAsync(FleetInvoice invoice, CancellationToken cancellationToken);

    /// <summary>Walks the open invoices and settles the ones the wallets can cover.</summary>
    Task<SettlementRunResult> RunAsync(Guid? fleetId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IInvoiceSettlementService"/>
/// <remarks>
/// <para>
/// <b>Debit first, record second.</b> The ledger and this service's tables are not in one
/// transaction, so the order is the whole of the crash-safety argument — subscription-svc's rule
/// (C047), and it matters more here because the amounts are larger. Debit-then-record leaves, at
/// worst, money taken and an invoice still DUE; the retry re-sends the same
/// <c>fleet_invoice:{invoiceId}</c> key, gets <c>replayed: true</c> for the same entry, moves
/// nothing, and writes the row it owed. Record-then-debit leaves an organisation marked PAID that
/// paid nothing, which no retry ever repairs because the row now says the month is settled.
/// </para>
/// <para>
/// <b>Two guards make the settlement single-shot and they guard different things.</b>
/// <c>billing.journal_entries.idempotency_key</c> is UNIQUE and stops the <em>money</em> moving
/// twice; <c>UPDATE … WHERE status IN ('DUE','OVERDUE')</c> stops a second <em>row</em> change. The
/// first is the load-bearing half, because two replicas can decide to settle at the same instant and
/// nothing serialises the decision.
/// </para>
/// <para>
/// <b>A FREE invoice is never posted and cannot be.</b> Its total is zero, so the entry would need a
/// zero leg — which <c>LedgerService.PostAsync</c> refuses outright as "a movement that did not
/// happen" — and <c>ck_fleet_invoices_free</c> forbids the column that would hold the result. The
/// filter here is the third lock on the same door.
/// </para>
/// <para>
/// <b>An organisation that cannot pay is an outcome, not an error.</b> The invoice stays open, the
/// dunning sweep will notice it when the term lapses, and the next tick tries again after a top-up.
/// That is what makes <c>FleetBilling:AutoSettle</c> safe to leave on.
/// </para>
/// </remarks>
internal sealed class InvoiceSettlementService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetInvoiceRepository invoices,
    IFleetLedgerClient ledger,
    IOutboxWriter outbox,
    IOptions<FleetBillingOptions> options,
    TimeProvider clock,
    ILogger<InvoiceSettlementService> logger) : IInvoiceSettlementService
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<SettlementOutcome> SettleAsync(FleetInvoice invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (!InvoiceStatuses.IsOpen(invoice.Status) || invoice.TotalMinor <= 0)
        {
            throw new MageRideException(
                FleetBillingErrors.InvoiceNotPayable,
                invoice.Status == InvoiceStatuses.Paid
                    ? $"Invoice {invoice.Id} was settled on {invoice.SettledAt:u}."
                    : $"Invoice {invoice.Id} is {invoice.Status} and its total is {invoice.TotalMinor}: there is "
                      + "nothing to pay. Every vehicle was in its first month, or the organisation runs Mode A "
                      + "vehicles only, which are free (AL-03).");
        }

        LedgerPosting posting;

        try
        {
            posting = await ledger.DebitAsync(
                invoice.FleetId,
                invoice.TotalMinor,
                LedgerKeys.FleetInvoiceKind,
                LedgerKeys.FleetInvoice(invoice.Id),
                $"Monthly platform fee, {invoice.PeriodMonth:yyyy-MM} ({invoice.VehicleCount} Mode B vehicle(s))",
                invoice.Id.ToString(),
                cancellationToken);
        }
        catch (MageRideException exception) when (exception.Error == MageRideErrors.InsufficientWallet)
        {
            logger.LogInformation(
                "Fleet {FleetId} cannot cover invoice {InvoiceId} ({AmountMinor} minor units for "
                + "{PeriodMonth:yyyy-MM}); it stays {Status}.",
                invoice.FleetId,
                invoice.Id,
                invoice.TotalMinor,
                invoice.PeriodMonth,
                invoice.Status);

            return new SettlementOutcome(invoice, Settled: false, Insufficient: true, JournalEntryId: null);
        }

        // The money has moved (or had already moved, which is the same fact). Everything after this
        // point is bookkeeping that a retry can redo.
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var settledAt = clock.GetUtcNow();

        var claimed = await invoices.TrySettleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, invoice.Id, posting.EntryId, settledAt, cancellationToken);

        if (!claimed)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            // Somebody else recorded it between the debit and here — and because they used the same
            // ledger key, they moved the same money. Reported as settled, because it is.
            logger.LogInformation(
                "Invoice {InvoiceId} was recorded as settled by another attempt; entry {EntryId} moved the money "
                + "once.",
                invoice.Id,
                posting.EntryId);

            return new SettlementOutcome(invoice, Settled: true, Insufficient: false, posting.EntryId);
        }

        // The per-vehicle charges this invoice consolidated. See MarkChargesPaidAsync for why this
        // service writes a column subscription-svc owns.
        var charges = await invoices.MarkChargesPaidAsync(
            unitOfWork.Connection, unitOfWork.Transaction, invoice.Id, cancellationToken);

        await outbox.WriteAsync(
            unitOfWork,
            FleetBillingEvents.Paid(invoice, posting.EntryId, posting.BalanceAfterMinor, settledAt),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Invoice {InvoiceId} settled for fleet {FleetId}: {AmountMinor} minor units, entry {EntryId}, "
            + "{Charges} per-vehicle charge(s) marked PAID, wallet now {BalanceAfterMinor}.",
            invoice.Id,
            invoice.FleetId,
            invoice.TotalMinor,
            posting.EntryId,
            charges,
            posting.BalanceAfterMinor);

        return new SettlementOutcome(
            invoice with { Status = InvoiceStatuses.Paid, JournalEntryId = posting.EntryId, SettledAt = settledAt },
            Settled: true,
            Insufficient: false,
            posting.EntryId);
    }

    public async Task<SettlementRunResult> RunAsync(Guid? fleetId, CancellationToken cancellationToken)
    {
        var payable = await invoices.ListPayableAsync(fleetId, _options.RunBatchSize, cancellationToken);

        var settled = 0;
        var insufficient = 0;

        foreach (var invoice in payable)
        {
            try
            {
                var outcome = await SettleAsync(invoice, cancellationToken);

                if (outcome.Settled)
                {
                    settled++;
                }
                else if (outcome.Insufficient)
                {
                    insufficient++;
                }
            }
            catch (MageRideException exception)
            {
                // One organisation's problem must not stop the sweep: a wallet-svc timeout on the
                // third invoice would otherwise leave the remaining two hundred unsettled until the
                // next tick, and a run that stops on the first failure never gets through a backlog.
                logger.LogWarning(
                    exception,
                    "Invoice {InvoiceId} for fleet {FleetId} could not be settled this pass.",
                    invoice.Id,
                    invoice.FleetId);
            }
        }

        return new SettlementRunResult(payable.Count, settled, insufficient);
    }
}
