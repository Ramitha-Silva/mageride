using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Fare.Settlement;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Fare.Payments;

/// <summary>
/// E-05 — Finance reverses a fare, in full, in part, or because it was paid twice (R-19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two rows for two different facts.</b> <c>fares.refunds</c> is the gateway-facing workflow
/// (requested → submitted → settled) and <c>billing.journal_entries</c> is the money. 1003's header
/// says so outright, and they are separate because a refund can be requested, approved and still
/// fail at the acquirer — a single row would have to be either a promise or a fact and cannot be
/// both.
/// </para>
/// <para>
/// <b>The ledger leg is wallet-svc's, not ours</b> (D-09). §11.14 draws it as
/// <c>DR platform_account / CR passenger</c>; on this platform the passenger has no wallet, so the
/// credit goes to whoever actually paid — the driver on a cash-shaped settlement, whose wallet is
/// the only one the platform holds. The kind is <c>payment_refund</c>, or
/// <c>overpaid_reversal</c> for R-19's double payment, and the key is the refund row, so a retry
/// replays.
/// </para>
/// <para>
/// <b>A refund never un-earns a driver.</b> The rollup is a read model of what was collected;
/// putting the reversal through it would make yesterday's Earnings screen change overnight. Finance
/// reconciles against the ledger, which is where the reversal lives.
/// </para>
/// </remarks>
internal sealed class RefundService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRidePaymentRepository payments,
    IRefundRepository refunds,
    IWalletLedgerClient wallet,
    ILogger<RefundService> logger)
{
    public async Task<Refund> RefundAsync(
        Guid financeUserId,
        Guid paymentId,
        string kind,
        long amountMinor,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (!RefundKinds.All.Contains(kind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = [$"kind must be one of {string.Join(", ", RefundKinds.All.Order(StringComparer.Ordinal))}."],
            });
        }

        if (amountMinor <= 0)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor must be greater than zero.");
        }

        var payment = await payments.FindAsync(paymentId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, $"No payment {paymentId}.");

        var already = await refunds.ListForPaymentAsync(paymentId, cancellationToken);

        var refundedSoFar = already
            .Where(r => r.Status is RefundStatuses.Succeeded or RefundStatuses.Submitted or RefundStatuses.Requested)
            .Sum(r => r.AmountMinor);

        // A payment cannot be refunded for more than it took. The ceiling is the whole payable —
        // fare, surcharge and tip — because all three left the passenger.
        if (refundedSoFar + amountMinor > payment.PayableMinor)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                $"Payment {paymentId} took {payment.PayableMinor} and {refundedSoFar} is already being refunded; "
                + $"{amountMinor} more would exceed it.");
        }

        var trigger = kind == RefundKinds.Partial ? PaymentTrigger.RefundedInPart : PaymentTrigger.RefundedInFull;

        if (!PaymentStateMachine.TryResolve(payment.State, trigger, out var transition, out var refusal))
        {
            throw refusal == TransitionRefusal.AlreadySettled
                ? new MageRideException(
                    MageRideErrors.PaymentAlreadySettled,
                    $"Payment {paymentId} is {payment.State} and cannot be refunded from there.")
                : new MageRideException(
                    MageRideErrors.Conflict,
                    $"Payment {paymentId} is {payment.State}; only a payment that actually took money can be "
                    + "refunded (E-05).");
        }

        var ride = await rides.ReadAsync(payment.RideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {payment.RideId}.");

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var moved = await payments.TransitionAsync(
                        unitOfWork, paymentId, transition.From, transition.To, null, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.Conflict, $"Payment {paymentId} moved while the refund was being raised.");

        var refund = await refunds.CreateAsync(
            unitOfWork,
            paymentId,
            kind,
            amountMinor,
            payment.Currency,
            reasonCode,
            financeUserId,
            RefundStatuses.Requested,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // After the commit: another service's transaction, keyed by this refund row so a retry
        // replays rather than reversing twice.
        var posted = await PostReversalAsync(refund, moved, ride, cancellationToken);

        logger.LogInformation(
            "Refund {RefundId} ({Kind}, {AmountMinor}) raised on payment {PaymentId}; payment is {State}, "
            + "ledger posted: {Posted}.",
            refund.Id,
            kind,
            amountMinor,
            paymentId,
            moved.State,
            posted);

        return refund;
    }

    /// <summary>The Finance queue: everything raised and not yet settled, oldest first (SCR-AP-009).</summary>
    public Task<IReadOnlyList<Refund>> QueueAsync(int limit, CancellationToken cancellationToken) =>
        refunds.ListOpenAsync(limit, cancellationToken);

    /// <summary>
    /// The balanced ledger entry a refund produces.
    /// </summary>
    /// <remarks>
    /// The credit goes to the driver's wallet because that is the only wallet the platform holds for
    /// a party to a ride — on a cash or driver-QR settlement they are also the party who received
    /// the money. A card refund that must return to the original instrument is the acquirer's
    /// reverse API, and <c>fares.refunds.provider_refund_id</c> is where that reference lands when
    /// C065 wires it. Named in the C050 handoff.
    /// </remarks>
    private async Task<bool> PostReversalAsync(
        Refund refund, RidePayment payment, RideFacts ride, CancellationToken cancellationToken)
    {
        if (ride.AcceptedDriverId is not { } driverId || !wallet.IsConfigured)
        {
            logger.LogError(
                "Refund {RefundId} on payment {PaymentId} could not be posted to the ledger: {Reason}. The row is "
                + "on the Finance queue and the money has not moved.",
                refund.Id,
                payment.Id,
                ride.AcceptedDriverId is null ? "the ride names no driver" : "wallet-svc is not configured");

            return false;
        }

        var kind = refund.Kind == RefundKinds.OverpaidReversal ? "overpaid_reversal" : "payment_refund";

        var result = await wallet.DebitAsync(
            driverId,
            refund.AmountMinor,
            kind,
            $"{kind}:{refund.Id}",
            $"Refund on ride {payment.RideId} ({refund.Kind}, {refund.ReasonCode}).",
            payment.RideId.ToString(),
            cancellationToken);

        return result is not null;
    }
}
