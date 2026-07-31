using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Fare.Payments;

/// <summary>What a gateway told us (D6' §7.1's payload, normalised).</summary>
public sealed record ProviderCallback(
    string ProviderTransactionId, Guid? PaymentId, string Status, long? AmountMinor);

/// <summary>
/// The two gateway callbacks, and R-19's late one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent on <c>provider_transaction_id</c>, and the guard is a UNIQUE index</b> (1002).
/// A redelivery either collides on that column or finds the payment already past the state it was
/// resolving from — both are answered <c>200</c>, because that is what stops a gateway retrying for
/// ever. Nothing here counts deliveries or keeps its own dedupe table.
/// </para>
/// <para>
/// <b>A verified <c>Succeeded</c> is OnePay-only (D-10), and this class does not enforce that —
/// the routing does.</b> Only the OnePay endpoint is wired to a gateway that can produce one;
/// LankaQR's confirm is the acquirer telling us a transfer landed, which is the same shape and the
/// same handler. The fence lives in which secret verifies which route.
/// </para>
/// </remarks>
internal sealed class CallbackService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRidePaymentRepository payments,
    IRefundRepository refunds,
    PaymentService pay,
    ILogger<CallbackService> logger)
{
    /// <summary>Settles, fails, or turns a late success into an overpayment.</summary>
    public async Task HandleAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var payment = await ResolveAsync(callback, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.NotFound,
                          "This callback names no ride payment this platform started.");

        var ride = await rides.ReadAsync(payment.RideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {payment.RideId}.");

        switch (callback.Status.ToUpperInvariant())
        {
            case "SUCCESS":
                await SucceedAsync(payment, ride, callback, cancellationToken);
                break;

            case "FAILED":
                await MoveQuietlyAsync(payment, ride, PaymentTrigger.GatewayFailed, callback, cancellationToken);
                break;

            default:
                // PENDING. The gateway will call again; recording the reference is the whole
                // handling, so a later delivery resolves to this row even without a paymentId.
                await StampReferenceAsync(payment, callback, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// A provider success. Ordinarily this settles the payment; after a cash fallback it is R-19's
    /// overpayment.
    /// </summary>
    private async Task SucceedAsync(
        RidePayment payment, RideFacts ride, ProviderCallback callback, CancellationToken cancellationToken)
    {
        // §11.14, verbatim on the point that matters: the ride is NOT dragged to Disputed. It was
        // settled in cash and it stays settled; what changes is that the passenger has now paid
        // twice and is owed one of them back.
        if (PaymentStateMachine.TryResolve(
                payment.State, PaymentTrigger.LateGatewaySucceeded, out var late, out _))
        {
            await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

            var overpaid = await payments.TransitionAsync(
                unitOfWork,
                payment.Id,
                late.From,
                late.To,
                new PaymentPatch(ProviderTransactionId: callback.ProviderTransactionId),
                cancellationToken);

            if (overpaid is null)
            {
                // Someone else moved it — most likely the same callback arriving twice at once.
                logger.LogInformation(
                    "A late {Provider} success for payment {PaymentId} lost the race; nothing was changed.",
                    callback.ProviderTransactionId,
                    payment.Id);

                return;
            }

            // The refund row IS the Finance queue: ix_refunds_open (1003) is a partial index over
            // the unsettled statuses ordered by requested_at, and its comment names it "the Finance
            // Officer refund queue (SCR-AP-009)". Raising it here is §11.14's
            // "INSERT fares.refunds(kind='overpaid_reversal')".
            var refund = await refunds.CreateAsync(
                unitOfWork,
                overpaid.Id,
                RefundKinds.OverpaidReversal,
                callback.AmountMinor ?? overpaid.PayableMinor,
                overpaid.Currency,
                reasonCode: "late_gateway_success_after_cash",
                requestedBy: null,
                RefundStatuses.Requested,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);

            logger.LogWarning(
                "Payment {PaymentId} was already settled in cash when {Provider} confirmed it; it is Overpaid "
                + "and refund {RefundId} is on the Finance queue (R-19, §11.14).",
                payment.Id,
                callback.ProviderTransactionId,
                refund.Id);

            return;
        }

        await MoveQuietlyAsync(payment, ride, PaymentTrigger.GatewaySucceeded, callback, cancellationToken);
    }

    /// <summary>
    /// Applies a trigger, and treats "cannot move from here" as a redelivery rather than an error.
    /// </summary>
    /// <remarks>
    /// A gateway that does not get a <c>200</c> retries, so a callback arriving after the state it
    /// would have caused has to be a no-op rather than a <c>409</c>. That is the whole of R-19's
    /// idempotency on this side of the UNIQUE index.
    /// </remarks>
    private async Task MoveQuietlyAsync(
        RidePayment payment,
        RideFacts ride,
        PaymentTrigger trigger,
        ProviderCallback callback,
        CancellationToken cancellationToken)
    {
        if (!PaymentStateMachine.TryResolve(payment.State, trigger, out _, out _))
        {
            logger.LogInformation(
                "A {Trigger} callback for payment {PaymentId} arrived while it was {State}; it is a redelivery "
                + "and nothing was changed.",
                trigger,
                payment.Id,
                payment.State);

            return;
        }

        try
        {
            await pay.ApplyAsync(
                payment,
                trigger,
                new PaymentPatch(
                    ProviderTransactionId: callback.ProviderTransactionId,
                    AmountMinor: null),
                ride,
                cancellationToken);
        }
        catch (MageRideException exception) when (exception.Error.Code == MageRideErrors.Conflict.Code)
        {
            // Lost a race with a concurrent delivery of the same callback. Both wanted the same
            // state; one of them got there.
            logger.LogInformation(
                "A concurrent {Trigger} for payment {PaymentId} was already applied.", trigger, payment.Id);
        }
    }

    private async Task StampReferenceAsync(
        RidePayment payment, ProviderCallback callback, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payment.ProviderTransactionId))
        {
            return;
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        await payments.TransitionAsync(
            unitOfWork,
            payment.Id,
            payment.State,
            payment.State,
            new PaymentPatch(ProviderTransactionId: callback.ProviderTransactionId),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The payment a callback is about: by its own id when the gateway echoed one, otherwise by the
    /// reference we have already recorded.
    /// </summary>
    private async Task<RidePayment?> ResolveAsync(ProviderCallback callback, CancellationToken cancellationToken) =>
        callback.PaymentId is { } id
            ? await payments.FindAsync(id, cancellationToken)
            : await payments.FindByProviderRefAsync(callback.ProviderTransactionId, cancellationToken);
}
