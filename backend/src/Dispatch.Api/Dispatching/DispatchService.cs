using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Redis;
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
public sealed record RideDispatchRequest(
    Guid RideId,
    GeoPoint Pickup,
    string VehicleType,
    string PaymentMethod,
    long? FareEstimateMinor,
    string Currency);

/// <summary>How a dispatch round ended.</summary>
public enum DispatchResult
{
    /// <summary>An offer is live and <c>offer.created</c> is committed.</summary>
    Offered,

    /// <summary>Nobody eligible was near enough. The ride stays in Matching.</summary>
    NoCandidate,

    /// <summary>ride-svc would not move the ride — accepted, cancelled, or already offered.</summary>
    RideNotDispatchable,

    /// <summary>The cascade bound was reached (<c>Dispatch:MaxOfferRounds</c>).</summary>
    RoundsExhausted,
}

/// <param name="PreFilterCount">Drivers the H3 ring returned, before any distance was applied.</param>
/// <param name="CandidateCount">Drivers that survived the exact <c>ST_DWithin</c> post-filter.</param>
public sealed record DispatchOutcome(
    DispatchResult Result,
    Guid? OfferId,
    Guid? DriverId,
    DateTimeOffset? ExpiresAt,
    long? Version,
    int PreFilterCount,
    int CandidateCount);

/// <summary>
/// The Mode C offer loop: candidate build, reservation, offer, cascade (D5' §3, ADD §11.11).
/// </summary>
public interface IDispatchService
{
    /// <summary>Runs one round: pick the nearest eligible driver and offer them the ride.</summary>
    Task<DispatchOutcome> DispatchAsync(RideDispatchRequest ride, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Requested → Matching</c> then a first round. What <c>ride.requested</c> triggers.
    /// </summary>
    Task<DispatchOutcome> BeginAsync(RideDispatchRequest ride, CancellationToken cancellationToken);

    /// <summary>Fires the R-04 backstop for one due timer.</summary>
    Task ExpireAsync(DueOfferTimer timer, CancellationToken cancellationToken);

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

    /// <summary>The driver won the ride: the offer is ACCEPTED and they leave the pool.</summary>
    Task MarkAcceptedAsync(Guid rideId, Guid driverId, CancellationToken cancellationToken);

    /// <summary>The ride is over: the driver goes back into the pool where they stand.</summary>
    Task ReturnToPoolAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDispatchService"/>
public sealed class DispatchService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IPresenceRepository presence,
    ICandidateRepository candidates,
    IOfferRepository offers,
    IOfferTimerRepository timers,
    IDriverIndex index,
    IRideServiceClient rideService,
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
        if (round >= _options.MaxOfferRounds)
        {
            // D5' §3.5 ends the cascade at `ExpiredNoDriver` after 120 s (US-6A.11). That is a ride
            // *state*, and no route exists to write it — C032's system-cancel and C034's global
            // timeout own it. Stopping here leaves the ride in Matching with no offer, which is
            // honest: a passenger sees "searching", not a driver who is not coming.
            logger.LogWarning(
                "Ride {RideId} has been through {Rounds} offers; stopping. The US-6A.11 ExpiredNoDriver " +
                "timeout is C034 — the ride stays in Matching until then",
                ride.RideId, round);

            return Nothing(DispatchResult.RoundsExhausted);
        }

        // --- H3 coarse pre-filter (R-06, D-06) -------------------------------------------------
        var grid = new H3Grid(_options.H3Resolution, _options.H3RingK);
        var cells = grid.DiskAt(ride.Pickup);
        var raw = await index.PreFilterAsync(ride.VehicleType, cells, cancellationToken);

        // --- exact-distance post-filter, MANDATORY (D5' §3.1) ----------------------------------
        // The cell set above spans tens of kilometres; it decided which KEYS to read and nothing
        // else. ST_DWithin on dispatch.driver_presence is what decides who is actually near.
        var ranked = await candidates.NarrowAsync(
            connection,
            ride.RideId,
            raw,
            ride.Pickup,
            ride.VehicleType,
            _options.SearchRadiusM,
            _options.PresenceTtl,
            cancellationToken);

        logger.LogInformation(
            "Ride {RideId}: {Cells} H3 res-{Resolution} cells → {Raw} indexed drivers → {Ranked} within {RadiusM} m",
            ride.RideId, cells.Count, _options.H3Resolution, raw.Count, ranked.Count, _options.SearchRadiusM);

        if (ranked.Count == 0)
        {
            return new DispatchOutcome(DispatchResult.NoCandidate, null, null, null, null, raw.Count, 0);
        }

        // R-11: the audit records everyone considered, not just the winner.
        await using (var scoring = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            await candidates.RecordScoresAsync(
                scoring.Connection, scoring.Transaction, ride.RideId, ranked, _options.AlgorithmVersion, cancellationToken);
            await scoring.CommitAsync(cancellationToken);
        }

        // R-12 Phase 1 is sequential: exactly one offer goes out. Walking down the ranked list is
        // not a second offer — it is finding the actual top-1 *eligible* driver, since a candidate
        // whose reservation loses to another ride was never eligible for this one.
        foreach (var candidate in ranked)
        {
            var outcome = await TryOfferAsync(ride, candidate, raw.Count, ranked.Count, cancellationToken);

            if (outcome is not null)
            {
                return outcome;
            }
        }

        logger.LogInformation(
            "Ride {RideId}: every one of the {Ranked} nearby drivers was already reserved", ride.RideId, ranked.Count);

        return new DispatchOutcome(DispatchResult.NoCandidate, null, null, null, null, raw.Count, ranked.Count);
    }

    public async Task ExpireAsync(DueOfferTimer timer, CancellationToken cancellationToken)
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

        var result = await rideService.ExpireOfferAsync(timer.RideId, timer.OfferId, cancellationToken);

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

    public async Task MarkAcceptedAsync(Guid rideId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var accepted = await offers.SettleDriversLiveOfferAsync(
            connection, null, driverId, OfferStatuses.Accepted, cancellationToken);

        if (accepted is not null)
        {
            await timers.CancelForOfferAsync(connection, null, accepted.Id, cancellationToken);
        }

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

    /// <summary>
    /// One offer attempt. Returns <see langword="null"/> when this candidate could not be reserved
    /// and the caller should try the next one.
    /// </summary>
    private async Task<DispatchOutcome?> TryOfferAsync(
        RideDispatchRequest ride,
        Candidate candidate,
        int preFilterCount,
        int candidateCount,
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
                DispatchResult.RideNotDispatchable, null, null, null, null, preFilterCount, candidateCount);
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
                    candidate.DistanceM),
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        await index.RefreshOfferDeadlineAsync(ride.RideId, expiresAt, cancellationToken);

        logger.LogInformation(
            "Ride {RideId} offered to driver {DriverId} ({DistanceM:0} m away) until {ExpiresAt:O}",
            ride.RideId, candidate.DriverId, candidate.DistanceM, expiresAt);

        return new DispatchOutcome(
            DispatchResult.Offered, offerId, candidate.DriverId, expiresAt, placed.Version,
            preFilterCount, candidateCount);
    }

    /// <summary>
    /// Puts everything back after ride-svc refused the offer. Runs in the reverse order of the
    /// arming, so the driver is not visible as available while an OFFERED row still names them.
    /// </summary>
    private async Task UnwindAsync(
        RideDispatchRequest ride, Candidate candidate, Guid offerId, Guid timerId, CancellationToken cancellationToken)
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
            candidate.DriverId, candidate.VehicleId, candidate.VehicleType, candidate.Geo, cancellationToken);
    }

    private static DispatchOutcome Nothing(DispatchResult result) => new(result, null, null, null, null, 0, 0);
}
