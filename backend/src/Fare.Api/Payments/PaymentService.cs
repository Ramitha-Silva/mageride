using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Fare.Gateways;
using MageRide.Fare.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Payments;

/// <summary>A payment and, when the method opened one, the gateway artefacts the app must follow.</summary>
public sealed record InitiatedPayment(RidePayment Payment, GatewaySession Session);

/// <summary>
/// <c>POST /v1/fare/pay</c> and the two operations that move a payment without a gateway saying so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every state change goes through <see cref="PaymentStateMachine"/>.</b> No method here writes a
/// state name: it names what happened, the machine says where that lands, and the repository's
/// guarded <c>UPDATE</c> applies it only from the state it was resolved against. A concurrent
/// callback and a passenger tapping "pay cash" therefore cannot both win.
/// </para>
/// <para>
/// <b>P-04 is resolved from the chosen method, not from the booking.</b> Cash is always paid by the
/// rider and LankaQR/OnePay are always charged to the booker — so a passenger who booked cash and
/// then pays by card moves the charge to the booker, which is the whole point of the rule.
/// </para>
/// </remarks>
internal sealed class PaymentService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRidePaymentRepository payments,
    IDriverPayoutRepository merchants,
    IEnumerable<IFareGateway> gateways,
    PaymentSettlementService settlement,
    IOptions<FareOptions> options,
    ILogger<PaymentService> logger)
{
    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The methods <c>POST /v1/fare/pay</c> accepts (<c>fare.yaml</c>'s <c>PaymentMethod</c>).</summary>
    /// <remarks>
    /// <c>cod</c> is absent and belongs elsewhere: it is a <em>booking-time</em> choice (C004 note
    /// (f)) and it settles through ride-svc's <c>POST /v1/rides/{id}/cod-collected</c>, not through
    /// a passenger tapping Pay.
    /// </remarks>
    private static readonly IReadOnlySet<string> PayableMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        RidePaymentMethods.Cash,
        RidePaymentMethods.LankaQr,
        RidePaymentMethods.Onepay,
        RidePaymentMethods.ScanDriverQr,
    };

    public async Task<InitiatedPayment> PayAsync(
        Guid callerId, Guid rideId, string method, long tipMinor, CancellationToken cancellationToken)
    {
        if (!PayableMethods.Contains(method))
        {
            throw new MageRideException(
                MageRideErrors.PaymentMethodInvalid,
                $"method must be one of {string.Join(", ", PayableMethods.Order(StringComparer.Ordinal))}. "
                + "Cash on delivery is chosen at booking and settled by the driver at the door (P-08).");
        }

        if (tipMinor < 0)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "tipMinor must not be negative.");
        }

        var (ride, payment) = await RequirePayableAsync(rideId, cancellationToken);

        RequireParticipant(ride, callerId);

        var payerRole = PayerRoleFor(method);
        var payerUserId = payerRole == PayerRoles.Booker ? ride.BookerId : ride.PassengerId;

        // US-8.11: OnePay adds 5%, stated separately so the passenger sees the difference before
        // committing. Every other rail is zero — there is no other surcharged method.
        var surchargeMinor = method == RidePaymentMethods.Onepay
            ? FareFormula.DivideRounded(payment.AmountMinor * _options.OnepaySurchargeBps, 10_000)
            : 0;

        // §11.8's retry: a passenger tapping Pay again after a gateway refused them. The failed row
        // is closed as `Retried` and a new attempt carries the next try, because
        // provider_transaction_id is UNIQUE and must stay one-to-one with a gateway call — reusing
        // the row would give two attempts one reference and make the chain unreconstructable.
        if (string.Equals(payment.State, RidePaymentStates.Failed, StringComparison.Ordinal))
        {
            payment = await RetryAsync(payment, ride, method, surchargeMinor, cancellationToken);
        }

        var session = await OpenSessionAsync(ride, payment, method, surchargeMinor, cancellationToken);

        var patch = new PaymentPatch(
            Method: method,
            SurchargeMinor: surchargeMinor,
            TipAmountMinor: tipMinor > 0 ? tipMinor : null,
            // P-04 is resolved here and not at booking: a passenger who booked cash and pays by card
            // moves the charge to the booker, which is exactly what the rule is for.
            PayerRole: payerRole,
            PayerUserId: payerUserId);

        // Cash and driver-QR never reach a gateway, so they do not become Pending. Cash closes here
        // — the driver has the money in their hand and no third party will ever confirm it — while
        // driver-QR only records the method and waits for the AL-47 attestation pair.
        var trigger = method switch
        {
            RidePaymentMethods.Cash => (PaymentTrigger?)PaymentTrigger.SettledInCash,
            RidePaymentMethods.ScanDriverQr => null,
            _ => PaymentTrigger.GatewaySessionOpened,
        };

        var moved = trigger is { } fired
            ? await ApplyAsync(payment, fired, patch, ride, cancellationToken)
            : await PatchOnlyAsync(payment, patch, cancellationToken);

        return new InitiatedPayment(moved, session);
    }

    /// <summary>US-8.15 — the passenger stranded by a gateway outage settles in the vehicle.</summary>
    public async Task<RidePayment> FallbackToCashAsync(
        Guid callerId, Guid paymentId, CancellationToken cancellationToken)
    {
        var (ride, payment) = await RequirePaymentAsync(paymentId, cancellationToken);

        RequireParticipant(ride, callerId);

        return await ApplyAsync(
            payment,
            PaymentTrigger.SettledInCash,
            new PaymentPatch(Method: RidePaymentMethods.Cash),
            ride,
            cancellationToken);
    }

    /// <summary>AL-22 — the passenger scanned the driver's own QR; the money moves outside the platform.</summary>
    public async Task<RidePayment> ScanDriverQrAsync(
        Guid callerId, Guid rideId, string qrPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qrPayload))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["qrPayload"] = ["qrPayload is required — it is the decoded contents of the driver's QR."],
            });
        }

        var (ride, payment) = await RequirePayableAsync(rideId, cancellationToken);

        RequireParticipant(ride, callerId);

        // Records the method and nothing else. Since AL-47 this no longer waits for a webhook: the
        // transfer is bank-to-bank and no gateway will ever tell us it happened, so the next move is
        // the claim/confirm pair.
        return await PatchOnlyAsync(
            payment, new PaymentPatch(Method: RidePaymentMethods.ScanDriverQr), cancellationToken);
    }

    /// <summary>US-8.15's poll. The terminal state also arrives as a push.</summary>
    public async Task<RidePayment> StatusAsync(Guid callerId, Guid paymentId, CancellationToken cancellationToken)
    {
        var (ride, payment) = await RequirePaymentAsync(paymentId, cancellationToken);

        RequireParticipant(ride, callerId);

        return payment;
    }

    /// <summary>
    /// Applies a trigger: resolve, write, and close the payment if that is where it landed.
    /// </summary>
    /// <remarks>
    /// The earning is written inside this transaction and the outward hops after it — see
    /// <see cref="PaymentSettlementService"/> for why the split is where it is.
    /// </remarks>
    public async Task<RidePayment> ApplyAsync(
        RidePayment payment,
        PaymentTrigger trigger,
        PaymentPatch? patch,
        RideFacts ride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(ride);

        if (!PaymentStateMachine.TryResolve(payment.State, trigger, out var transition, out var refusal))
        {
            throw Refused(payment, trigger, refusal);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var moved = await payments.TransitionAsync(
                        unitOfWork, payment.Id, transition.From, transition.To, patch, cancellationToken)
                    // No row means the state moved under us between the resolve and the write — a
                    // concurrent callback, a second tap. The database picked the winner; this caller
                    // is told so rather than overwriting a settlement.
                    ?? throw new MageRideException(
                        MageRideErrors.Conflict,
                        $"Payment {payment.Id} moved while this request was in flight. Poll its status.");

        if (transition.ClosesPayment)
        {
            await settlement.RecordEarningAsync(
                unitOfWork,
                moved,
                ride.AcceptedDriverId,
                PaymentSettlementService.EarningPayable(moved.State),
                cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        if (transition.ClosesPayment)
        {
            await settlement.PublishAsync(moved, ride.AcceptedDriverId, cancellationToken);
        }

        logger.LogInformation(
            "Payment {PaymentId} on ride {RideId}: {From} --{Trigger}--> {To}",
            moved.Id,
            moved.RideId,
            transition.From,
            trigger,
            transition.To);

        return moved;
    }

    /// <summary>
    /// Closes a failed attempt and opens the next one (§11.8, D5' §8.1's <c>Failed → Retried</c>).
    /// </summary>
    /// <remarks>
    /// Both halves are one transaction: a <c>Retried</c> row with no successor is a payment nothing
    /// can settle, and a successor with its predecessor still <c>Failed</c> would let the passenger
    /// be charged on two live attempts.
    /// </remarks>
    private async Task<RidePayment> RetryAsync(
        RidePayment failed, RideFacts ride, string method, long surchargeMinor, CancellationToken cancellationToken)
    {
        if (!PaymentStateMachine.TryResolve(
                failed.State, PaymentTrigger.RetryRequested, out var transition, out var refusal))
        {
            throw Refused(failed, PaymentTrigger.RetryRequested, refusal);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var closed = await payments.TransitionAsync(
                         unitOfWork, failed.Id, transition.From, transition.To, null, cancellationToken)
                     ?? throw new MageRideException(
                         MageRideErrors.Conflict,
                         $"Payment {failed.Id} moved while a retry was being opened. Poll its status.");

        var next = await payments.CreateRetryAsync(
            unitOfWork, closed, method, closed.AmountMinor, surchargeMinor, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Payment {FailedId} on ride {RideId} was retried as {NextId} (attempt {AttemptNo}).",
            failed.Id,
            ride.RideId,
            next.Id,
            next.AttemptNo);

        return next;
    }

    /// <summary>Updates columns without moving the state — the driver-QR method record.</summary>
    private async Task<RidePayment> PatchOnlyAsync(
        RidePayment payment, PaymentPatch patch, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var moved = await payments.TransitionAsync(
                        unitOfWork, payment.Id, payment.State, payment.State, patch, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.Conflict,
                        $"Payment {payment.Id} moved while this request was in flight. Poll its status.");

        await unitOfWork.CommitAsync(cancellationToken);

        return moved;
    }

    private async Task<GatewaySession> OpenSessionAsync(
        RideFacts ride, RidePayment payment, string method, long surchargeMinor, CancellationToken cancellationToken)
    {
        var gateway = gateways.FirstOrDefault(g => string.Equals(g.Method, method, StringComparison.Ordinal));

        if (gateway is null)
        {
            return GatewaySession.None;
        }

        string? merchantId = null;

        if (method == RidePaymentMethods.Onepay)
        {
            // D-11 / ADD §11.9: without a merchant binding the money has nowhere to land, and the
            // ADD's own answer is that fare-svc "cannot route in-app payments for this driver and
            // falls back to cash". 402 is what the contract declares, and the app offers cash.
            merchantId = ride.AcceptedDriverId is { } driver
                ? await merchants.ReadMerchantIdAsync(driver, cancellationToken)
                : null;

            if (string.IsNullOrWhiteSpace(merchantId))
            {
                throw new MageRideException(
                    MageRideErrors.MerchantNotOnboarded,
                    "This driver has no active OnePay merchant account, so a card payment cannot be routed to "
                    + "them (D-11). Pay in cash or by LankaQR.");
            }
        }

        return await gateway.StartAsync(
            payment.Id, ride.RideId, payment.AmountMinor + surchargeMinor, merchantId, cancellationToken);
    }

    /// <summary>The ride and its computed fare, or the right refusal.</summary>
    private async Task<(RideFacts Ride, RidePayment Payment)> RequirePayableAsync(
        Guid rideId, CancellationToken cancellationToken)
    {
        var ride = await rides.ReadAsync(rideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {rideId}.");

        var payment = await payments.FindForRideAsync(rideId, cancellationToken)
                      // C049's POST /v1/fare/calculate creates it when the ride completes. Its
                      // absence means the ride has not been priced, which is a different problem
                      // from a payment that cannot be made.
                      ?? throw new MageRideException(
                          MageRideErrors.NotFound,
                          $"Ride {rideId} has no computed fare yet. It is priced when the ride completes.");

        return (ride, payment);
    }

    private async Task<(RideFacts Ride, RidePayment Payment)> RequirePaymentAsync(
        Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindAsync(paymentId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, $"No payment {paymentId}.");

        var ride = await rides.ReadAsync(payment.RideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {payment.RideId}.");

        return (ride, payment);
    }

    /// <summary>
    /// P-04: cash is always paid by the rider, LankaQR and OnePay are always charged to the booker.
    /// </summary>
    /// <remarks>
    /// Driver-QR follows cash: the passenger transfers from their own bank app to the driver's, in
    /// the vehicle, so it is the person in the vehicle who pays.
    /// </remarks>
    internal static string PayerRoleFor(string method) =>
        method is RidePaymentMethods.LankaQr or RidePaymentMethods.Onepay
            ? PayerRoles.Booker
            : PayerRoles.Rider;

    /// <summary>
    /// Only the ride's own people may touch its money.
    /// </summary>
    /// <remarks>
    /// The booker is included even on a cash ride: they may not be paying, but a proxy booking is
    /// theirs and the status poll is how their app follows it. The driver is included because
    /// AL-47's confirm is theirs.
    /// </remarks>
    internal static void RequireParticipant(RideFacts ride, Guid callerId)
    {
        ArgumentNullException.ThrowIfNull(ride);

        if (callerId != ride.PassengerId && callerId != ride.BookerId && callerId != ride.AcceptedDriverId)
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "This ride's payment is not yours.");
        }
    }

    /// <summary>
    /// The refusal a blocked transition earns.
    /// </summary>
    /// <remarks>
    /// A settled payment is <c>409 payment-already-settled</c>, which every one of these operations
    /// declares; a legal-but-wrong-moment one is an ordinary conflict. Collapsing the two would tell
    /// a passenger their card had been charged when it had not.
    /// </remarks>
    internal static MageRideException Refused(RidePayment payment, PaymentTrigger trigger, TransitionRefusal refusal) =>
        refusal == TransitionRefusal.AlreadySettled
            ? new MageRideException(
                MageRideErrors.PaymentAlreadySettled,
                $"Payment {payment.Id} is {payment.State} and its money has already been settled.")
            : new MageRideException(
                MageRideErrors.Conflict,
                $"A payment in {payment.State} cannot be moved by {trigger}.");
}
