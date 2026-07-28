using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>What a cancellation did, and what it costs.</summary>
/// <param name="PenaltyMinor">
/// LKR minor units, 0 when the matrix's Penalty column says None. Accrued against the passenger's
/// next trip, never collected here (D5' §7.1).
/// </param>
public sealed record RideCancellation(RideRow Ride, RideCancellationOutcome Outcome, long PenaltyMinor);

/// <summary>
/// The server-owned cancellation and no-show matrix (R-03, D5' §7, ADD §11.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller says what happened; the matrix says what it means.</b> Every path here resolves
/// <see cref="RideCancellationMatrix"/> from (the state the ride is actually in) × (the trigger),
/// and the conditional <c>UPDATE</c> is then bound to that same state — so a ride that moved on
/// between the read and the write is never terminated under another row's rules. The client's
/// <c>reason</c> is recorded and published; it decides nothing.
/// </para>
/// <para>
/// <b>No money moves here.</b> The Rs 50 (D-05), the Rs 100 no-show fee and the mid-trip full fare
/// are *accrued* — stated as owed on <c>cancellation.penalty.accrued</c> and settled by fare-svc
/// against the passenger's next completed trip, keyed <c>penaltyId:rideId</c> (D5' §7.1). ride-svc
/// writes no ledger entry and no <c>dispatch.cancellation_penalties</c> row: those tables belong to
/// other bounded contexts, and the fence for this component is that cross-service state changes go
/// through the outbox and nothing else.
/// </para>
/// </remarks>
public interface IRideCancellationService
{
    /// <summary>
    /// <c>POST /v1/rides/{rideId}/cancel</c>. The trigger is derived from who is authenticated —
    /// a rider cancel and a driver cancel are different rows of the matrix and a caller does not
    /// get to choose which one they are.
    /// </summary>
    Task<RideCancellation> CancelAsync(
        Guid callerId, Guid rideId, string? reason, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/internal/rides/{rideId}/system-cancel</c> — dispatch-svc on an expired grace or
    /// an exhausted candidate cascade, reputation-svc on a fraud lock, admin-bff on an operator's
    /// intervention.
    /// </summary>
    Task<RideCancellation> SystemCancelAsync(Guid rideId, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a trigger that no one asked for: a durable timer fired.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the matrix has no row for the state the ride is now in — the
    /// normal race in which the thing the timer was watching for stopped being possible (the driver
    /// arrived, the rider cancelled first, the ride was already settled). A fired timer that finds
    /// nothing to do is not a fault.
    /// </returns>
    Task<RideCancellation?> TryApplyAsync(
        Guid rideId, RideCancellationTrigger trigger, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideCancellationService"/>
public sealed class RideCancellationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    RideStateWriter stateWriter,
    IOptions<RideOptions> options,
    TimeProvider timeProvider,
    ILogger<RideCancellationService> logger) : IRideCancellationService
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The four values <c>ride.yaml</c>'s <c>system-cancel</c> body accepts.</summary>
    private static readonly IReadOnlyDictionary<string, RideCancellationTrigger> SystemReasons =
        new Dictionary<string, RideCancellationTrigger>(StringComparer.Ordinal)
        {
            ["driver_offline_grace_expired"] = RideCancellationTrigger.DriverOfflineGraceExpired,
            ["fraud_lock"] = RideCancellationTrigger.FraudLock,
            ["no_driver_found"] = RideCancellationTrigger.NoDriverFound,
            ["admin_intervention"] = RideCancellationTrigger.AdminIntervention,
        };

    /// <summary>The four values <c>ride.yaml</c>'s <c>cancel</c> body accepts.</summary>
    private static readonly HashSet<string> ClientReasons =
        new(StringComparer.Ordinal) { "RIDER_CHANGED_MIND", "DRIVER_TOO_FAR", "EMERGENCY", "OTHER" };

    public async Task<RideCancellation> CancelAsync(
        Guid callerId, Guid rideId, string? reason, long expectedVersion, CancellationToken cancellationToken)
    {
        if (reason is null || !ClientReasons.Contains(reason))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = [$"reason must be one of {string.Join(", ", ClientReasons.Order(StringComparer.Ordinal))}."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

        if (ride is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw RideProblems.NotFound(rideId);
        }

        // Which side of the ride the caller is on is what decides the row. The accepted driver is
        // the only driver who can cancel — a driver still holding an *offer* declines it, which is
        // a different route and a different (non-terminal) outcome.
        var trigger = ride.AcceptedDriverId == callerId
            ? RideCancellationTrigger.DriverCancel
            : ride.PassengerId == callerId || ride.BookerId == callerId || ride.RiderId == callerId
                ? RideCancellationTrigger.RiderCancel
                : throw await ForbidAsync(unitOfWork, cancellationToken);

        if (ride.Version != expectedVersion)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.VersionConflict,
                $"The ride has moved on: it is at version {ride.Version}, not {expectedVersion}.");
        }

        return await ApplyAsync(
            unitOfWork, ride, trigger, actorId: callerId, clientReason: reason,
            expectedVersion: expectedVersion, cancellationToken);
    }

    public async Task<RideCancellation> SystemCancelAsync(
        Guid rideId, string? reason, CancellationToken cancellationToken)
    {
        if (reason is null || !SystemReasons.TryGetValue(reason, out var trigger))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["reason"] = [$"reason must be one of {string.Join(", ", SystemReasons.Keys.Order(StringComparer.Ordinal))}."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

        if (ride is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw RideProblems.NotFound(rideId);
        }

        // No version echo: the contract's body is `{reason}` alone, because the caller is a system
        // reacting to something that has already happened rather than a client holding a snapshot.
        // The state predicate on the UPDATE is what keeps it safe.
        return await ApplyAsync(
            unitOfWork, ride, trigger, actorId: null, clientReason: null,
            expectedVersion: null, cancellationToken);
    }

    public async Task<RideCancellation?> TryApplyAsync(
        Guid rideId, RideCancellationTrigger trigger, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var ride = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

        if (ride is null || !RideCancellationMatrix.TryResolve(ride.State, trigger, out _))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return null;
        }

        try
        {
            return await ApplyAsync(
                unitOfWork, ride, trigger, actorId: null, clientReason: null,
                expectedVersion: null, cancellationToken);
        }
        catch (MageRideException exception)
        {
            // The ride moved between this transaction's read and its write. Same normal race as
            // above; the caller is a timer with nobody to answer to.
            logger.LogDebug(
                exception, "Ride {RideId} moved on before the {Trigger} trigger could be applied", rideId, trigger);

            return null;
        }
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The one place a ride becomes terminal. Resolves the matrix, performs the guarded update and
    /// writes the audit row, the timers and the events the row's Events column names.
    /// </summary>
    private async Task<RideCancellation> ApplyAsync(
        IUnitOfWork unitOfWork,
        RideRow ride,
        RideCancellationTrigger trigger,
        Guid? actorId,
        string? clientReason,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!RideCancellationMatrix.TryResolve(ride.State, trigger, out var outcome))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw NoSuchCell(ride, trigger);
        }

        var terminated = await rides.TerminateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            ride.State,
            outcome.ToState,
            expectedVersion,
            cancellationToken);

        if (terminated is null)
        {
            // Row count 0: the ride left `ride.State` between the read and the write. Only now is a
            // second read worth making, and only to say which race it lost.
            var current = await rides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, ride.Id, cancellationToken);

            await unitOfWork.RollbackAsync(cancellationToken);

            throw RideProblems.Raced(current, ride.Id, expectedVersion);
        }

        var penaltyMinor = PenaltyFor(outcome.Penalty, ride);
        var events = BuildEvents(terminated, ride.State, outcome, penaltyMinor, clientReason);

        await stateWriter.RecordAsync(
            unitOfWork,
            terminated,
            fromState: ride.State,
            actorType: outcome.ActorType,
            actorId: actorId,
            reasonCode: outcome.ReasonCode,
            events,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Ride {RideId} moved {FromState} → {ToState} ({ReasonCode}); penalty {PenaltyMinor} minor",
            ride.Id, ride.State, terminated.State, outcome.ReasonCode, penaltyMinor);

        return new RideCancellation(terminated, outcome, penaltyMinor);
    }

    /// <summary>The §11.12 "Events emitted" column, built.</summary>
    private IReadOnlyCollection<OutboxRecord> BuildEvents(
        RideRow terminated,
        string fromState,
        RideCancellationOutcome outcome,
        long penaltyMinor,
        string? clientReason)
    {
        var now = timeProvider.GetUtcNow();

        var events = new List<OutboxRecord>(3)
        {
            RideEvents.Build(
                outcome.EventType, terminated, Guid.NewGuid(), now, outcome.ReasonCode, clientReason),
        };

        if (outcome.Penalty is not RidePenaltyBasis.None)
        {
            events.Add(RideEvents.BuildPenalty(
                terminated,
                new RidePenaltyPayload(
                    PassengerId: terminated.PassengerId,
                    AffectedDriverId: terminated.AcceptedDriverId,
                    AmountMinor: penaltyMinor,
                    Currency: terminated.Currency,
                    Basis: PenaltyBasisName(outcome.Penalty),
                    ReasonCode: outcome.ReasonCode,
                    FromState: fromState,

                    // D5' §7.1: accrued now, collected on the passenger's next completed trip.
                    // There is no card on file, so there is nothing to charge at this moment.
                    SettledOn: RideSettlement.NextTrip,
                    DriverCompensationBasis: CompensationBasis(outcome.Penalty)),
                Guid.NewGuid(),
                now));
        }

        if (outcome.ReputationHit && terminated.AcceptedDriverId is { } driverId)
        {
            events.Add(RideEvents.BuildReputation(
                terminated,
                new RideReputationPayload(
                    DriverId: driverId,
                    VehicleId: terminated.AcceptedVehicleId,
                    PassengerId: terminated.PassengerId,
                    FromState: fromState,
                    ToState: terminated.State,
                    ReasonCode: outcome.ReasonCode,
                    SystemInitiated: outcome.ActorType != RideTransitions.Actors.Driver),
                Guid.NewGuid(),
                now));
        }

        return events;
    }

    private long PenaltyFor(RidePenaltyBasis basis, RideRow ride) => basis switch
    {
        RidePenaltyBasis.RiderCancellation => _options.CancellationPenaltyMinor,
        RidePenaltyBasis.RiderNoShow => _options.NoShowPenaltyMinor,

        // The quote, because it is the only number this service holds — the metered fare is
        // fare-svc's (D5' §1.4) and the ride never reached Completed, so nothing computed one.
        // `basis: full_fare` on the event is what tells fare-svc to replace it.
        RidePenaltyBasis.FullFare => (ride.FareEstimateMinor ?? 0) + ride.FareSurchargeMinor,
        _ => 0,
    };

    private static string PenaltyBasisName(RidePenaltyBasis basis) => basis switch
    {
        RidePenaltyBasis.RiderCancellation => "cancellation_fee",
        RidePenaltyBasis.RiderNoShow => "no_show_fee",
        RidePenaltyBasis.FullFare => "full_fare",
        _ => "none",
    };

    /// <summary>What the driver is owed, as a rule rather than an amount.</summary>
    /// <remarks>
    /// §11.12 gives the rider no-show row "driver compensation = base fare/2". The base fare is per
    /// tier (D5' §1.1) and lives in <c>fares.tariffs</c>, so the rule travels and fare-svc resolves
    /// it. The other two are the whole penalty passing through to the affected driver (D5' §7.1).
    /// </remarks>
    private static string? CompensationBasis(RidePenaltyBasis basis) => basis switch
    {
        RidePenaltyBasis.RiderCancellation => "cancellation_fee",
        RidePenaltyBasis.RiderNoShow => "base_fare_half",
        RidePenaltyBasis.FullFare => "full_fare",
        _ => null,
    };

    private static async Task<MageRideException> ForbidAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        await unitOfWork.RollbackAsync(cancellationToken);

        return new MageRideException(
            MageRideErrors.NotRideParticipant,
            "Only the rider, the booker or the driver who accepted the ride can cancel it.");
    }

    /// <summary>
    /// Why the matrix has no row for this ride. A terminal ride is a 409 — the request was
    /// coherent and simply arrived after the ride ended; anything else is a 400, because the
    /// combination of state and trigger is not a thing that can happen.
    /// </summary>
    private static MageRideException NoSuchCell(RideRow ride, RideCancellationTrigger trigger)
    {
        if (RideStates.IsTerminal(ride.State))
        {
            return new MageRideException(
                MageRideErrors.RideTerminal, $"This ride is already {ride.State}.");
        }

        var legal = RideCancellationMatrix.StatesFor(trigger);

        return new MageRideException(
            MageRideErrors.IllegalTransition,
            legal.Count == 0
                ? $"'{trigger}' is not a transition this ride can make."
                : $"The ride is {ride.State}; this outcome only applies from {string.Join(" or ", legal)}.");
    }
}

/// <summary>Where an accrued penalty is collected (the contract's <c>settledOn</c>).</summary>
public static class RideSettlement
{
    /// <summary>D5' §7.1 — added to the fare of the passenger's next ride.</summary>
    public const string NextTrip = "next-trip";
}

/// <summary>Problem responses more than one ride service needs to raise.</summary>
internal static class RideProblems
{
    public static MageRideException NotFound(Guid rideId) =>
        new(MageRideErrors.NotFound, $"No ride {rideId}.");

    /// <summary>
    /// A guarded <c>UPDATE</c> matched nothing. The ride was read a moment ago, so the only
    /// explanations are that it moved state or moved version.
    /// </summary>
    public static MageRideException Raced(RideRow? current, Guid rideId, long? expectedVersion)
    {
        if (current is null)
        {
            return NotFound(rideId);
        }

        if (expectedVersion is { } expected && current.Version != expected)
        {
            return new MageRideException(
                MageRideErrors.VersionConflict,
                $"The ride has moved on: it is at version {current.Version}, not {expected}.");
        }

        if (RideStates.IsTerminal(current.State))
        {
            return new MageRideException(MageRideErrors.RideTerminal, $"This ride is already {current.State}.");
        }

        return new MageRideException(
            MageRideErrors.Conflict, $"The ride moved to {current.State} while this command was in flight.");
    }
}
