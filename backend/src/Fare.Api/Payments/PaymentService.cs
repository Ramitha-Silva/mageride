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
    Settlement.IWalletLedgerClient wallet,
    PaymentSettlementService settlement,
    ILogger<PaymentService> logger)
{

    /// <summary>
    /// The methods <c>POST /v1/fare/pay</c> accepts (<c>fare.yaml</c>'s <c>PaymentMethod</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This set is the AL-57/AL-59 fence.</b> <c>onepay</c> and platform-merchant <c>lankaqr</c>
    /// are gone: OnePay has one merchant account per merchant, so a card fare could only ever land
    /// in MageRide's own account, and <c>lankaqr</c> pointed at the platform's merchant while
    /// crediting the driver nothing but a read-model row. **No ride fare on this surface can reach a
    /// platform merchant account**, and the mechanism is that there is no value for one — not a
    /// check somewhere that could be relaxed.
    /// </para>
    /// <para>
    /// <c>cod</c> is absent for its own reason: it is a <em>booking-time</em> choice (C004 note (f))
    /// and settles through ride-svc's <c>POST /v1/rides/{id}/cod-collected</c>, not by a passenger
    /// tapping Pay.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<string> PayableMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        RidePaymentMethods.Cash,
        RidePaymentMethods.Wallet,
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

        // Δ AL-57: there is no surcharge on any surviving rail. The +5% recovered OnePay's ~3% on the
        // ride, and no ride rail touches an acquirer any more — OnePay sits on the wallet top-up
        // instead, where MageRide is the payee. The column stays so historical rows still read.
        const long surchargeMinor = 0;

        var patch = new PaymentPatch(
            Method: method,
            SurchargeMinor: surchargeMinor,
            TipAmountMinor: tipMinor > 0 ? tipMinor : null,
            // P-04 is resolved here and not at booking: a passenger who booked cash and pays from
            // their wallet moves the charge to the booker, which is exactly what the rule is for.
            PayerRole: payerRole,
            PayerUserId: payerUserId);

        // The wallet rail moves the money BEFORE the state moves, which inverts this service's usual
        // order — and has to. For every other rail the outward hop *follows* the decision; here the
        // hop IS the decision, because whether the fare can be paid at all is a question only the
        // ledger can answer. The call is idempotent on `trip_payment:{ridePaymentId}`, so the one bad
        // interleaving — ledger moved, this process died before transitioning — is repaired by the
        // passenger simply tapping Pay again: the replay is a no-op and the transition completes.
        if (method == RidePaymentMethods.Wallet)
        {
            await SettleFromWalletAsync(ride, payment, payerUserId, cancellationToken);
        }

        // Cash and driver-QR reach no ledger and no gateway. Cash closes here — the driver has the
        // money in their hand and no third party will ever confirm it — while driver-QR only records
        // the method and waits for the AL-47 attestation pair.
        var trigger = method switch
        {
            RidePaymentMethods.Cash => (PaymentTrigger?)PaymentTrigger.SettledInCash,
            RidePaymentMethods.Wallet => PaymentTrigger.SettledFromWallet,
            _ => null,
        };

        var moved = trigger is { } fired
            ? await ApplyAsync(payment, fired, patch, ride, cancellationToken)
            : await PatchOnlyAsync(payment, patch, cancellationToken);

        return new InitiatedPayment(moved, GatewaySession.None);
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

    // Δ AL-57 — `RetryAsync` REMOVED. §11.8's retry chain closed a `Failed` attempt and opened
    // the next one, and `Failed` was only ever reached by `GatewayFailed`. With no ride gateway
    // there is nothing to fail: a wallet payment either moves the ledger or is refused before any
    // row changes, and cash and driver-QR never had a provider. The states and the
    // `Failed -> Retried` edge stay in the machine — historical rows carry them and D5' §8.1 still
    // describes them — but nothing on this surface can reach them.

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

    // Δ AL-57/AL-59 — `OpenSessionAsync` REMOVED with the two ride gateways it opened, and with it
    // the D-11 merchant lookup and `402 merchant-not-onboarded`: OnePay has one merchant account per
    // merchant, so the per-driver binding it checked for never existed. No surviving ride rail has a
    // session to open.

    /// <summary>
    /// Moves a wallet-paid fare from the passenger's balance to the driver's (AL-57).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One balanced <c>trip_payment</c> entry in wallet-svc — passenger debit, driver credit, no
    /// platform leg. This service writes no journal row, as it never has (D-09).
    /// </para>
    /// <para>
    /// <b>An unreachable wallet-svc refuses the payment rather than settling it.</b> Everywhere else
    /// on this surface a failed outward hop degrades — the fare is still correct, the debt waits for
    /// the next trip. Here the hop is the payment: reporting <c>Succeeded</c> because the ledger was
    /// unreachable would tell a driver they had been paid a fare that never moved.
    /// </para>
    /// </remarks>
    private async Task SettleFromWalletAsync(
        RideFacts ride, RidePayment payment, Guid payerUserId, CancellationToken cancellationToken)
    {
        if (ride.AcceptedDriverId is not { } driverId)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This ride has no accepted driver, so there is nobody for a wallet fare to be paid to.");
        }

        var posted = await wallet.TripPaymentAsync(
            payment.Id, payerUserId, driverId, payment.AmountMinor, cancellationToken);

        if (posted is null)
        {
            logger.LogError(
                "The wallet rail is unreachable, so ride payment {PaymentId} was not settled. The passenger "
                + "keeps cash and the driver's QR; nothing moved.",
                payment.Id);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "The wallet could not be reached, so nothing was charged. Pay in cash or by the driver's QR.");
        }
    }

    /// <summary>The ride and its computed fare, or the right refusal.</summary>    /// <summary>The ride and its computed fare, or the right refusal.</summary>
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
    /// <remarks>
    /// P-04, restated for the AL-57 rails: **cash and driver-QR are paid by the rider; the wallet is
    /// charged to the booker.** The rule was always about *whose instrument settles it* — the person
    /// in the car hands over cash or scans a QR, and a stored balance belongs to whoever booked and
    /// topped it up. The two retired methods kept the same side of the line, so nothing about the
    /// rule changed; only the values did.
    /// </remarks>
    internal static string PayerRoleFor(string method) =>
        method is RidePaymentMethods.Wallet or RidePaymentMethods.LankaQr or RidePaymentMethods.Onepay
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
