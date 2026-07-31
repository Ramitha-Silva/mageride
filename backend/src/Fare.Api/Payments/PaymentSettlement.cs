using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Fare.Settlement;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;

namespace MageRide.Fare.Payments;

/// <summary>
/// What happens when a payment closes: R-05's earning, and telling ride-svc.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because there are seven ways to close a payment and they must all mean the same
/// thing.</b> A gateway <c>Succeeded</c>, a cash settlement, a COD collection, a driver's QR
/// confirmation, a dispute and two refund paths all end here — and R-05 is a single rule ("the
/// driver's earning posts only once the payment reaches a terminal state"), not seven similar ones.
/// </para>
/// <para>
/// <b>The earning is posted inside the caller's transaction; the two outward calls are not.</b> The
/// rollup is this service's own table and belongs to the same commit as the state change, so a
/// payment cannot be terminal without its earning. ride-svc's settle and wallet-svc's tip are other
/// services' transactions and cannot be rolled back with ours, so they run after the commit and are
/// each idempotent by construction — the ride settle is guarded on the ride's own state, and the tip
/// is keyed by the payment.
/// </para>
/// <para>
/// <b>A failed hop is loud and does not fail the caller.</b> The passenger has paid; refusing the
/// request would not un-pay them, and a 500 on a settlement path invites a retry that finds the
/// payment already terminal. What it leaves is a ride in <c>PaymentPending</c> whose payment is
/// closed, which is a reconciliation matter and is logged as one.
/// </para>
/// </remarks>
internal sealed class PaymentSettlementService(
    IDriverEarningsRepository earnings,
    IRideSettlementClient rides,
    IWalletLedgerClient wallet,
    TimeProvider clock,
    ILogger<PaymentSettlementService> logger)
{
    /// <summary>
    /// Records the earning for a payment that has just closed. Call inside the transaction that
    /// wrote the terminal state.
    /// </summary>
    /// <param name="driverId">
    /// The driver who earned it. <see langword="null"/> when the ride names none — a ride that
    /// reached a terminal with no accepted driver has nobody to pay, and that is logged rather than
    /// guessed at.
    /// </param>
    public async Task RecordEarningAsync(
        IUnitOfWork unitOfWork,
        RidePayment payment,
        Guid? driverId,
        bool earningPayable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (!earningPayable)
        {
            // A disputed fare is a terminal of the ride and not of the money: nothing has been
            // earned until Finance says so. ride-svc draws the same line on `ride.settled`.
            logger.LogInformation(
                "Payment {PaymentId} closed at {State}; no driver earning posts (R-05).",
                payment.Id,
                payment.State);

            return;
        }

        if (driverId is not { } driver)
        {
            logger.LogError(
                "Payment {PaymentId} on ride {RideId} closed at {State} with no accepted driver, so no earning "
                + "could be attributed. The money is real and the rollup is short by it.",
                payment.Id,
                payment.RideId,
                payment.State);

            return;
        }

        var now = clock.GetUtcNow();
        var (earnDate, tzAt) = BusinessCalendar.Stamp(now);

        // gross is what the passenger paid for the trip. The OnePay surcharge is deliberately
        // excluded — it is the processing fee and never the driver's — and the D-05 penalty that may
        // be inside the amount nets out through the wallet legs C049 posts, not through this rollup.
        await earnings.PostAsync(unitOfWork, driver, earnDate, tzAt, payment.AmountMinor, cancellationToken);
    }

    /// <summary>
    /// The two outward hops a closed payment makes. Call <b>after</b> the commit.
    /// </summary>
    public async Task PublishAsync(RidePayment payment, Guid? driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        // E-10: the tip is the passenger's money reaching the driver directly, and it is a wallet
        // credit rather than part of the fare — the driver collected the fare, not the tip.
        if (payment.TipAmountMinor > 0 && driverId is { } tipped)
        {
            await wallet.CreditAsync(
                tipped,
                payment.TipAmountMinor,
                "tip_payout",
                $"tip_payout:{payment.Id}",
                $"Tip on ride {payment.RideId} (E-10).",
                payment.RideId.ToString(),
                cancellationToken);
        }

        await rides.SettleAsync(
            payment.RideId, payment.Id, payment.State, payment.AmountMinor, cancellationToken);
    }

    /// <summary>
    /// Whether a closing state earns the driver anything.
    /// </summary>
    /// <remarks>
    /// The same split ride-svc's <c>ride.settled</c> makes: <c>Disputed</c> closes a payment and
    /// earns nothing, and <c>Refunded</c> closes one whose money has gone back.
    /// </remarks>
    public static bool EarningPayable(string state) =>
        state is RidePaymentStates.Succeeded
            or RidePaymentStates.FellBackToCash
            or RidePaymentStates.CashOnDeliveryCollected
            or RidePaymentStates.DriverConfirmedQR;
}
