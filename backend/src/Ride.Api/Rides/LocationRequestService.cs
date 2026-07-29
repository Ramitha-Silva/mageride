using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>The body of <c>POST /v1/location-requests</c>, before validation.</summary>
/// <param name="RideDraftId">
/// <b>Accepted and dropped.</b> <c>rides.location_requests.ride_id</c> is a foreign key onto
/// <c>rides.rides</c> and a draft is precisely a ride that has not been booked — there is no row to
/// point at, and the whole reason the round-trip exists is that the booker does not yet know the
/// pickup. The booker's client correlates the answer through <c>requestId</c>, which is also the
/// WebSocket group it is already listening on (P-13). Recorded as a contract gap in the C037 handoff.
/// </param>
public sealed record IssueLocationRequestCommand(Guid BookerId, string? RiderPhone, string? RideDraftId);

/// <summary>
/// The P-02 / P-13 booker→rider GPS round-trip (ADD §11.15).
/// </summary>
/// <remarks>
/// <para>
/// <b>The privacy posture is the design.</b> A booker learns a rider's position only because the
/// rider chose to send it, once, into a request the rider can see the sender of. Declining transmits
/// nothing — not a coarse position, not a cell, not a "near you" — and the statement that performs a
/// decline has no column for one. Every outcome, including the declines and the numbers that reached
/// nobody, lands in <c>safety.location_request_audit</c>, because "this booker keeps pinging
/// somebody who keeps refusing" is the abuse pattern P-12 exists to surface.
/// </para>
/// <para>
/// <b>The rate limit is Postgres, not Redis.</b> D5' §10 names a Redis token bucket; ride-svc holds
/// no Redis connection at all (<c>UseRedis = false</c>, and R-04's "the backstop fires independently
/// of any Redis TTL" is structural here because of it). The durable rows <em>are</em> the counter —
/// <c>ix_location_requests_booker</c> exists in migration 0606 for exactly this query, and its
/// header says so — and counting them inside the transaction that inserts the next one means a
/// request that rolled back never spent a token, which a bucket decremented before the write would
/// have. Recorded in the C037 handoff.
/// </para>
/// </remarks>
public interface ILocationRequestService
{
    Task<LocationRequestRow> IssueAsync(IssueLocationRequestCommand command, CancellationToken cancellationToken);

    /// <summary>The diagnostic read. Only the booker and the named rider may make it.</summary>
    Task<LocationRequestRow> GetAsync(Guid callerId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// The rider shares their position. <paramref name="riderId"/> is the authenticated rider on the
    /// in-app path and <see langword="null"/> on AL-45's web path, where public-bff has already
    /// burned the <c>pickup_confirm</c> token that stands in for one.
    /// </summary>
    Task<LocationRequestRow> ConfirmAsync(
        Guid? riderId, Guid requestId, GeoPoint geo, double? accuracyM, CancellationToken cancellationToken);

    /// <summary>The rider refuses. No coordinates are accepted, and none are stored (P-02).</summary>
    Task<LocationRequestRow> DeclineAsync(Guid? riderId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the requests whose 300 s window has closed — the durable backstop behind the
    /// booker's "Rider didn't respond" prompt (ADD §11.15's expiry path).
    /// </summary>
    /// <returns>How many were expired.</returns>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILocationRequestService"/>
public sealed class LocationRequestService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connectionFactory,
    ILocationRequestRepository requests,
    ILocationRequestAuditRepository audit,
    IRiderDirectory riders,
    RiderPhoneHasher phones,
    IOutboxWriter outbox,
    IOptions<RideOptions> options,
    TimeProvider timeProvider,
    ILogger<LocationRequestService> logger) : ILocationRequestService
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<LocationRequestRow> IssueAsync(
        IssueLocationRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!RiderPhone.TryNormalise(command.RiderPhone, out var phone))
        {
            throw new MageRideException(
                MageRideErrors.InvalidPhone,
                "riderPhone must be a Sri Lankan mobile in E.164 (+947XXXXXXXX).");
        }

        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // P-12, before the lookup rather than after it. The lookup is a registration oracle: a
        // booker who has run out of requests must not be able to keep asking iam-svc whether a
        // stranger's number is on the platform, which checking afterwards would let them do for
        // free.
        await RequireWithinRateLimitAsync(unitOfWork, command.BookerId, now, cancellationToken);

        var rider = await riders.LookupAsync(phone, cancellationToken);
        var hash = phones.Hash(phone);

        var state = rider.Registered ? LocationRequestStates.Pending : LocationRequestStates.RiderNotRegistered;

        var request = await requests.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            new NewLocationRequest(
                RequestId: Guid.NewGuid(),
                BookerId: command.BookerId,
                RiderId: rider.UserId,
                RiderPhoneHash: hash,
                State: state,
                TtlSeconds: (int)_options.LocationRequestTtl.TotalSeconds,
                RideId: null),
            cancellationToken);

        // An unregistered rider is an outcome on arrival, so it is audited on arrival. A pending
        // one is audited when it resolves — there is nothing to record about a question still being
        // asked, and the P-12 count above reads the request rows themselves.
        if (LocationRequestDecisions.For(state) is { } decision)
        {
            await audit.RecordAsync(
                unitOfWork.Connection, unitOfWork.Transaction,
                command.BookerId, hash, request.RequestId, decision, cancellationToken);
        }

        // One event for both branches, carrying the state notification-svc branches on: `Pending`
        // is the FCM data message to a registered rider's app, `RiderNotRegistered` is AL-45's
        // `pickup_confirm` token minted and SMSed to the number below.
        await outbox.WriteAsync(
            unitOfWork,
            [
                RideEvents.BuildLocationRequest(
                    RideEventTypes.LocationRequestIssued, Payload(request, phone), Guid.NewGuid(), now),
            ],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Booker {BookerId} issued location request {RequestId} ({State}), expiring {ExpiresAt:O}",
            command.BookerId, request.RequestId, request.State, request.ExpiresAt);

        return request;
    }

    public async Task<LocationRequestRow> GetAsync(Guid callerId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var request = await requests.FindAsync(connection, null, requestId, cancellationToken)
                      ?? throw NotFound(requestId);

        // The booker and the rider, and nobody else. A request names a third party's position; a
        // reader who is neither party has no business knowing one was even asked for.
        if (request.BookerId != callerId && request.RiderId != callerId)
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "This location request belongs to another booker and rider.");
        }

        return request;
    }

    public async Task<LocationRequestRow> ConfirmAsync(
        Guid? riderId, Guid requestId, GeoPoint geo, double? accuracyM, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var confirmed = await requests.ConfirmAsync(
            unitOfWork.Connection, unitOfWork.Transaction, requestId, riderId, geo, accuracyM, cancellationToken);

        if (confirmed is null)
        {
            throw await DiagnoseAsync(unitOfWork, requestId, riderId, cancellationToken);
        }

        var events = new List<OutboxRecord>
        {
            RideEvents.BuildLocationRequest(
                RideEventTypes.LocationRequestConfirmed, Payload(confirmed, riderPhone: null), Guid.NewGuid(), now),
        };

        await RecordAsync(unitOfWork, confirmed, LocationRequestDecisions.Confirmed, cancellationToken);

        // ADD §11.15: "only the *first* confirmation is honoured per booker session (subsequent
        // confirmations transition to Expired)". A booker holding several requests open has one
        // pickup pin, so a second answer arriving later would silently move a ride they have
        // already started booking. The others are closed here, and their bookers' sockets are told
        // — a rider who taps Share and is told nothing happened is worse than one told the request
        // has closed.
        foreach (var superseded in await requests.ExpireOthersAsync(
                     unitOfWork.Connection, unitOfWork.Transaction, confirmed.BookerId, requestId, cancellationToken))
        {
            await RecordAsync(unitOfWork, superseded, LocationRequestDecisions.Expired, cancellationToken);

            events.Add(RideEvents.BuildLocationRequest(
                RideEventTypes.LocationRequestExpired, Payload(superseded, riderPhone: null), Guid.NewGuid(), now));
        }

        await outbox.WriteAsync(unitOfWork, events, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Location request {RequestId} confirmed; booker {BookerId} has a pickup pin", requestId, confirmed.BookerId);

        return confirmed;
    }

    public async Task<LocationRequestRow> DeclineAsync(
        Guid? riderId, Guid requestId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var declined = await requests.DeclineAsync(
            unitOfWork.Connection, unitOfWork.Transaction, requestId, riderId, cancellationToken);

        if (declined is null)
        {
            throw await DiagnoseAsync(unitOfWork, requestId, riderId, cancellationToken);
        }

        await RecordAsync(unitOfWork, declined, LocationRequestDecisions.Declined, cancellationToken);

        // The payload builder is handed `declined`, whose `resolved_geo` is NULL because the
        // statement that produced it has no column for one. There is no branch here that could
        // attach a position to a decline (P-02).
        await outbox.WriteAsync(
            unitOfWork,
            [
                RideEvents.BuildLocationRequest(
                    RideEventTypes.LocationRequestDeclined, Payload(declined, riderPhone: null), Guid.NewGuid(), now),
            ],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Location request {RequestId} declined; no position was transmitted (P-02)", requestId);

        return declined;
    }

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var expired = await requests.ClaimExpiredAsync(
            unitOfWork.Connection, unitOfWork.Transaction, _options.LocationRequestBatchSize, cancellationToken);

        if (expired.Count == 0)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return 0;
        }

        var events = new List<OutboxRecord>(expired.Count);

        foreach (var request in expired)
        {
            await RecordAsync(unitOfWork, request, LocationRequestDecisions.Expired, cancellationToken);

            events.Add(RideEvents.BuildLocationRequest(
                RideEventTypes.LocationRequestExpired, Payload(request, riderPhone: null), Guid.NewGuid(), now));
        }

        await outbox.WriteAsync(unitOfWork, events, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "{Count} location request(s) expired unanswered; their bookers fall back to a map pin (US-8.19)",
            expired.Count);

        return expired.Count;
    }

    // -------------------------------------------------------------------------------------------

    private async Task RequireWithinRateLimitAsync(
        IUnitOfWork unitOfWork, Guid bookerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hourly = await requests.CountIssuedSinceAsync(
            unitOfWork.Connection, unitOfWork.Transaction, bookerId, now.AddHours(-1), cancellationToken);

        if (hourly >= _options.LocationRequestsPerHour)
        {
            await RefuseAsync(unitOfWork, _options.LocationRequestsPerHour, "hour", cancellationToken);
        }

        var daily = await requests.CountIssuedSinceAsync(
            unitOfWork.Connection, unitOfWork.Transaction, bookerId, now.AddDays(-1), cancellationToken);

        if (daily >= _options.LocationRequestsPerDay)
        {
            await RefuseAsync(unitOfWork, _options.LocationRequestsPerDay, "day", cancellationToken);
        }
    }

    private static async Task RefuseAsync(
        IUnitOfWork unitOfWork, int limit, string window, CancellationToken cancellationToken)
    {
        await unitOfWork.RollbackAsync(cancellationToken);

        throw new MageRideException(
            MageRideErrors.LocRequestRateLimited,
            $"A booker may ask for a rider's location {limit} times per {window} (P-12).");
    }

    private Task RecordAsync(
        IUnitOfWork unitOfWork, LocationRequestRow request, string decision, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            request.BookerId,

            // NOT NULL in the audit table. Every request this service creates carries a digest, so
            // the fallback is unreachable — and an empty digest is still a truthful "we do not know
            // who", where a skipped row would be a missing outcome.
            request.RiderPhoneHash ?? [],
            request.RequestId,
            decision,
            cancellationToken);

    private static LocationRequestPayload Payload(LocationRequestRow request, string? riderPhone) =>
        new(
            RequestId: request.RequestId,
            BookerId: request.BookerId,
            RiderId: request.RiderId,
            RiderPhone: riderPhone,
            State: request.State,
            IssuedAt: request.IssuedAt,
            ExpiresAt: request.ExpiresAt,
            Geo: request.ResolvedGeo is { } geo ? new EventGeoPoint(geo.Latitude, geo.Longitude) : null,
            AccuracyM: request.ResolvedAccuracyM is { } accuracy ? (double)accuracy : null);

    /// <summary>
    /// Why a guarded resolution matched nothing. The order matters: "there is no such request" and
    /// "it is not yours" have to be answered before "it has closed", or a caller who is not a party
    /// to a request could learn from the status code that one exists.
    /// </summary>
    private async Task<MageRideException> DiagnoseAsync(
        IUnitOfWork unitOfWork, Guid requestId, Guid? riderId, CancellationToken cancellationToken)
    {
        var current = await requests.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, requestId, cancellationToken);

        await unitOfWork.RollbackAsync(cancellationToken);

        if (current is null)
        {
            return NotFound(requestId);
        }

        if (riderId is { } caller && current.RiderId != caller)
        {
            return new MageRideException(
                MageRideErrors.Forbidden, "This location request was sent to somebody else.");
        }

        if (!current.IsLive)
        {
            // 410, not 409: the request was well formed and would have worked a moment ago, which
            // is what Gone means. `token-expired-or-revoked` is the code both this route and
            // AL-45's public twin declare, so the two paths answer a late tap identically.
            return new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked,
                $"This location request is already {current.State}.");
        }

        // Still live, so the only guard left is the TTL: the row is inside its window by its own
        // timestamps and outside it by Postgres's clock, which is the boundary being enforced.
        return new MageRideException(
            MageRideErrors.TokenExpiredOrRevoked,
            $"This location request closed at {current.ExpiresAt:O}.");
    }

    private static MageRideException NotFound(Guid requestId) =>
        new(MageRideErrors.NotFound, $"No location request {requestId}.");
}
