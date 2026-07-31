using System.Collections.Frozen;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Ride.Rides;

/// <summary>The command fare-svc sends when a payment reaches a terminal state.</summary>
public sealed record PaymentSettledCommand(Guid RideId, Guid PaymentId, string? PaymentState, long? SettledMinor);

/// <summary>
/// <c>PaymentPending → Paid | CashSettled | CashOnDeliveryCollected | Disputed</c> (R-05, D5' §8.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only door into a settled state,</b> which is what R-05 means: "driver earning
/// posts <b>only</b> on payment terminal". The ride does not settle itself on a timer, a driver
/// cannot mark it paid, and the passenger cannot either — fare-svc drives the payment machine and
/// reports the terminal it reached. A non-terminal payment state is refused rather than stored, so
/// there is no moment at which a ride looks settled while money is still in flight.
/// </para>
/// <para>
/// The <c>ride.settled</c> event carries <c>earningPayable</c>, and it is true for exactly the
/// three terminals D5' §8.1 names ("driver earning posts only on terminal <c>Paid</c> /
/// <c>CashSettled</c> / <c>CashOnDeliveryCollected</c>"). <c>Disputed</c> is a terminal of the ride
/// and not of the money: it routes to manual review (§11.12, §11.14), so the earning stays unposted
/// until an operator resolves it.
/// </para>
/// </remarks>
public interface IRideSettlementService
{
    Task<RideRow> SettleAsync(PaymentSettledCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideSettlementService"/>
public sealed class RideSettlementService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    RideStateWriter stateWriter,
    TimeProvider timeProvider,
    ILogger<RideSettlementService> logger) : IRideSettlementService
{
    /// <summary>
    /// The <c>PaymentState</c> values (<c>_shared.yaml</c>, <c>fares.ride_payments.state</c>) that
    /// end a ride, and where each lands it.
    /// </summary>
    /// <remarks>
    /// Everything absent is a payment still in motion — <c>Initiated</c>, <c>Pending</c>,
    /// <c>Failed</c>, <c>Retried</c>, <c>CashOnDelivery</c>, the two AL-47 driver-QR steps — or a
    /// post-terminal correction that does not move the ride: <c>Overpaid</c> and <c>Refunded</c>
    /// happen to a ride that has *already* settled (§11.14 states it outright: "UPDATE rides SET
    /// state='Disputed' is NOT done"), and answering them here would drag a Paid ride backwards.
    /// </remarks>
    private static readonly FrozenDictionary<string, (string State, string ReasonCode, bool EarningPayable)> Terminals =
        new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal)
        {
            ["Succeeded"] = (RideStates.Paid, RideReasonCodes.PaymentSucceeded, true),
            ["FellBackToCash"] = (RideStates.CashSettled, RideReasonCodes.PaymentCashSettled, true),
            // Δ C050 — AL-47's driver-QR attestation terminal. It lands on CashSettled because that
            // is what it is: the passenger transferred bank-to-bank into the driver's own account,
            // MageRide held none of it and took no commission, and no gateway ever confirmed it.
            // 1002's own column comment fixed the rule before either side implemented it — "the
            // driver earning posts on DriverConfirmedQR exactly as it does on CashSettled (R-05)" —
            // and fare-svc had no terminal to report until this row existed.
            ["DriverConfirmedQR"] = (RideStates.CashSettled, RideReasonCodes.PaymentCashSettled, true),
            ["CashOnDeliveryCollected"] =
                (RideStates.CashOnDeliveryCollected, RideReasonCodes.PaymentCodCollected, true),
            ["Disputed"] = (RideStates.Disputed, RideReasonCodes.PaymentDisputed, false),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public async Task<RideRow> SettleAsync(PaymentSettledCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.PaymentId == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["paymentId"] = ["paymentId is required and must be a ULID or a UUID."],
            });
        }

        if (command.PaymentState is null || !Terminals.TryGetValue(command.PaymentState, out var terminal))
        {
            // 400 illegal-transition rather than validation-failed: `Pending` is a perfectly valid
            // PaymentState, it just is not one that settles a ride. The operation declares the code.
            throw new MageRideException(
                MageRideErrors.IllegalTransition,
                $"'{command.PaymentState}' is not a terminal payment state. Only " +
                $"{string.Join(", ", Terminals.Keys.Order(StringComparer.Ordinal))} settle a ride (R-05).");
        }

        if (command.SettledMinor is < 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["settledMinor"] = ["settledMinor must not be negative."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var settled = await rides.TerminateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            command.RideId,
            RideStates.PaymentPending,
            terminal.State,
            expectedVersion: null,
            cancellationToken);

        if (settled is null)
        {
            var current = await rides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, command.RideId, cancellationToken);

            await unitOfWork.RollbackAsync(cancellationToken);

            if (current is not null && string.Equals(current.State, terminal.State, StringComparison.Ordinal))
            {
                // The ride is already where this callback was going to put it. fare-svc's delivery
                // is at least once and R-14's replay only covers an identical `Idempotency-Key`, so
                // a repeat under a fresh key is a normal redelivery — answered with the settled
                // ride rather than an error, and writing no second transition and no second event.
                logger.LogInformation(
                    "Ride {RideId} was already {State} when {PaymentState} arrived again",
                    command.RideId, current.State, command.PaymentState);

                return current;
            }

            throw Diagnose(current, command.RideId, terminal.State);
        }

        await stateWriter.RecordAsync(
            unitOfWork,
            settled,
            fromState: RideStates.PaymentPending,
            actorType: RideTransitions.Actors.System,
            actorId: null,
            reasonCode: terminal.ReasonCode,
            [
                RideEvents.BuildSettlement(
                    settled,
                    new RideSettlementPayload(
                        PassengerId: settled.PassengerId,
                        DriverId: settled.AcceptedDriverId,
                        VehicleId: settled.AcceptedVehicleId,
                        PaymentId: command.PaymentId,
                        PaymentState: command.PaymentState,
                        State: settled.State,
                        SettledMinor: command.SettledMinor,
                        Currency: settled.Currency,
                        EarningPayable: terminal.EarningPayable),
                    Guid.NewGuid(),
                    timeProvider.GetUtcNow()),
            ],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Ride {RideId} settled {PaymentState} → {State}; driver earning payable: {EarningPayable}",
            command.RideId, command.PaymentState, settled.State, terminal.EarningPayable);

        return settled;
    }

    /// <summary>
    /// Why a settlement matched nothing, once "it is already there" has been ruled out. Every
    /// answer is <c>illegal-transition</c> or <c>not-found</c> because those are the codes
    /// <c>ride.yaml</c>'s <c>notifyPaymentSettled</c> declares.
    /// </summary>
    private static MageRideException Diagnose(RideRow? ride, Guid rideId, string target)
    {
        if (ride is null)
        {
            return RideProblems.NotFound(rideId);
        }

        if (RideStates.IsTerminal(ride.State))
        {
            return new MageRideException(
                MageRideErrors.IllegalTransition,
                $"This ride is {ride.State} and cannot be settled as {target}.");
        }

        return new MageRideException(
            MageRideErrors.IllegalTransition,
            $"The ride is {ride.State}; only a ride in {RideStates.PaymentPending} can be settled.");
    }
}
