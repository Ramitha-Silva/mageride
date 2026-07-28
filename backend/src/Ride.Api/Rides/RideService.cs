using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Fares;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Ride.Rides;

/// <summary>
/// The Mode C ride aggregate's command side — ride-svc is its sole writer (R-01, D5' §6).
/// </summary>
/// <remarks>
/// Every mutation is one transaction containing three writes: the conditional <c>UPDATE</c> on
/// <c>rides.rides</c>, the <c>rides.transitions</c> audit row (ADD Appendix B.2 invariant 4) and
/// the <c>rides.outbox</c> row (D6' §2.4, R-13). Nothing publishes directly to Redpanda — the
/// dispatcher does that after COMMIT, which is what makes "no event describes a rolled-back
/// change" true.
/// </remarks>
public interface IRideService
{
    Task<RideBooking> RequestAsync(RequestRideCommand command, CancellationToken cancellationToken);

    /// <summary>The full aggregate, for a caller who is a party to it (<c>403 not-ride-participant</c>).</summary>
    Task<RideView> GetAsync(Guid callerId, Guid rideId, CancellationToken cancellationToken);

    Task<RideView?> GetActiveForPassengerAsync(Guid callerId, Guid passengerId, CancellationToken cancellationToken);

    Task<RideView?> GetActiveForDriverAsync(Guid callerId, Guid driverId, CancellationToken cancellationToken);

    /// <summary>The ADD §11.11 atomic single-winner accept.</summary>
    Task<RideView> AcceptOfferAsync(
        Guid driverId, Guid rideId, Guid offerId, long expectedVersion, CancellationToken cancellationToken);

    Task<RideRow> DeclineOfferAsync(Guid driverId, Guid rideId, Guid offerId, CancellationToken cancellationToken);

    Task<RideRow> ArriveAsync(Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken);

    Task<RideRow> StartAsync(Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken);

    /// <summary><c>InProgress → Completed → PaymentPending</c>, both moves in one transaction.</summary>
    Task<RideRow> CompleteAsync(Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Internal: dispatch-svc has begun the candidate build.</summary>
    Task<RideRow> MarkMatchingAsync(Guid rideId, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Internal: dispatch-svc reserved a driver and is about to push the offer.</summary>
    Task<RideRow> PlaceOfferAsync(PlaceOfferCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideService"/>
public sealed class RideService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    IRideTransitionRepository transitions,
    IDriverSummaryRepository drivers,
    IOutboxWriter outbox,
    FareEstimateTokenCodec fareTokens,
    IOptions<RideOptions> options,
    TimeProvider timeProvider,
    ILogger<RideService> logger) : IRideService
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<RideBooking> RequestAsync(RequestRideCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var clientRequestId = RequireClientRequestId(command.ClientRequestId);
        var kind = RequireBookableKind(command);
        var vehicleType = RequireVehicleType(command.VehicleType);
        var pickup = RequirePlace(command.Pickup, "pickup");
        var dropoff = RequirePlace(command.Dropoff, "dropoff");
        var paymentMethod = RequirePaymentMethod(command.PaymentMethod, kind);
        RequireImmediate(command.ScheduledAt);

        var quote = RequireFareEstimate(command.FareEstimateToken, vehicleType, kind);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var result = await rides.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            new NewRide(
                PassengerId: command.PassengerId,
                ClientRequestId: clientRequestId,
                VehicleType: vehicleType,
                Pickup: pickup,
                Dropoff: dropoff,
                PaymentMethod: paymentMethod,
                FareEstimateMinor: quote.AmountMinor,
                FareSurchargeMinor: quote.SurchargeMinor),
            cancellationToken);

        switch (result.Outcome)
        {
            case RideCreateOutcome.ActiveRideExists:
                throw new MageRideException(
                    MageRideErrors.ActiveRideExists,
                    "This passenger already has a ride that has not finished. Cancel or complete it first " +
                    "(ADD Appendix B.2 invariant 1).");

            case RideCreateOutcome.AlreadyRequested:
                // R-18: the retry returns the ride the first call booked, and writes nothing —
                // no second transition row, no second ride.requested. The transaction is still
                // committed rather than rolled back so the command-log reservation the kernel
                // holds is completed against a settled state.
                await unitOfWork.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "POST /v1/rides/request replayed for client request {ClientRequestId}; ride {RideId} already exists",
                    clientRequestId, result.Ride!.Id);

                return new RideBooking(result.Ride, Replayed: true);

            case RideCreateOutcome.Created:
            default:
                break;
        }

        var ride = result.Ride!;

        await transitions.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            fromState: null,
            toState: ride.State,
            actorType: RideTransitions.Actors.Rider,
            actorId: command.PassengerId,
            reasonCode: null,
            cancellationToken);

        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(RideEventTypes.Requested, ride, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Ride {RideId} requested by passenger {PassengerId} ({VehicleType}, {AmountMinor} minor)",
            ride.Id, command.PassengerId, vehicleType, quote.AmountMinor);

        return new RideBooking(ride, Replayed: false);
    }

    public async Task<RideView> GetAsync(Guid callerId, Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var ride = await rides.FindAsync(connection, null, rideId, cancellationToken) ?? throw NotFound(rideId);

        if (!ride.IsParticipant(callerId))
        {
            throw new MageRideException(
                MageRideErrors.NotRideParticipant, "This ride belongs to another passenger and driver.");
        }

        return await ProjectAsync(connection, null, ride, cancellationToken);
    }

    public async Task<RideView?> GetActiveForPassengerAsync(
        Guid callerId, Guid passengerId, CancellationToken cancellationToken)
    {
        // A recovery read for somebody else's account would be a live-location leak, so the path
        // id must be the caller. Support reads go through admin-bff (AL-02), not this route.
        RequireSelf(callerId, passengerId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var ride = await rides.FindActiveByPassengerAsync(connection, null, passengerId, cancellationToken);

        return ride is null ? null : await ProjectAsync(connection, null, ride, cancellationToken);
    }

    public async Task<RideView?> GetActiveForDriverAsync(Guid callerId, Guid driverId, CancellationToken cancellationToken)
    {
        RequireSelf(callerId, driverId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var ride = await rides.FindActiveByDriverAsync(connection, null, driverId, cancellationToken);

        return ride is null ? null : await ProjectAsync(connection, null, ride, cancellationToken);
    }

    public async Task<RideView> AcceptOfferAsync(
        Guid driverId, Guid rideId, Guid offerId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var accepted = await rides.AcceptAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId, driverId, offerId, expectedVersion, cancellationToken);

        if (accepted is null)
        {
            // Row count 0. Everything below is diagnosis of a race that has already been decided —
            // the answer to "who won" was settled by the UPDATE above, not by this read.
            var current = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);

            if (current is not null && current.AcceptedDriverId == driverId)
            {
                // This driver already holds the ride — a repeat accept under a fresh
                // Idempotency-Key, not a loss. The contract marks the operation idempotent, so it
                // answers 200 with where the ride actually is.
                var held = await ProjectAsync(unitOfWork.Connection, unitOfWork.Transaction, current, cancellationToken);
                await unitOfWork.RollbackAsync(cancellationToken);
                return held;
            }

            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedAccept(current, rideId, driverId, offerId, expectedVersion);
        }

        // ADD §11.11's UPDATE also matches a ride in `Matching`, so the origin is not structurally
        // pinned. It is `Offered` here because the only thing in this slice that returns a ride to
        // Matching is `decline`, which clears `current_offer_id` — and the accept cannot match
        // without one. **C037 must revisit this** when the R-04 offer-expiry backstop lands: if
        // that job moves `Offered → Matching` while leaving `current_offer_id` set, the second
        // origin becomes reachable and this audit row starts lying.
        await transitions.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            accepted.Id,
            fromState: RideStates.Offered,
            toState: accepted.State,
            actorType: RideTransitions.Actors.Driver,
            actorId: driverId,
            reasonCode: null,
            cancellationToken);

        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(RideEventTypes.Accepted, accepted, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        var driver = await drivers.FindByVehicleAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            driverId,
            accepted.AcceptedVehicleId ?? Guid.Empty,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Driver {DriverId} won offer {OfferId} on ride {RideId}", driverId, offerId, rideId);

        return new RideView(accepted, driver);
    }

    public async Task<RideRow> DeclineOfferAsync(
        Guid driverId, Guid rideId, Guid offerId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var declined = await rides.DeclineOfferAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId, offerId, driverId, cancellationToken);

        if (declined is null)
        {
            var current = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedDecline(current, rideId, driverId, offerId);
        }

        await transitions.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            declined.Id,
            fromState: RideStates.Offered,
            toState: declined.State,
            actorType: RideTransitions.Actors.Driver,
            actorId: driverId,
            reasonCode: "OFFER_DECLINED",
            cancellationToken);

        // dispatch-svc's cue to release the driver and offer the next candidate (§11.12).
        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(RideEventTypes.OfferDeclined, declined, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Driver {DriverId} declined offer {OfferId}; ride {RideId} is Matching again",
            driverId, offerId, rideId);

        return declined;
    }

    public Task<RideRow> ArriveAsync(Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken) =>
        AdvanceAsDriverAsync(
            driverId, rideId, expectedVersion,
            fromStates: [RideStates.Accepted],
            toState: RideStates.DriverArrived,
            eventType: RideEventTypes.DriverArrived,
            cancellationToken);

    public Task<RideRow> StartAsync(Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken) =>
        AdvanceAsDriverAsync(
            driverId, rideId, expectedVersion,

            // The contract's `start` allows `Accepted | DriverArrived → InProgress`; ADD
            // Appendix B.2 draws only the second. The contract wins (C022 handoff, gap (e)).
            fromStates: [RideStates.Accepted, RideStates.DriverArrived],
            toState: RideStates.InProgress,
            eventType: RideEventTypes.Started,
            cancellationToken);

    public async Task<RideRow> CompleteAsync(
        Guid driverId, Guid rideId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var completed = await rides.AdvanceAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            rideId,
            [RideStates.InProgress],
            RideStates.Completed,
            expectedVersion,
            requiredDriverId: driverId,
            cancellationToken);

        if (completed is null)
        {
            var current = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedAdvance(current, rideId, driverId, [RideStates.InProgress], expectedVersion);
        }

        await transitions.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId,
            RideStates.InProgress, RideStates.Completed,
            RideTransitions.Actors.Driver, driverId, null, cancellationToken);

        // The fare is owed the moment the trip ends, so the ride never rests in Completed: D5' §6
        // draws `Completed --> PaymentPending: fare finalised` and the contract's `complete`
        // returns `{state: PaymentPending}`. Both moves are in this transaction, so a crash
        // between them is impossible and the audit still shows the pair.
        var pending = await rides.AdvanceAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            rideId,
            [RideStates.Completed],
            RideStates.PaymentPending,
            completed.Version,
            requiredDriverId: null,
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.InternalError,
                "The ride moved out of Completed inside its own completion transaction.");

        await transitions.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId,
            RideStates.Completed, RideStates.PaymentPending,
            RideTransitions.Actors.System, null, "FARE_HANDOFF", cancellationToken);

        // One event, not two. D6' §2.2 registers `ride.completed` and nothing for the payment
        // hand-off; the state on the envelope is PaymentPending, which is where the ride actually
        // is by the time a consumer reads it. fare-svc's `POST /v1/fare/calculate` is C049/C050 —
        // until then this event is the entire hand-off, and no earning posts (R-05).
        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(RideEventTypes.Completed, pending, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Ride {RideId} completed by driver {DriverId}; awaiting payment", rideId, driverId);

        return pending;
    }

    public async Task<RideRow> MarkMatchingAsync(Guid rideId, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var matching = await rides.MarkMatchingAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId, expectedVersion, cancellationToken);

        if (matching is null)
        {
            var current = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedAdvance(current, rideId, actorId: null, [RideStates.Requested], expectedVersion);
        }

        await transitions.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, rideId,
            RideStates.Requested, RideStates.Matching,
            RideTransitions.Actors.System, null, "DISPATCH_CANDIDATE_BUILD", cancellationToken);

        // No outbox row: dispatch-svc drove this move, so an event would only tell it what it just
        // did, and D6' §2.2 registers no name for one.
        await unitOfWork.CommitAsync(cancellationToken);

        return matching;
    }

    public async Task<RideRow> PlaceOfferAsync(PlaceOfferCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var ttl = command.TtlSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : _options.OfferTtl;

        if (ttl <= TimeSpan.Zero || ttl > RideOptions.MaxOfferTtl)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["ttlSeconds"] = [$"ttlSeconds must be between 1 and {RideOptions.MaxOfferTtl.TotalSeconds:0}."],
            });
        }

        // The deadline is stamped from this service's clock rather than taken from dispatch-svc's
        // body, because it is this service's `offer_expires_at > now()` that decides an accept
        // (§11.11). Two clocks would make the boundary unfalsifiable.
        var expiresAt = timeProvider.GetUtcNow().Add(ttl);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var offered = await rides.PlaceOfferAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            command.RideId,
            command.OfferId,
            command.DriverId,
            command.VehicleId,
            expiresAt,
            command.ExpectedVersion,
            cancellationToken);

        if (offered is null)
        {
            var current = await rides.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, command.RideId, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedAdvance(
                current, command.RideId, actorId: null, [RideStates.Matching], command.ExpectedVersion);
        }

        await transitions.RecordAsync(
            unitOfWork.Connection, unitOfWork.Transaction, command.RideId,
            RideStates.Matching, RideStates.Offered,
            RideTransitions.Actors.System, command.DriverId, "OFFER_SENT", cancellationToken);

        // R-13: the driver's push is sent by whoever consumes this row, which cannot happen before
        // COMMIT — so there is no such thing as a phantom offer.
        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(RideEventTypes.OfferCreated, offered, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Offer {OfferId} on ride {RideId} reserved for driver {DriverId} until {ExpiresAt:O}",
            command.OfferId, command.RideId, command.DriverId, expiresAt);

        return offered;
    }

    // -------------------------------------------------------------------------------------------
    // Shared machinery
    // -------------------------------------------------------------------------------------------

    private async Task<RideRow> AdvanceAsDriverAsync(
        Guid driverId,
        Guid rideId,
        long expectedVersion,
        string[] fromStates,
        string toState,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // One conditional UPDATE per legal origin, rather than one `state = ANY(...)`. Only one can
        // match — the ride is in exactly one state — and this way the audit row records which
        // origin it actually was instead of leaving `from_state` null on the two-origin `start`.
        RideRow? advanced = null;
        string? fromState = null;

        foreach (var candidate in fromStates)
        {
            advanced = await rides.AdvanceAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                rideId,
                [candidate],
                toState,
                expectedVersion,
                requiredDriverId: driverId,
                cancellationToken);

            if (advanced is not null)
            {
                fromState = candidate;
                break;
            }
        }

        if (advanced is null)
        {
            // Row count 0 every time: the ride does not exist, is somewhere else, is at another
            // version, or belongs to another driver. Only now is a read worth making, and only to
            // say which.
            var current = await rides.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);

            throw DiagnoseFailedAdvance(current, rideId, driverId, fromStates, expectedVersion);
        }

        await transitions.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            rideId,
            fromState: fromState,
            toState: toState,
            actorType: RideTransitions.Actors.Driver,
            actorId: driverId,
            reasonCode: null,
            cancellationToken);

        await outbox.WriteAsync(
            unitOfWork,
            RideEvents.Build(eventType, advanced, Guid.NewGuid(), timeProvider.GetUtcNow()),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return advanced;
    }

    private async Task<RideView> ProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RideRow ride,
        CancellationToken cancellationToken)
    {
        if (ride.AcceptedDriverId is not { } driverId || ride.AcceptedVehicleId is not { } vehicleId)
        {
            return new RideView(ride, null);
        }

        return new RideView(
            ride, await drivers.FindByVehicleAsync(connection, transaction, driverId, vehicleId, cancellationToken));
    }

    /// <summary>
    /// Why an accept lost. The order matters: "somebody else won" has to be answered before
    /// "your version was stale", because both are true for the loser of a race and only the first
    /// tells the driver app to show the next offer (§11.11).
    /// </summary>
    private MageRideException DiagnoseFailedAccept(
        RideRow? ride, Guid rideId, Guid driverId, Guid offerId, long expectedVersion)
    {
        if (ride is null)
        {
            return NotFound(rideId);
        }

        if (ride.AcceptedDriverId is not null)
        {
            return new MageRideException(
                MageRideErrors.OfferAlreadyAccepted, "Another driver accepted this ride first.");
        }

        if (RideStates.IsTerminal(ride.State))
        {
            return new MageRideException(
                MageRideErrors.RideTerminal, $"This ride is {ride.State} and can no longer be accepted.");
        }

        if (ride.CurrentOfferId != offerId)
        {
            // The offer was declined, expired and re-issued, or never existed. Either way the
            // offer this driver is holding is not the live one.
            return new MageRideException(
                MageRideErrors.OfferAlreadyAccepted, "This offer is no longer the ride's live offer.");
        }

        if (ride.OfferExpiresAt is not { } expiresAt || expiresAt <= timeProvider.GetUtcNow())
        {
            return new MageRideException(MageRideErrors.OfferExpired, "The 15-second offer window has closed.");
        }

        if (ride.OfferedDriverId != driverId)
        {
            return new MageRideException(
                MageRideErrors.NotRideParticipant, "This offer was reserved for another driver.");
        }

        if (ride.Version != expectedVersion)
        {
            return new MageRideException(
                MageRideErrors.VersionConflict,
                $"The ride has moved on: it is at version {ride.Version}, not {expectedVersion}.");
        }

        return new MageRideException(
            MageRideErrors.IllegalTransition, $"A ride in {ride.State} cannot be accepted.");
    }

    private static MageRideException DiagnoseFailedDecline(RideRow? ride, Guid rideId, Guid driverId, Guid offerId)
    {
        if (ride is null)
        {
            return NotFound(rideId);
        }

        if (ride.OfferedDriverId != driverId && ride.AcceptedDriverId != driverId)
        {
            return new MageRideException(
                MageRideErrors.NotRideParticipant, "This offer was reserved for another driver.");
        }

        if (ride.CurrentOfferId != offerId)
        {
            return new MageRideException(MageRideErrors.OfferExpired, "This offer is no longer live.");
        }

        return new MageRideException(
            MageRideErrors.IllegalTransition, $"A ride in {ride.State} has no offer to decline.");
    }

    private MageRideException DiagnoseFailedAdvance(
        RideRow? ride, Guid rideId, Guid? actorId, IReadOnlyCollection<string> fromStates, long? expectedVersion)
    {
        if (ride is null)
        {
            return NotFound(rideId);
        }

        if (actorId is { } driverId && ride.AcceptedDriverId != driverId)
        {
            return new MageRideException(
                MageRideErrors.NotRideParticipant, "This ride was accepted by another driver.");
        }

        if (RideStates.IsTerminal(ride.State))
        {
            return new MageRideException(MageRideErrors.RideTerminal, $"This ride is {ride.State}.");
        }

        if (!fromStates.Contains(ride.State, StringComparer.Ordinal))
        {
            return new MageRideException(
                MageRideErrors.IllegalTransition,
                $"The ride is {ride.State}; this command is only legal from {string.Join(" or ", fromStates)}.");
        }

        if (expectedVersion is { } expected && ride.Version != expected)
        {
            return new MageRideException(
                MageRideErrors.VersionConflict,
                $"The ride has moved on: it is at version {ride.Version}, not {expected}.");
        }

        return new MageRideException(MageRideErrors.Conflict, "The ride could not be moved from where it stands.");
    }

    private static MageRideException NotFound(Guid rideId) =>
        new(MageRideErrors.NotFound, $"No ride {rideId}.");

    private static void RequireSelf(Guid callerId, Guid subjectId)
    {
        if (callerId != subjectId)
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "A recovery read is only ever for the caller's own account.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Request validation
    // -------------------------------------------------------------------------------------------

    private static Guid RequireClientRequestId(string? value)
    {
        if (!Ulids.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["clientRequestId"] = ["clientRequestId is required and must be a ULID or a UUID (R-18)."],
            });
        }

        return parsed;
    }

    /// <summary>
    /// Only <c>passenger</c> is bookable in this slice. Proxy needs the P-02 location-request
    /// round-trip and a rider identity (<c>ck_rides_proxy_identity</c>); package needs a size and
    /// both OTP hashes (<c>ck_rides_package_complete</c>). Both are C032/C037, and a booking that
    /// skipped them would be rejected by the database anyway — as a 500, not as an answer.
    /// </summary>
    private static string RequireBookableKind(RequestRideCommand command)
    {
        var kind = string.IsNullOrWhiteSpace(command.Kind) ? RideKinds.Passenger : command.Kind;

        if (!RideKinds.All.Contains(kind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["kind"] = [$"kind must be one of {string.Join(", ", RideKinds.All.Order(StringComparer.Ordinal))}."],
            });
        }

        if (kind != RideKinds.Passenger || command.IsProxy == true || command.PackageSize is not null)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["kind"] =
                [
                    "Only 'passenger' rides are bookable in this build. Proxy booking (P-01..P-05) and package " +
                    "delivery (P-06..P-11) are C032/C037.",
                ],
            });
        }

        return kind;
    }

    private static string RequireVehicleType(string? vehicleType)
    {
        if (!RideVehicleTypes.IsBookable(vehicleType))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["vehicleType"] =
                [
                    "vehicleType must be a Mode C bookable tier (AL-09): " +
                    string.Join(", ", RideVehicleTypes.Passenger.Concat(RideVehicleTypes.Delivery).Order(StringComparer.Ordinal)) + ".",
                ],
            });
        }

        return vehicleType!;
    }

    private static GeoPoint RequirePlace(RidePlace? place, string field)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (place?.Lat is not { } lat || double.IsNaN(lat) || lat is < -90 or > 90)
        {
            errors[$"{field}.lat"] = [$"{field}.lat is required and must be between -90 and 90."];
        }

        if (place?.Lng is not { } lng || double.IsNaN(lng) || lng is < -180 or > 180)
        {
            errors[$"{field}.lng"] = [$"{field}.lng is required and must be between -180 and 180."];
        }

        return errors.Count == 0
            ? new GeoPoint(place!.Lat!.Value, place.Lng!.Value)
            : throw new MageRideValidationException(errors);
    }

    private static string RequirePaymentMethod(string? paymentMethod, string kind)
    {
        if (paymentMethod is null || !RidePaymentMethods.All.Contains(paymentMethod))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["paymentMethod"] =
                [
                    $"paymentMethod must be one of {string.Join(", ", RidePaymentMethods.All.Order(StringComparer.Ordinal))}.",
                ],
            });
        }

        // D3': "cod=package only".
        if (paymentMethod == RidePaymentMethods.CashOnDelivery && kind != RideKinds.Package)
        {
            throw new MageRideException(
                MageRideErrors.PaymentMethodInvalid, "Cash on delivery is only available on a package booking (P-08).");
        }

        return paymentMethod;
    }

    /// <summary>
    /// A scheduled ride is a Job Board posting, not a dispatch (D-06, US-6A.5), and the whole
    /// scheduling path — <c>dispatch.scheduled_rides</c>, the intent flow, the Quartz trigger — is
    /// C035. Refusing is the honest answer; accepting and dispatching immediately would send a
    /// driver to a passenger who asked for tomorrow.
    /// </summary>
    private static void RequireImmediate(DateTimeOffset? scheduledAt)
    {
        if (scheduledAt is not null)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["scheduledAt"] = ["Scheduled rides are not available in this build (C035). Omit scheduledAt for immediate dispatch."],
            });
        }
    }

    private FareEstimateClaims RequireFareEstimate(string? token, string vehicleType, string kind)
    {
        if (!fareTokens.TryRead(token, out var claims, out var failure))
        {
            throw new MageRideException(
                MageRideErrors.InvalidFareToken,
                failure switch
                {
                    FareEstimateTokenFailure.Expired =>
                        "The fare estimate has expired. Ask fare-svc for a new one and re-submit.",
                    FareEstimateTokenFailure.BadSignature =>
                        "The fare estimate was not issued by fare-svc.",
                    _ => "fareEstimateToken is required and must be the opaque token from GET /v1/fare/estimate.",
                });
        }

        // The quote is per tier (D5' §1.1 prices each vehicle type separately), so a token for a
        // motorbike cannot book a van at the motorbike's price.
        if (!string.Equals(claims.VehicleType, vehicleType, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.InvalidFareToken,
                $"The fare estimate was issued for '{claims.VehicleType}', not '{vehicleType}'.");
        }

        var expectedKind = kind == RideKinds.Package ? "package" : "passenger";
        if (!string.Equals(claims.Kind, expectedKind, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.InvalidFareToken,
                $"The fare estimate was issued for a '{claims.Kind}' trip, not a '{expectedKind}' one.");
        }

        return claims;
    }
}
