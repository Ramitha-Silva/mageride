using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Eligibility;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Redis;
using MageRide.Shared.Geo;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Dispatching;

/// <summary>Everything a candidate build needs, as <c>ride.requested</c> already carries it.</summary>
/// <remarks>
/// Deliberately a value, not a ride id to go and read: <c>rides.rides</c> is ride-svc's table and
/// dispatch-svc neither reads nor writes it. Every <c>ride.events</c> envelope carries the whole
/// payload (C022's <c>RideEventPayload</c>), so <c>offer.declined</c> and <c>offer.expired</c> are
/// enough to re-run a round without a single cross-service read.
/// </remarks>
/// <param name="PassengerId">
/// Whose ride it is — the US-12.10 <c>safety.blocked_drivers</c> gate is per (passenger, driver)
/// pair and cannot be applied without it.
/// </param>
/// <param name="PackageSize">
/// <c>S</c> | <c>M</c> | <c>L</c> for a <c>kind=package</c> ride, else <see langword="null"/>. The
/// P-11 compatibility gate's input.
/// </param>
public sealed record RideDispatchRequest(
    Guid RideId,
    GeoPoint Pickup,
    string VehicleType,
    string PaymentMethod,
    long? FareEstimateMinor,
    string Currency,
    Guid? PassengerId = null,
    string Kind = RideKinds.Passenger,
    string? PackageSize = null);

/// <summary>How a dispatch round ended.</summary>
public enum DispatchResult
{
    /// <summary>An offer is live and <c>offer.created</c> is committed.</summary>
    Offered,

    /// <summary>Nobody eligible was near enough. The ride stays in Matching until the global timeout.</summary>
    NoCandidate,

    /// <summary>ride-svc would not move the ride — accepted, cancelled, or already offered.</summary>
    RideNotDispatchable,

    /// <summary>
    /// The cascade is over and the ride was system-cancelled into <c>ExpiredNoDriver</c> — either
    /// the 120 s global timeout or <c>Dispatch:MaxOfferRounds</c> (US-6A.11, ADD §11.12).
    /// </summary>
    ExpiredNoDriver,
}

/// <param name="PreFilterCount">Drivers the H3 ring returned, before any distance was applied.</param>
/// <param name="CandidateCount">Drivers that survived the exact <c>ST_DWithin</c> post-filter.</param>
/// <param name="EligibleCount">Of those, how many survived the D5' §3.2 hard gates.</param>
public sealed record DispatchOutcome(
    DispatchResult Result,
    Guid? OfferId,
    Guid? DriverId,
    DateTimeOffset? ExpiresAt,
    long? Version,
    int PreFilterCount,
    int CandidateCount,
    int EligibleCount = 0);

/// <summary>
/// The Mode C offer loop: candidate build, hard gates, weighted scoring, reservation and the
/// cascade (D5' §3, ADD §11.11).
/// </summary>
public interface IDispatchService
{
    /// <summary>Runs one round: score the eligible candidates and offer the ride to the best one.</summary>
    Task<DispatchOutcome> DispatchAsync(RideDispatchRequest ride, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Requested → Matching</c>, arm the US-6A.11 global deadline, then a first round. What
    /// <c>ride.requested</c> triggers.
    /// </summary>
    Task<DispatchOutcome> BeginAsync(RideDispatchRequest ride, CancellationToken cancellationToken);

    /// <summary>Fires the R-04 backstop for one due offer timer.</summary>
    /// <param name="driverUnreachable">
    /// R-15's path rather than R-04's: the driver's session is gone, so the offer may be revoked
    /// inside its own window.
    /// </param>
    Task ExpireAsync(DueOfferTimer timer, bool driverUnreachable, CancellationToken cancellationToken);

    /// <summary>Fires one due <c>dispatch.timers</c> row — the global timeout or an R-15 grace.</summary>
    Task RunTimerAsync(DueDispatchTimer timer, CancellationToken cancellationToken);

    /// <summary>
    /// Settles whichever offer is live on a ride and puts its driver back in the pool (§11.12,
    /// ADD §9.4's "driver availability after terminal cancellation").
    /// </summary>
    /// <remarks>
    /// Keyed by ride rather than by offer because <b><c>offer.declined</c> and
    /// <c>offer.expired</c> name neither the driver nor the offer</b>: ride-svc builds the
    /// envelope from the row <em>after</em> the update, and both moves clear
    /// <c>offered_driver_id</c> and <c>current_offer_id</c>, so the payload's <c>driverId</c> and
    /// <c>offerId</c> are absent. <c>dispatch.offers</c> is where dispatch already knows the
    /// answer, so nothing is lost — recorded as a contract gap in the C023 handoff.
    /// </remarks>
    Task ReleaseLiveOfferAsync(Guid rideId, string toStatus, CancellationToken cancellationToken);

    /// <summary>
    /// R-15: the driver's EMQX session dropped and the grace ran out. Releases whichever offer they
    /// are holding so the ride can cascade, and takes them out of the pool.
    /// </summary>
    Task<bool> ReleaseDriverOfferAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The driver won the ride: the offer is ACCEPTED and they leave the pool.</summary>
    Task MarkAcceptedAsync(Guid rideId, Guid driverId, CancellationToken cancellationToken);

    /// <summary>The ride is over: the driver goes back into the pool where they stand.</summary>
    Task ReturnToPoolAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The ride left dispatch's hands — retire its global cascade deadline.</summary>
    Task RetireRideAsync(Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDispatchService"/>
public sealed class DispatchService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IPresenceRepository presence,
    ICandidateRepository candidates,
    IOfferRepository offers,
    IOfferTimerRepository timers,
    IDispatchTimerRepository dispatchTimers,
    IDriverIndex index,
    IRideServiceClient rideService,
    IReputationGate reputationGate,
    IWalletGate walletGate,
    ICandidateScorer scorer,
    IOutboxWriter outbox,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<DispatchService> logger) : IDispatchService
{
    /// <summary>
    /// Slack added to the backstop's fire time. R-04 wants the durable job to fire "within 1 s of
    /// expiry"; firing a hair *early* is worse than a hair late, because ride-svc evaluates
    /// <c>offer_expires_at &lt;= now()</c> and would answer 409 to a premature sweep.
    /// </summary>
    private static readonly TimeSpan BackstopGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The <c>system-cancel</c> reason that resolves to <c>ExpiredNoDriver</c> — one of the four
    /// <c>ride.yaml</c> accepts, and the only cell of §11.12's matrix that produces that state.
    /// </summary>
    private const string NoDriverFound = "no_driver_found";

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DispatchOutcome> BeginAsync(RideDispatchRequest ride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ride);

        // Requested → Matching. Idempotent on ride-svc's side (deterministic key + a `state =
        // 'Requested'` predicate), so a redelivered ride.requested does not need dedupe state here
        // — at-least-once delivery is the contract (D6' §2.3) and this is how it is absorbed.
        var matching = await rideService.MarkMatchingAsync(ride.RideId, version: null, cancellationToken);

        if (!matching.Succeeded && matching.Status != System.Net.HttpStatusCode.Conflict)
        {
            logger.LogWarning(
                "ride-svc refused to move ride {RideId} to Matching ({Status}/{ErrorCode}); not dispatching",
                ride.RideId, (int)matching.Status, matching.ErrorCode);

            return Nothing(DispatchResult.RideNotDispatchable);
        }

        // US-6A.11's clock starts here, not on the first offer: the two minutes are the passenger's
        // wait, and a ride nobody was ever near has to end as well as one that was declined eight
        // times. Armed *before* the first round so a process that dies mid-round still leaves a
        // deadline behind; ux_dispatch_timers_ride_live absorbs the redelivery.
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            var deadline = await dispatchTimers.ArmRideTimeoutAsync(
                connection, null, ride.RideId, timeProvider.GetUtcNow().Add(_options.GlobalTimeout), cancellationToken);

            logger.LogInformation(
                "Ride {RideId} is Matching; the cascade must produce a driver by {Deadline:O}",
                ride.RideId, deadline);
        }

        // A 409 means the ride is already past Requested — a redelivery, or another replica got
        // there first. Dispatching anyway is correct: the offer call is guarded on `Matching`, so
        // a ride that has moved further along simply refuses it.
        return await DispatchAsync(ride, cancellationToken);
    }

    public async Task<DispatchOutcome> DispatchAsync(RideDispatchRequest ride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ride);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var round = await offers.CountForRideAsync(connection, ride.RideId, cancellationToken);
        var deadline = await dispatchTimers.FindRideDeadlineAsync(connection, ride.RideId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // §11.12: "No candidates after N rounds OR timeout". Both ends of the OR land here, and
        // both end the ride the same way — the passenger is told nobody is coming rather than left
        // watching a spinner (US-6A.11).
        if (round >= _options.MaxOfferRounds)
        {
            logger.LogInformation(
                "Ride {RideId} has been through {Rounds} offers, the configured maximum; giving up",
                ride.RideId, round);

            return await GiveUpAsync(ride.RideId, "rounds-exhausted", cancellationToken);
        }

        if (deadline is { } expiresAt && expiresAt <= now)
        {
            logger.LogInformation("Ride {RideId} passed its {Timeout} cascade deadline; giving up",
                ride.RideId, _options.GlobalTimeout);

            return await GiveUpAsync(ride.RideId, "global-timeout", cancellationToken);
        }

        // --- H3 coarse pre-filter (R-06, D-06) -------------------------------------------------
        var grid = new H3Grid(_options.H3Resolution, _options.H3RingK);
        var cells = grid.DiskAt(ride.Pickup);
        var raw = await index.PreFilterAsync(ride.VehicleType, cells, cancellationToken);

        // --- exact-distance post-filter, MANDATORY (D5' §3.1) ----------------------------------
        // The cell set above spans tens of kilometres; it decided which KEYS to read and nothing
        // else. ST_DWithin on dispatch.driver_presence is what decides who is actually near, and
        // the same query applies the five §3.2 gates that are predicates on rows it already holds.
        var nearby = await candidates.NarrowAsync(
            connection,
            new CandidateQuery(
                ride.RideId,
                ride.PassengerId,
                ride.Pickup,
                ride.VehicleType,
                _options.SearchRadiusM,
                _options.PositionFreshness),
            raw,
            cancellationToken);

        logger.LogInformation(
            "Ride {RideId}: {Cells} H3 res-{Resolution} cells → {Raw} indexed drivers → {Nearby} within {RadiusM} m",
            ride.RideId, cells.Count, _options.H3Resolution, raw.Count, nearby.Count, _options.SearchRadiusM);

        if (nearby.Count == 0)
        {
            return new DispatchOutcome(DispatchResult.NoCandidate, null, null, null, null, raw.Count, 0);
        }

        // --- the two gates that need another service, then the §3.3 score ----------------------
        var identities = nearby.Select(c => (c.DriverId, c.VehicleId, c.VehicleType)).ToList();

        var reputation = await reputationGate.EvaluateAsync(
            [.. nearby.Select(static c => c.DriverId)], cancellationToken);

        var wallet = await walletGate.EvaluateAsync(connection, identities, cancellationToken);

        var scored = scorer.Score(ride, nearby, reputation, wallet);
        var eligible = scored.Where(static c => c.Eligible).ToList();

        // R-11: the audit records everyone considered — including everyone excluded, and by which
        // gate. Committed before any offer goes out, so the record of the decision cannot be lost
        // by whatever happens to the offer.
        await using (var scoring = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            await candidates.RecordScoresAsync(
                scoring.Connection, scoring.Transaction, ride.RideId, scored, _options.AlgorithmVersion,
                cancellationToken);

            await scoring.CommitAsync(cancellationToken);
        }

        if (eligible.Count == 0)
        {
            logger.LogInformation(
                "Ride {RideId}: all {Nearby} nearby drivers were excluded by a hard gate ({Reasons})",
                ride.RideId, nearby.Count,
                string.Join(", ", scored.Select(static c => c.RejectedBy).Distinct()));

            return new DispatchOutcome(DispatchResult.NoCandidate, null, null, null, null, raw.Count, nearby.Count);
        }

        // R-12 Phase 1 is sequential: exactly one offer goes out. Walking down the ranked list is
        // not a second offer — it is finding the actual top-1 *reservable* driver, since a
        // candidate whose reservation loses to another ride was never available for this one.
        foreach (var candidate in eligible)
        {
            var outcome = await TryOfferAsync(ride, candidate, raw.Count, nearby.Count, eligible.Count, cancellationToken);

            if (outcome is not null)
            {
                return outcome;
            }
        }

        logger.LogInformation(
            "Ride {RideId}: every one of the {Eligible} eligible drivers was already reserved",
            ride.RideId, eligible.Count);

        return new DispatchOutcome(
            DispatchResult.NoCandidate, null, null, null, null, raw.Count, nearby.Count, eligible.Count);
    }

    /// <summary>
    /// The cascade is over with nobody found: end the ride in <c>ExpiredNoDriver</c> (US-6A.11).
    /// </summary>
    private async Task<DispatchOutcome> GiveUpAsync(Guid rideId, string why, CancellationToken cancellationToken)
    {
        // ride-svc is the sole writer of rides.state (R-01), so the ride ends through the contract's
        // own internal command rather than through an UPDATE here. `no_driver_found` resolves from
        // `Matching` alone (§11.12); a ride that has moved on is answered 400 or 409, which is
        // information and not a failure — something better than a timeout already happened to it.
        var cancelled = await rideService.SystemCancelAsync(rideId, NoDriverFound, cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await dispatchTimers.RetireForRideAsync(
            connection, null, rideId, DispatchTimerKinds.RideTimeout, cancellationToken);

        if (cancelled.Succeeded)
        {
            logger.LogInformation(
                "Ride {RideId} ended ExpiredNoDriver ({Why}); the passenger has been told nobody is coming",
                rideId, why);

            return Nothing(DispatchResult.ExpiredNoDriver);
        }

        logger.LogInformation(
            "Ride {RideId} could not be expired ({Why}): ride-svc answered {Status}/{ErrorCode} — it has moved on",
            rideId, why, (int)cancelled.Status, cancelled.ErrorCode);

        return Nothing(DispatchResult.RideNotDispatchable);
    }

    public async Task ExpireAsync(
        DueOfferTimer timer, bool driverUnreachable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var offer = await offers.FindAsync(connection, null, timer.OfferId, cancellationToken);

        if (offer is null || offer.Status != OfferStatuses.Offered)
        {
            // Already settled by the driver's answer. The timer has nothing left to watch.
            await timers.MarkFiredAsync(connection, null, timer.Id, cancellationToken);
            return;
        }

        var result = await rideService.ExpireOfferAsync(
            timer.RideId, timer.OfferId, driverUnreachable, cancellationToken);

        if (result.Status == System.Net.HttpStatusCode.Conflict)
        {
            // ride-svc says the window is still open: this node's clock ran ahead. Postgres decides
            // (`offer_expires_at <= now()` on ride-svc), so back off and let the sweep return.
            await timers.RescheduleAsync(
                connection, null, timer.Id, timeProvider.GetUtcNow().AddSeconds(1), cancellationToken);
            return;
        }

        await timers.MarkFiredAsync(connection, null, timer.Id, cancellationToken);
        await offers.TrySettleAsync(connection, null, timer.OfferId, OfferStatuses.Expired, cancellationToken);
        await index.ReleaseReservationAsync(timer.DriverId, timer.RideId, timer.OfferId, cancellationToken);

        if (result.Succeeded)
        {
            // ride-svc performed Offered → Matching and emitted offer.expired; the consumer picks
            // that up and runs the next round (ADD §11.11's R-04 paragraph).
            await ReturnToPoolAsync(timer.DriverId, cancellationToken);

            logger.LogInformation(
                "Offer {OfferId} on ride {RideId} expired unanswered; driver {DriverId} is back in the pool",
                timer.OfferId, timer.RideId, timer.DriverId);

            return;
        }

        // 410/404: the offer is no longer the ride's live one and ride-svc will not say why —
        // accepted, declined and re-offered all look the same from here. The row is settled so
        // ux_offers_driver_live stops blocking the driver, but presence is deliberately NOT
        // touched: the ride.accepted / offer.declined event knows which it was, and guessing
        // "available" would put a driver who is mid-ride back in the candidate pool.
        logger.LogInformation(
            "Backstop for offer {OfferId} found the ride already moved on ({Status}/{ErrorCode})",
            timer.OfferId, (int)result.Status, result.ErrorCode);
    }

    public async Task RunTimerAsync(DueDispatchTimer timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        switch (timer.Kind)
        {
            case DispatchTimerKinds.RideTimeout when timer.RideId is { } rideId:
                await RunGlobalTimeoutAsync(timer, rideId, cancellationToken);
                break;

            case DispatchTimerKinds.OfferReleaseGrace when timer.DriverId is { } driverId:
                await RunReleaseGraceAsync(timer, driverId, cancellationToken);
                break;

            default:
                // A kind this sweep claimed but cannot act on, or a row whose subject column is
                // null. Retired rather than left due, so it cannot spin the sweep forever.
                logger.LogWarning(
                    "dispatch.timers row {TimerId} of kind {Kind} has no usable subject; retiring it",
                    timer.Id, timer.Kind);

                await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
                {
                    await dispatchTimers.MarkFiredAsync(connection, null, timer.Id, cancellationToken);
                }

                break;
        }
    }

    public async Task ReleaseLiveOfferAsync(Guid rideId, string toStatus, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toStatus);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var live = await offers.FindLiveForRideAsync(connection, null, rideId, cancellationToken);

        if (live is null)
        {
            // Already settled — by the backstop, or by a redelivery of this same event. Both are
            // normal; at-least-once delivery is absorbed by making this a no-op rather than by a
            // dedupe table (D6' §2.3).
            return;
        }

        await offers.TrySettleAsync(connection, null, live.Id, toStatus, cancellationToken);
        await timers.CancelForOfferAsync(connection, null, live.Id, cancellationToken);
        await index.ReleaseReservationAsync(live.DriverId, rideId, live.Id, cancellationToken);
        await ReturnToPoolAsync(live.DriverId, cancellationToken);

        logger.LogInformation(
            "Offer {OfferId} on ride {RideId} settled {Status}; driver {DriverId} is back in the pool",
            live.Id, rideId, toStatus, live.DriverId);
    }

    public async Task<bool> ReleaseDriverOfferAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var live = await offers.FindLiveForDriverAsync(connection, null, driverId, cancellationToken);

        if (live is null || live.Status != OfferStatuses.Offered)
        {
            // Nothing offered, or the driver has ACCEPTED and is mid-ride — in which case the
            // last will is ride-svc's `offline_grace` and §11.12's business, not dispatch's. R-15
            // says "releases active offer"; an accepted ride is no longer an offer.
            return false;
        }

        // Reuse of the one expiry implementation, not a second one: ride-svc still has to perform
        // Offered → Matching and emit offer.expired, or the ride sits Offered to a driver who is
        // not there. The rides.timers row is claimed first so the R-04 sweep and this path cannot
        // both fire against the same offer.
        var claimed = await timers.TryClaimForOfferAsync(
            connection, null, live.Id, _options.TimerLease, cancellationToken);

        var timer = claimed ?? new DueOfferTimer(
            Guid.Empty, live.RideId, live.Id, live.DriverId, timeProvider.GetUtcNow());

        // The one caller allowed to revoke an offer inside its own 15 s window: the grace has
        // already established that this driver's broker session is dead, so there is no window left
        // to protect and the ride would otherwise wait out the rest of it on nobody.
        await ExpireAsync(timer, driverUnreachable: true, cancellationToken);

        logger.LogInformation(
            "Driver {DriverId}'s EMQX session stayed down past the {Grace} grace; offer {OfferId} on ride " +
            "{RideId} released (R-15)",
            driverId, _options.OfferReleaseGrace, live.Id, live.RideId);

        return true;
    }

    public async Task MarkAcceptedAsync(Guid rideId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var accepted = await offers.SettleDriversLiveOfferAsync(
            connection, null, driverId, OfferStatuses.Accepted, cancellationToken);

        if (accepted is not null)
        {
            await timers.CancelForOfferAsync(connection, null, accepted.Id, cancellationToken);
        }

        // The cascade is over: a driver is on their way, so the 120 s deadline must not fire behind
        // them and cancel a ride that has already been accepted.
        await dispatchTimers.RetireForRideAsync(
            connection, null, rideId, DispatchTimerKinds.RideTimeout, cancellationToken);

        // ADD §9.4: an accepted driver comes out of geo:drivers:available. The presence row moves
        // with them so the exact post-filter — which reads state, not Redis — agrees.
        await presence.TransitionAsync(
            connection, null, driverId, [PresenceStates.Offered, PresenceStates.Available], PresenceStates.OnRide,
            cancellationToken);

        await index.RemoveFromPoolAsync(driverId, PresenceStates.OnRide, cancellationToken);

        logger.LogInformation("Driver {DriverId} accepted ride {RideId}; out of the candidate pool", driverId, rideId);
    }

    public async Task ReturnToPoolAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // ADD §11.12's three duties on a terminal event, and all three have to happen or the driver
        // is "ghost-busy" — the failure the R-20 stuck-state alert catches.
        //
        // (a) The offer that started the ride stops counting against R-10's one-live-offer rule
        //     (migration 0712). Without it the driver's *first* accepted offer blocks
        //     ux_offers_driver_live for ever and they can never be offered a second ride.
        // (b) `lock:driver-offer:{driverId}` is dropped, rather than left to run its 15-second TTL
        //     out. The TTL means the bug self-heals in a quarter of a minute, which is exactly long
        //     enough to lose the driver the next ride and short enough that nobody notices why.
        // (c) The presence row and the GEO index put them back where they stand, below.
        if (await offers.ReleaseAcceptedAsync(connection, null, driverId, cancellationToken) is { } released)
        {
            await index.ReleaseReservationAsync(driverId, released.RideId, released.Id, cancellationToken);

            logger.LogDebug(
                "Driver {DriverId}'s offer {OfferId} on ride {RideId} is released; they can hold a new one",
                driverId, released.Id, released.RideId);
        }

        var row = await presence.TransitionAsync(
            connection, null, driverId, [PresenceStates.Offered, PresenceStates.OnRide], PresenceStates.Available,
            cancellationToken);

        // Nothing to do if they went offline in the meantime — TransitionAsync's `state = ANY(...)`
        // is what refuses to drag an OFFLINE driver back into the pool.
        if (row?.Geo is { } position)
        {
            await index.IndexAvailableAsync(
                driverId, row.VehicleId, row.VehicleType, position, cancellationToken);
        }
    }

    public async Task RetireRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await dispatchTimers.RetireForRideAsync(
            connection, null, rideId, DispatchTimerKinds.RideTimeout, cancellationToken);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>US-6A.11's deadline arrived.</summary>
    private async Task RunGlobalTimeoutAsync(
        DueDispatchTimer timer, Guid rideId, CancellationToken cancellationToken)
    {
        OfferRow? live;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            live = await offers.FindLiveForRideAsync(connection, null, rideId, cancellationToken);
        }

        if (live is { Status: OfferStatuses.Offered })
        {
            // A driver is looking at the offer right now. Killing the ride under them would waste
            // the one candidate the cascade actually found, and §11.12 has no `Offered → Expired`
            // cell anyway — the transition is legal from `Matching` alone. Let the 15 s window
            // finish; whichever way it settles, the next round comes back here immediately.
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);

            await dispatchTimers.RescheduleAsync(
                connection, null, timer.Id, live.ExpiresAt.Add(BackstopGrace), cancellationToken);

            logger.LogInformation(
                "Ride {RideId} reached its cascade deadline with offer {OfferId} still live; waiting for the " +
                "offer to settle at {ExpiresAt:O}",
                rideId, live.Id, live.ExpiresAt);

            return;
        }

        await GiveUpAsync(rideId, "global-timeout", cancellationToken);
    }

    /// <summary>R-15's grace ran out.</summary>
    private async Task RunReleaseGraceAsync(
        DueDispatchTimer timer, Guid driverId, CancellationToken cancellationToken)
    {
        await ReleaseDriverOfferAsync(driverId, cancellationToken);

        // Presence follows the session. A driver whose broker connection has been dead for the
        // whole grace cannot be sent an offer they could answer, so they leave the pool the same
        // way `POST /v1/standby/offline` would take them out of it — and their app puts them back
        // by going online again, which is the one signal that means "I am here".
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            await presence.GoOfflineAsync(connection, null, driverId, cancellationToken);
            await dispatchTimers.MarkFiredAsync(connection, null, timer.Id, cancellationToken);
        }

        await index.ForgetAsync(driverId, cancellationToken);
    }

    /// <summary>
    /// One offer attempt. Returns <see langword="null"/> when this candidate could not be reserved
    /// and the caller should try the next one.
    /// </summary>
    private async Task<DispatchOutcome?> TryOfferAsync(
        RideDispatchRequest ride,
        ScoredCandidate candidate,
        int preFilterCount,
        int candidateCount,
        int eligibleCount,
        CancellationToken cancellationToken)
    {
        var offerId = Guid.NewGuid();
        var ttl = _options.OfferTtl;

        // (1) Redis fast path (D5' §3.6). Losing here means another ride reserved this driver a
        //     moment ago; the authoritative check below would have caught it too, one round trip
        //     later and after a wasted INSERT.
        if (!await index.TryReserveAsync(candidate.DriverId, ride.RideId, offerId, ttl, cancellationToken))
        {
            logger.LogDebug("Driver {DriverId} is already reserved; trying the next candidate", candidate.DriverId);
            return null;
        }

        var provisionalExpiry = timeProvider.GetUtcNow().Add(ttl);

        // (2) Authoritative reservation + the durable backstop, in one transaction. The backstop is
        //     armed BEFORE ride-svc is called, deliberately: if this process dies between the two,
        //     the sweep finds a timer for an offer the ride never got, calls expire, is answered
        //     410, settles the row and frees the driver. The other order — call ride-svc first —
        //     would leave a ride Offered with nothing watching the deadline.
        Guid timerId;
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var inserted = await offers.TryInsertAsync(
                unitOfWork.Connection, unitOfWork.Transaction, offerId, ride.RideId, candidate.DriverId,
                provisionalExpiry, cancellationToken);

            if (!inserted)
            {
                // R-10's Postgres half rejected it — the driver holds a live offer that Redis did
                // not know about (a flush, a failover, a lock that expired early). This is exactly
                // the case ADD §11.11 says neither mechanism alone covers.
                await unitOfWork.RollbackAsync(cancellationToken);
                await index.ReleaseReservationAsync(candidate.DriverId, ride.RideId, offerId, cancellationToken);

                logger.LogInformation(
                    "ux_offers_driver_live refused an offer to driver {DriverId}; Redis and Postgres disagreed",
                    candidate.DriverId);

                return null;
            }

            timerId = await timers.ArmAsync(
                unitOfWork.Connection, unitOfWork.Transaction, ride.RideId, offerId, candidate.DriverId,
                provisionalExpiry.Add(BackstopGrace), cancellationToken);

            await presence.TransitionAsync(
                unitOfWork.Connection, unitOfWork.Transaction, candidate.DriverId,
                [PresenceStates.Available], PresenceStates.Offered, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await index.RemoveFromPoolAsync(candidate.DriverId, PresenceStates.Offered, cancellationToken);

        // (3) Matching → Offered. ride-svc is the sole writer of rides.state.
        var placed = await rideService.PlaceOfferAsync(
            ride.RideId, offerId, candidate.DriverId, candidate.VehicleId, (int)ttl.TotalSeconds, cancellationToken);

        if (!placed.Succeeded)
        {
            await UnwindAsync(ride, candidate, offerId, timerId, cancellationToken);

            // The ride itself would not move — accepted, cancelled, or offered by another replica.
            // Walking to the next candidate would just be refused again.
            return new DispatchOutcome(
                DispatchResult.RideNotDispatchable, null, null, null, null, preFilterCount, candidateCount,
                eligibleCount);
        }

        var expiresAt = placed.OfferExpiresAt ?? provisionalExpiry;

        // (4) Align everything to ride-svc's deadline and commit the event. R-13: the driver's
        //     push is produced from this row, which cannot exist before the commit.
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            await offers.SetExpiryAsync(unitOfWork.Connection, unitOfWork.Transaction, offerId, expiresAt, cancellationToken);

            await timers.RescheduleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, timerId, expiresAt.Add(BackstopGrace), cancellationToken);

            await outbox.WriteAsync(
                unitOfWork,
                DispatchEvents.OfferCreated(
                    ride.RideId,
                    offerId,
                    candidate.DriverId,
                    candidate.VehicleId,
                    placed.Version ?? 0,
                    timeProvider.GetUtcNow(),
                    expiresAt,
                    ride.FareEstimateMinor,
                    ride.Currency,
                    ride.PaymentMethod,
                    candidate.DistanceM,
                    ride.Kind,
                    ride.PackageSize),
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await index.RefreshOfferDeadlineAsync(ride.RideId, expiresAt, cancellationToken);

        logger.LogInformation(
            "Ride {RideId} offered to driver {DriverId} ({DistanceM:0} m away, score {Score:0.0000}) until {ExpiresAt:O}",
            ride.RideId, candidate.DriverId, candidate.DistanceM, candidate.Score, expiresAt);

        return new DispatchOutcome(
            DispatchResult.Offered, offerId, candidate.DriverId, expiresAt, placed.Version,
            preFilterCount, candidateCount, eligibleCount);
    }

    /// <summary>
    /// Puts everything back after ride-svc refused the offer. Runs in the reverse order of the
    /// arming, so the driver is not visible as available while an OFFERED row still names them.
    /// </summary>
    private async Task UnwindAsync(
        RideDispatchRequest ride,
        ScoredCandidate candidate,
        Guid offerId,
        Guid timerId,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        await offers.TrySettleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, offerId, OfferStatuses.Expired, cancellationToken);

        await timers.MarkFiredAsync(unitOfWork.Connection, unitOfWork.Transaction, timerId, cancellationToken);

        await presence.TransitionAsync(
            unitOfWork.Connection, unitOfWork.Transaction, candidate.DriverId,
            [PresenceStates.Offered], PresenceStates.Available, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        await index.ReleaseReservationAsync(candidate.DriverId, ride.RideId, offerId, cancellationToken);
        await index.IndexAvailableAsync(
            candidate.DriverId, candidate.VehicleId, candidate.VehicleType, candidate.Candidate.Geo, cancellationToken);
    }

    private static DispatchOutcome Nothing(DispatchResult result) => new(result, null, null, null, null, 0, 0);
}
