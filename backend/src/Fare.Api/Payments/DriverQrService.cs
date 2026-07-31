using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Payments;

/// <summary>
/// AL-47 — driver-QR settlement by attestation, because no gateway can confirm it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The money never touches MageRide.</b> A passenger scanning the driver's printed QR pays
/// bank-to-bank into the driver's own account; the platform sees no callback, takes no commission
/// and holds nothing. So there is no ledger entry on this path and there must not be — the only
/// wallet movement any driver-QR settlement produces is the optional tip, which is a different
/// promise (E-10).
/// </para>
/// <para>
/// <b>The driver's confirm is valid without a claim, and that asymmetry is the design.</b> The
/// driver's bank app is the only party that actually saw the money arrive; the passenger's claim is
/// evidence, not proof. So a confirm settles from <c>Initiated</c> as readily as from
/// <c>QrClaimedByPassenger</c>, and a claim on its own settles nothing.
/// </para>
/// </remarks>
internal sealed class DriverQrService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRidePaymentRepository payments,
    ISupportTicketRepository tickets,
    PaymentService pay,
    TimeProvider clock,
    ILogger<DriverQrService> logger)
{
    /// <summary>US-26.1 — the passenger says they have paid.</summary>
    public async Task<RidePayment> ClaimAsync(
        Guid callerId, Guid rideId, Guid? receiptArtifactId, CancellationToken cancellationToken)
    {
        var (ride, payment) = await RequireAsync(rideId, cancellationToken);

        // The claim is the passenger's, not the driver's: it is an assertion about the payer's own
        // bank app. A driver claiming on the passenger's behalf would be attesting to their own
        // payment.
        RequirePayer(ride, callerId);

        var moved = await pay.ApplyAsync(
            payment,
            PaymentTrigger.QrClaimed,
            new PaymentPatch(QrClaimedAt: clock.GetUtcNow(), QrClaimArtifactId: receiptArtifactId),
            ride,
            cancellationToken);

        // The driver's prompt and the +5 min re-push are notification-svc's (C052); this service
        // records the claim and its instant, and QrNudgeSweeper finds the ones that go unanswered.
        logger.LogInformation(
            "Passenger {CallerId} claimed a driver-QR payment on ride {RideId}; driver {DriverId} must confirm.",
            callerId,
            rideId,
            ride.AcceptedDriverId);

        return moved;
    }

    /// <summary>AL-47's terminal — the driver saw the money.</summary>
    public async Task<RidePayment> ConfirmAsync(Guid callerId, Guid rideId, CancellationToken cancellationToken)
    {
        var (ride, payment) = await RequireAsync(rideId, cancellationToken);

        if (callerId != ride.AcceptedDriverId)
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant,
                "Only the driver whose QR was scanned can confirm the payment arrived (AL-47).");
        }

        return await pay.ApplyAsync(
            payment,
            PaymentTrigger.QrConfirmed,
            new PaymentPatch(
                Method: RidePaymentMethods.ScanDriverQr, QrConfirmedAt: clock.GetUtcNow()),
            ride,
            cancellationToken);
    }

    /// <summary>
    /// Either party disputes. Opens a Support ticket that routes to Finance and moves nothing.
    /// </summary>
    /// <remarks>
    /// <b>No wallet movement, because there is nothing to reverse.</b> The platform never held this
    /// money. What Finance adjudicates on is the evidence: the claim screenshot when one was
    /// attached, and the two timestamps.
    /// </remarks>
    public async Task<Guid> DisputeAsync(
        Guid callerId, Guid rideId, string? note, CancellationToken cancellationToken)
    {
        var (ride, payment) = await RequireAsync(rideId, cancellationToken);

        PaymentService.RequireParticipant(ride, callerId);

        if (!PaymentStateMachine.TryResolve(payment.State, PaymentTrigger.Disputed, out var transition, out var refusal))
        {
            throw PaymentService.Refused(payment, PaymentTrigger.Disputed, refusal);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var moved = await payments.TransitionAsync(
                        unitOfWork, payment.Id, transition.From, transition.To, null, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.Conflict,
                        $"Payment {payment.Id} moved while the dispute was being raised.");

        var description =
            $"Driver-QR payment dispute on ride {rideId} (AL-47). Payment {payment.Id} was "
            + $"{payment.State} for {payment.AmountMinor} {payment.Currency} minor units"
            + (payment.QrClaimedAt is { } claimed ? $", claimed at {claimed:O}" : ", never claimed by the passenger")
            + $". Raised by {callerId}."
            + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" Note: {note.Trim()}");

        var ticketId = await tickets.CreateAsync(
            unitOfWork,
            callerId,
            SupportTicketRepository.DriverQrCategory,
            description,
            rideId,
            screenshotUrl: null,
            cancellationToken);

        // Disputed closes the payment and earns nothing (R-05): the fare is not the driver's until
        // Finance says whose it is.
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Driver-QR dispute {TicketId} opened on ride {RideId} by {CallerId}; payment {PaymentId} is Disputed.",
            ticketId,
            rideId,
            callerId,
            moved.Id);

        return ticketId;
    }

    /// <summary>The payer's side of AL-47 — whoever the money would leave.</summary>
    private static void RequirePayer(RideFacts ride, Guid callerId)
    {
        if (callerId != ride.PassengerId && callerId != ride.BookerId)
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "Only the payer can claim to have paid.");
        }
    }

    private async Task<(RideFacts Ride, RidePayment Payment)> RequireAsync(
        Guid rideId, CancellationToken cancellationToken)
    {
        var ride = await rides.ReadAsync(rideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {rideId}.");

        var payment = await payments.FindForRideAsync(rideId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.NotFound,
                          $"Ride {rideId} has no computed fare yet. It is priced when the ride completes.");

        return (ride, payment);
    }
}
