using MageRide.Payout.Configuration;
using MageRide.Payout.Domain;
using MageRide.Payout.Persistence;
using MageRide.Payout.Wallet;
using MageRide.Shared.Errors;
using MageRide.Shared.Time;
using Microsoft.Extensions.Options;

namespace MageRide.Payout.Payouts;

/// <summary>What one sweep did.</summary>
public sealed record PayoutRunResult(PayoutBatch Batch, int Swept, int Skipped, long TotalMinor);

/// <summary>
/// The weekly sweep, and the outcomes a bank reports back afterwards (AL-58).
/// </summary>
/// <remarks>
/// <para>
/// <b>Full sweep, no minimum, no holdback.</b> Whatever a driver's balance is on run day leaves in
/// full (<c>Payout:RetainMinor</c> defaults to 0). The one named consequence is in D5' §8.1: the
/// D-08 daily fee is charged from the second trip of a Colombo day and cash fares never credit the
/// wallet, so a cash-earning driver is short on their second trip after a sweep. The knob is the
/// remedy and the decision was to leave it at zero.
/// </para>
/// <para>
/// <b>The debit and the instruction cannot be one transaction, so they are made recoverable
/// instead.</b> They live in two services. The payout id is derived from <c>(batch, driver)</c> and
/// wallet-svc composes its ledger key from the id, so a crash between the two is repaired by simply
/// running the batch again: the debit replays to a no-op and the insert that did not happen
/// happens. <see cref="PayoutIds"/> carries the full argument; <c>ux_payouts_batch_driver</c> stops
/// the completing insert becoming a second one.
/// </para>
/// <para>
/// <b>The order is debit-then-record, and it has to be.</b> <c>billing.payouts.journal_entry_id</c>
/// is <c>NOT NULL</c> — the schema refuses to hold half the pair — so there is no row to write until
/// the money has moved. The failure that order admits is an orphaned debit, which the derived id
/// makes findable; the other order would admit an instruction with no money behind it, which
/// nothing could find.
/// </para>
/// </remarks>
internal sealed class PayoutRunService(
    IPayoutRepository payouts,
    IPayoutLedgerClient wallet,
    IBankOrigination bank,
    IOptions<PayoutOptions> options,
    TimeProvider clock,
    ILogger<PayoutRunService> logger)
{
    private readonly PayoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Runs the sweep for a Colombo business date, or refuses because it has already run.
    /// </summary>
    /// <param name="force">
    /// True for the Finance route, which asks for a date rather than waiting for one. It changes
    /// nothing about the work — only whether an already-swept date is a conflict or a quiet skip.
    /// </param>
    public async Task<PayoutRunResult?> RunAsync(
        DateOnly runDate, bool force, CancellationToken cancellationToken)
    {
        var (batch, created) = await payouts.OpenBatchAsync(
            runDate, BusinessCalendar.StartOfDay(runDate), cancellationToken);

        if (!created && batch.Status != PayoutBatchStatuses.Running)
        {
            // A completed batch for this date. The scheduled runner treats that as "already done";
            // Finance asking for it explicitly is told so, because a second sweep of the same day
            // would raise a zero-value instruction for every driver it had just emptied.
            return force
                ? throw new MageRideException(
                    MageRideErrors.PayoutBatchExists,
                    $"The sweep for {runDate:yyyy-MM-dd} has already run. It pays a driver's whole balance, so "
                    + "running it again the same day would raise an empty instruction for everybody.")
                : null;
        }

        var eligible = await payouts.EligibleDriversAsync(
            batch.Id, _options.RetainMinor, _options.BatchSize, cancellationToken);

        var swept = 0;
        var skipped = 0;

        foreach (var driver in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await SweepAsync(batch, driver, cancellationToken))
            {
                swept++;
            }
            else
            {
                skipped++;
            }
        }

        await payouts.CompleteBatchAsync(batch.Id, PayoutBatchStatuses.Completed, cancellationToken);

        var completed = (await payouts.ListBatchesAsync(1, cancellationToken))
            .FirstOrDefault(b => b.Id == batch.Id) ?? batch;

        logger.LogInformation(
            "Payout sweep for {RunDate}: {Swept} driver(s) paid {TotalMinor} minor units, {Skipped} skipped. "
            + "{Selected} had a verified payout profile and a balance to move.",
            runDate,
            swept,
            completed.TotalMinor,
            skipped,
            eligible.Count);

        return new PayoutRunResult(completed, swept, skipped, completed.TotalMinor);
    }

    /// <summary>One driver: debit, record, and hand to the bank if there is one.</summary>
    private async Task<bool> SweepAsync(
        PayoutBatch batch, EligibleDriver driver, CancellationToken cancellationToken)
    {
        var payoutId = PayoutIds.For(batch.Id, driver.DriverId);

        var debited = await wallet.DebitAsync(
            payoutId, driver.DriverId, driver.BalanceMinor, cancellationToken);

        if (debited?.EntryId is not { } entryId)
        {
            // Logged by the client with the reason. The driver keeps every rupee and the next run
            // sweeps whatever is there then — which is the whole point of not holding a balance
            // hostage to a rail being up.
            return false;
        }

        var recorded = await payouts.InsertInstructionAsync(
            payoutId, batch.Id, driver.DriverId, driver.PayoutProfileId,
            driver.BalanceMinor, entryId, cancellationToken);

        if (!recorded)
        {
            // The row was already there — a re-run of this batch after a crash, whose debit replayed
            // above. Nothing to do and nothing wrong.
            return false;
        }

        await SubmitAsync(payoutId, driver.AccountNo, cancellationToken);

        return true;
    }

    /// <summary>Hands one recorded instruction to the bank, if a bank is configured.</summary>
    private async Task SubmitAsync(Guid payoutId, string accountNo, CancellationToken cancellationToken)
    {
        if (!bank.IsConfigured)
        {
            // The design, not a degradation: the debit is made and the row records what is owed, so
            // an operator can see the liability before a rail exists. Announced at start-up.
            return;
        }

        var instruction = await payouts.FindAsync(payoutId, cancellationToken);

        if (instruction is null)
        {
            return;
        }

        if (await bank.SubmitAsync(instruction, accountNo, cancellationToken) is { } reference)
        {
            await payouts.MarkSubmittedAsync(payoutId, reference, cancellationToken);
        }
    }

    /// <summary>
    /// The bank reporting an outcome (R-19's shape: deduped on the provider reference).
    /// </summary>
    /// <remarks>
    /// <b><c>FAILED</c> reverses the debit, and the guarded <c>UPDATE</c> is what makes it exactly
    /// once.</b> The status moves first: a redelivered result finds the row already terminal, does
    /// nothing, and answers the same way — which is what stops a bank retrying for ever and what
    /// stops the reversal being posted twice. The ledger key would catch a second reversal anyway;
    /// two guards, because this one is somebody's money.
    /// </remarks>
    public async Task<PayoutInstruction> ReportAsync(
        Guid payoutId, string status, string? failureReason, CancellationToken cancellationToken)
    {
        var instruction = await payouts.FindAsync(payoutId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No payout {payoutId}.");

        var terminal = status switch
        {
            PayoutStatuses.Paid => PayoutStatuses.Paid,
            PayoutStatuses.Failed => PayoutStatuses.Failed,
            _ => throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = [$"status must be {PayoutStatuses.Paid} or {PayoutStatuses.Failed}."],
            }),
        };

        if (terminal == PayoutStatuses.Failed && string.IsNullOrWhiteSpace(failureReason))
        {
            // ck_payouts_failure_reason would refuse the row anyway; refusing here names the field.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["failureReason"] = ["A failed payout must say why — the money is going back and somebody has to know."],
            });
        }

        var moved = await payouts.SettleAsync(
            payoutId, instruction.Status, terminal, failureReason, cancellationToken);

        if (!moved)
        {
            // Already terminal: a redelivery. Answer with what is there rather than moving it again.
            return await payouts.FindAsync(payoutId, cancellationToken) ?? instruction;
        }

        if (terminal == PayoutStatuses.Failed)
        {
            var reversed = await wallet.ReverseAsync(
                payoutId, instruction.DriverId, instruction.AmountMinor, failureReason, cancellationToken);

            if (reversed?.EntryId is null)
            {
                // The row says FAILED and the money has not come back. Loud, because this is the one
                // state in the whole flow where a driver is out of pocket, and it needs a person.
                logger.LogError(
                    "Payout {PayoutId} for driver {DriverId} FAILED and the reversal of {AmountMinor} minor "
                    + "units could not be posted. The driver's balance is short by that amount until it is. "
                    + "This needs Finance.",
                    payoutId,
                    instruction.DriverId,
                    instruction.AmountMinor);
            }
        }

        return await payouts.FindAsync(payoutId, cancellationToken) ?? instruction;
    }

    /// <summary>Whether <paramref name="instant"/> falls on the configured Colombo run day.</summary>
    public bool IsRunDay(DateTimeOffset instant) =>
        BusinessCalendar.StartOfDay(BusinessCalendar.BusinessDate(instant)).DayOfWeek == _options.RunDay;

    public DateOnly Today() => BusinessCalendar.Today(clock);
}
