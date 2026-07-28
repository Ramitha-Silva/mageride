using Dapper;
using MageRide.Ride.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>What <see cref="IRideRepository.CreateAsync"/> found when it tried to book.</summary>
public enum RideCreateOutcome
{
    /// <summary>A new ride was inserted.</summary>
    Created,

    /// <summary>
    /// <c>ux_rides_idem</c> already held <c>(passengerId, clientRequestId)</c> — R-18's retry.
    /// The returned ride is the existing one and no second booking happened.
    /// </summary>
    AlreadyRequested,

    /// <summary>
    /// <c>ux_rides_open_passenger</c> rejected it: this passenger already has a different
    /// non-terminal ride (<c>409 active-ride-exists</c>).
    /// </summary>
    ActiveRideExists,
}

/// <param name="Outcome">Which of the three the insert hit.</param>
/// <param name="Ride">The ride, unless the passenger already had a different open one.</param>
public sealed record RideCreateResult(RideCreateOutcome Outcome, RideRow? Ride);

/// <summary>
/// <c>rides.rides</c> (server_db_schema.md §5, D4' §5; migrations 0601 + 0608) — ride-svc is its
/// sole writer (R-01, D5' §6).
/// </summary>
/// <remarks>
/// Every state move is a single conditional <c>UPDATE … RETURNING</c> guarded on the state it
/// expects and on <c>version</c>. There is no read-then-write anywhere in this file: a read
/// followed by a write is exactly the race ADD §11.11 exists to close, and a repository that
/// offers one would eventually be used on the accept path.
/// </remarks>
public interface IRideRepository
{
    /// <summary>
    /// Books a ride, or reports why it could not. Idempotent on <c>(passengerId, clientRequestId)</c>
    /// — R-18's second key, independent of the <c>Idempotency-Key</c> header.
    /// </summary>
    Task<RideCreateResult> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewRide ride,
        CancellationToken cancellationToken);

    Task<RideRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken);

    /// <summary>The passenger's current non-terminal ride, for client recovery (R-18).</summary>
    Task<RideRow?> FindActiveByPassengerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid passengerId, CancellationToken cancellationToken);

    /// <summary>The driver's current non-terminal ride, for driver-side resume.</summary>
    Task<RideRow?> FindActiveByDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary><c>Requested → Matching</c>: dispatch-svc has begun the candidate build (D5' §6).</summary>
    Task<RideRow?> MarkMatchingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        long? expectedVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>Matching → Offered</c>: dispatch-svc reserved a driver and is about to push the offer
    /// (ADD §11.11). <paramref name="expiresAt"/> is the authoritative 15 s TTL — the Redis key is
    /// only the fast path (R-04).
    /// </summary>
    Task<RideRow?> PlaceOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        Guid vehicleId,
        DateTimeOffset expiresAt,
        long? expectedVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// The ADD §11.11 atomic single-winner accept. Returns the updated row for the one caller whose
    /// row count was 1, and <see langword="null"/> for everybody else.
    /// </summary>
    Task<RideRow?> AcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid driverId,
        Guid offerId,
        long expectedVersion,
        CancellationToken cancellationToken);

    /// <summary><c>Offered → Matching</c>: the offer is released and the ride re-enters the pool.</summary>
    Task<RideRow?> DeclineOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>Offered → Matching</c> because the 15 s window closed unanswered (R-04). Bound to
    /// <c>offer_expires_at &lt;= now()</c>, evaluated by Postgres — the caller's clock never
    /// decides an expiry.
    /// </summary>
    Task<RideRow?> ExpireOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A move along the happy path, guarded on the states it is legal from, on <c>version</c> and
    /// — when <paramref name="requiredDriverId"/> is given — on the ride already belonging to that
    /// driver.
    /// </summary>
    Task<RideRow?> AdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyCollection<string> fromStates,
        string toState,
        long? expectedVersion,
        Guid? requiredDriverId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A move to a terminal state (§11.12): stamps <c>terminal_at</c> and drops the live offer, so
    /// a finished ride carries neither a countdown nor something to accept.
    /// </summary>
    Task<RideRow?> TerminateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string fromState,
        string toState,
        long? expectedVersion,
        CancellationToken cancellationToken);

    /// <summary>The driver's live ride on a given vehicle, for the R-15 last-will path.</summary>
    /// <remarks>
    /// Keyed on <c>accepted_vehicle_id</c> rather than the driver: the last will names a vehicle,
    /// and a driver may own several. <c>ux_rides_driver_busy</c> makes at most one ride per driver
    /// busy at a time, so at most one row can come back.
    /// </remarks>
    Task<RideRow?> FindBusyByVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>
    /// How many post-acceptance rider cancellations this passenger has run up since their last
    /// completed ride (AL-16, US-6A.10b).
    /// </summary>
    Task<int> CountConsecutiveRiderCancellationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid passengerId, CancellationToken cancellationToken);

    /// <summary>
    /// How many rides have sat in <paramref name="state"/> longer than <paramref name="age"/> —
    /// one row of ADD §13.3.1's stuck-state table (R-20).
    /// </summary>
    Task<int> CountStuckAsync(
        NpgsqlConnection connection, string state, TimeSpan age, CancellationToken cancellationToken);

    /// <summary>Outbox rows for this ride that the dispatcher has not published yet (saga diagnostics).</summary>
    Task<int> CountPendingOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken);
}

/// <summary>The fields <c>POST /v1/rides/request</c> writes.</summary>
public sealed record NewRide(
    Guid PassengerId,
    Guid ClientRequestId,
    string VehicleType,
    GeoPoint Pickup,
    GeoPoint Dropoff,
    string PaymentMethod,
    long FareEstimateMinor,
    long FareSurchargeMinor);

/// <inheritdoc cref="IRideRepository"/>
public sealed class RideRepository : IRideRepository
{
    /// <summary>Unique-violation. Postgres reports every unique index breach as 23505.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The partial unique index behind invariant 1 (ADD Appendix B.2).</summary>
    private const string OpenPassengerIndex = "ux_rides_open_passenger";

    private const string Columns =
        "id, passenger_id, client_request_id, booker_id, rider_id, rider_name, is_proxy, kind, " +
        "vehicle_type, pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id, " +
        "offered_driver_id, offered_vehicle_id, current_offer_id, offer_expires_at, payment_method, " +
        "fare_estimate_minor, fare_surcharge_minor, currency, version, created_at, updated_at, terminal_at";

    /// <summary>The ten states a ride never leaves (D5' §6); anything else is live.</summary>
    private static readonly string[] TerminalStates = [.. RideStates.Terminal];

    /// <summary>The four states <c>ux_rides_driver_busy</c> holds a driver in (O2, R-10).</summary>
    private static readonly string[] DriverBusyStates = [.. RideStates.DriverBusy];

    /// <summary>
    /// The only outcomes AL-16's consecutive counter reads: a post-acceptance rider cancel, and a
    /// ride that got far enough to be "successfully completed" — which is Completed and everything
    /// downstream of it, because a ride that reached PaymentPending was driven and delivered.
    /// </summary>
    private static readonly string[] CountedOutcomeStates =
    [
        RideStates.CancelledByRiderAfterAccept,
        RideStates.Completed,
        RideStates.PaymentPending,
        RideStates.Paid,
        RideStates.CashSettled,
        RideStates.CashOnDeliveryCollected,
    ];

    public async Task<RideCreateResult> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NewRide ride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ride);

        try
        {
            // ON CONFLICT names ux_rides_idem specifically, so R-18's retry is absorbed here while
            // ux_rides_open_passenger still raises — the two mean opposite things to the caller and
            // an untargeted DO NOTHING would turn "you already have a ride running" into a silent
            // success that returns nothing.
            //
            // booker_id = passenger_id and kind = 0: proxy and package are C032/C037 (see RideKinds).
            // version starts at 1 because the contract types it `minimum: 1` and the 202 example
            // says 1; the column defaults to 0.
            var created = await connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
                $"""
                 INSERT INTO rides.rides
                   (passenger_id, client_request_id, booker_id, rider_id, kind, vehicle_type,
                    pickup_geo, dropoff_geo, state, payment_method,
                    fare_estimate_minor, fare_surcharge_minor, currency, version)
                 VALUES
                   (@PassengerId, @ClientRequestId, @PassengerId, @PassengerId, 0, @VehicleType,
                    @Pickup, @Dropoff, '{RideStates.Requested}', @PaymentMethod,
                    @FareEstimateMinor, @FareSurchargeMinor, 'LKR', 1)
                 ON CONFLICT (passenger_id, client_request_id) DO NOTHING
                 RETURNING {Columns};
                 """,
                new
                {
                    ride.PassengerId,
                    ride.ClientRequestId,
                    ride.VehicleType,
                    ride.Pickup,
                    ride.Dropoff,
                    ride.PaymentMethod,
                    ride.FareEstimateMinor,
                    ride.FareSurchargeMinor,
                },
                transaction,
                cancellationToken: cancellationToken));

            if (created is not null)
            {
                return new RideCreateResult(RideCreateOutcome.Created, created);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == OpenPassengerIndex)
        {
            return new RideCreateResult(RideCreateOutcome.ActiveRideExists, null);
        }

        var existing = await FindByClientRequestAsync(
            connection, transaction, ride.PassengerId, ride.ClientRequestId, cancellationToken);

        // The conflict fired, so the row exists; it can only be missing if something deleted it
        // between the two statements, which nothing in the platform does.
        return new RideCreateResult(RideCreateOutcome.AlreadyRequested, existing);
    }

    public Task<RideRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"SELECT {Columns} FROM rides.rides WHERE id = @RideId;",
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> FindActiveByPassengerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid passengerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Newest first: the ux_rides_open_passenger index exempts Completed, so a passenger can
        // legitimately hold a Completed ride and a freshly Requested one at the same time, and the
        // one they are looking at is the new one (C004 note (b)).
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM rides.rides
              WHERE passenger_id = @PassengerId
                AND NOT (state = ANY(@TerminalStates))
              ORDER BY created_at DESC, id DESC
              LIMIT 1;
             """,
            new { PassengerId = passengerId, TerminalStates },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> FindActiveByDriverAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ux_rides_driver_busy makes at most one of these non-terminal at a time (O2, R-10); the
        // ORDER BY only decides which Completed ride surfaces if the driver has finished one and
        // is holding an offer on another.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM rides.rides
              WHERE (accepted_driver_id = @DriverId OR offered_driver_id = @DriverId)
                AND NOT (state = ANY(@TerminalStates))
              ORDER BY created_at DESC, id DESC
              LIMIT 1;
             """,
            new { DriverId = driverId, TerminalStates },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> MarkMatchingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = '{RideStates.Matching}', version = version + 1, updated_at = now()
              WHERE id = @RideId
                AND state = '{RideStates.Requested}'
                AND (@ExpectedVersion::bigint IS NULL OR version = @ExpectedVersion)
             RETURNING {Columns};
             """,
            new { RideId = rideId, ExpectedVersion = expectedVersion },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> PlaceOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        Guid vehicleId,
        DateTimeOffset expiresAt,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = '{RideStates.Offered}',
                    current_offer_id = @OfferId,
                    offered_driver_id = @DriverId,
                    offered_vehicle_id = @VehicleId,
                    offer_expires_at = @ExpiresAt,
                    version = version + 1,
                    updated_at = now()
              WHERE id = @RideId
                AND state = '{RideStates.Matching}'
                AND (@ExpectedVersion::bigint IS NULL OR version = @ExpectedVersion)
             RETURNING {Columns};
             """,
            new
            {
                RideId = rideId,
                OfferId = offerId,
                DriverId = driverId,
                VehicleId = vehicleId,
                ExpiresAt = expiresAt,
                ExpectedVersion = expectedVersion,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> AcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid driverId,
        Guid offerId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ADD §11.11 / D5' §6.1, statement for statement. Two properties are load-bearing:
        //
        //  * `offer_expires_at > now()` is evaluated by Postgres, not by the process — the 15 s
        //    TTL must not depend on the accepting node's clock.
        //  * there is deliberately NO `offered_driver_id = :driverId` predicate. The conditional
        //    UPDATE is the single arbiter; adding a driver predicate would turn the concurrent
        //    double-accept §11.11 is written to resolve into two 403s, and the guarantee that
        //    exactly one caller sees row_count = 1 would move from the database into whichever
        //    process read the row last. `dispatch.offers`' UNIQUE partial index on driver_id is
        //    the other half of the pair (C023).
        //
        // accepted_vehicle_id comes off the offer rather than from a lookup, so the vehicle the
        // ride records is the one the offer was made for — but only when the accepting driver IS
        // the one it was reserved for. A winner who was never offered the ride (only reachable
        // when an offerId leaks, and stopped in production by dispatch.offers' UNIQUE partial
        // index) records no vehicle rather than somebody else's.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = '{RideStates.Accepted}',
                    accepted_driver_id = @DriverId,
                    accepted_vehicle_id = CASE WHEN offered_driver_id = @DriverId
                                               THEN offered_vehicle_id END,
                    version = version + 1,
                    updated_at = now()
              WHERE id = @RideId
                AND state IN ('{RideStates.Matching}', '{RideStates.Offered}')
                AND current_offer_id = @OfferId
                AND offer_expires_at > now()
                AND version = @ExpectedVersion
             RETURNING {Columns};
             """,
            new { RideId = rideId, DriverId = driverId, OfferId = offerId, ExpectedVersion = expectedVersion },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> DeclineOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Declining IS bound to the offered driver: no version is echoed on this route (the
        // contract's body is `{offerId}` alone), so the driver identity is the only thing stopping
        // one driver from releasing another's offer. Clearing the offer columns together keeps
        // ck_rides_offer_pair satisfied.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = '{RideStates.Matching}',
                    current_offer_id = NULL,
                    offer_expires_at = NULL,
                    offered_driver_id = NULL,
                    offered_vehicle_id = NULL,
                    version = version + 1,
                    updated_at = now()
              WHERE id = @RideId
                AND state = '{RideStates.Offered}'
                AND current_offer_id = @OfferId
                AND offered_driver_id = @DriverId
             RETURNING {Columns};
             """,
            new { RideId = rideId, OfferId = offerId, DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> ExpireOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Deliberately NOT bound to a driver: nobody acts here, the deadline does. It IS bound to
        // `offer_expires_at <= now()`, which is the same predicate — negated — that decides an
        // accept, and it is Postgres that evaluates both. A backstop that trusted the sweeping
        // node's clock could cancel an offer a driver was still inside the window to accept.
        //
        // The offer columns are cleared exactly as on a decline, so `Offered → Matching` leaves the
        // ride with no live offer either way. Leaving `current_offer_id` set would make the second
        // origin of the ADD §11.11 accept (`state IN ('Matching','Offered')`) reachable and the
        // accept's `from_state='Offered'` audit row start lying — the question C022's handoff left
        // open for whoever landed R-04.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = '{RideStates.Matching}',
                    current_offer_id = NULL,
                    offer_expires_at = NULL,
                    offered_driver_id = NULL,
                    offered_vehicle_id = NULL,
                    version = version + 1,
                    updated_at = now()
              WHERE id = @RideId
                AND state = '{RideStates.Offered}'
                AND current_offer_id = @OfferId
                AND offer_expires_at <= now()
             RETURNING {Columns};
             """,
            new { RideId = rideId, OfferId = offerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> AdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyCollection<string> fromStates,
        string toState,
        long? expectedVersion,
        Guid? requiredDriverId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fromStates);

        // The driver predicate is part of the statement rather than a check afterwards: a caller
        // who does not own the ride must never take a row lock on it, and a post-hoc check would
        // mean writing and rolling back on every unauthorised call.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = @ToState, version = version + 1, updated_at = now()
              WHERE id = @RideId
                AND state = ANY(@FromStates)
                AND (@ExpectedVersion::bigint IS NULL OR version = @ExpectedVersion)
                AND (@RequiredDriverId::uuid IS NULL OR accepted_driver_id = @RequiredDriverId)
             RETURNING {Columns};
             """,
            new
            {
                RideId = rideId,
                FromStates = fromStates.ToArray(),
                ToState = toState,
                ExpectedVersion = expectedVersion,
                RequiredDriverId = requiredDriverId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> TerminateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string fromState,
        string toState,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // One origin state per call, not `= ANY(...)`: the §11.12 matrix resolves the outcome from
        // the state the caller believed the ride was in, so binding the UPDATE to that same state
        // is what stops a ride that moved on between the read and the write from being terminated
        // under the wrong row's rules. Row count 0 sends the caller back to the matrix.
        //
        // `accepted_driver_id` is deliberately kept: the audit and every consumer need to know who
        // was driving when it ended, and ux_rides_driver_busy releases the driver by itself the
        // moment the state leaves its four-state list.
        //
        // The offer columns that describe a *live* offer are cleared, exactly as a decline clears
        // them; `offered_driver_id`/`offered_vehicle_id` stay, because they are the record of who
        // was asked and RideRow.IsParticipant lets that driver read the ride they were offered.
        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             UPDATE rides.rides
                SET state = @ToState,
                    current_offer_id = NULL,
                    offer_expires_at = NULL,
                    terminal_at = now(),
                    version = version + 1,
                    updated_at = now()
              WHERE id = @RideId
                AND state = @FromState
                AND (@ExpectedVersion::bigint IS NULL OR version = @ExpectedVersion)
             RETURNING {Columns};
             """,
            new
            {
                RideId = rideId,
                FromState = fromState,
                ToState = toState,
                ExpectedVersion = expectedVersion,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RideRow?> FindBusyByVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM rides.rides
              WHERE accepted_vehicle_id = @VehicleId
                AND state = ANY(@BusyStates)
              ORDER BY updated_at DESC
              LIMIT 1;
             """,
            new { VehicleId = vehicleId, BusyStates = DriverBusyStates },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountConsecutiveRiderCancellationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid passengerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // US-6A.10b, read literally: "the counter is consecutive — it resets to zero on any
        // successfully completed ride", and "only cancellations made after a driver has accepted
        // count; pre-acceptance cancellations never count".
        //
        // So the sequence is filtered to the two outcomes that mean anything — a post-acceptance
        // cancel and a ride that completed — and the answer is the length of the run of cancels at
        // its head. Everything else (a pre-acceptance cancel, a driver cancel, ExpiredNoDriver, a
        // no-show) neither increments nor resets, which is what "never count" has to mean: an
        // event that reset the counter would be an event that *helped* the passenger, and the URD
        // grants that only to a completed ride.
        //
        // Derived from this service's own rides rather than stored, because reputation-svc (C033)
        // owns `reputation.counters.cancellations_continuous` and "counters live there and nowhere
        // else". This is the same question answered from the aggregate that produced the facts;
        // when C033 lands, ride-svc asks it over gRPC instead. Recorded in the C032 handoff.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
             WITH outcomes AS (
               SELECT state,
                      row_number() OVER (ORDER BY COALESCE(terminal_at, updated_at) DESC, id DESC) AS rn
                 FROM rides.rides
                WHERE passenger_id = @PassengerId
                  AND state = ANY(@CountedStates))
             SELECT COALESCE(min(rn) - 1, (SELECT count(*) FROM outcomes))::int
               FROM outcomes
              WHERE state <> '{RideStates.CancelledByRiderAfterAccept}';
             """,
            new { PassengerId = passengerId, CountedStates = CountedOutcomeStates },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountStuckAsync(
        NpgsqlConnection connection, string state, TimeSpan age, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ADD §13.3.1: `count(rides WHERE state=S AND age > T)`, where age is time in the state.
        // `updated_at` is that instant — every transition writes it — and it is cheaper than
        // joining rides.transitions on the scrape path.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM rides.rides
             WHERE state = @State
               AND updated_at < now() - make_interval(secs => @Seconds);
            """,
            new { State = state, Seconds = age.TotalSeconds },
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountPendingOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM rides.outbox WHERE aggregate_id = @RideId AND dispatched_at IS NULL;",
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private Task<RideRow?> FindByClientRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        Guid clientRequestId,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<RideRow>(new CommandDefinition(
            $"SELECT {Columns} FROM rides.rides WHERE passenger_id = @PassengerId AND client_request_id = @ClientRequestId;",
            new { PassengerId = passengerId, ClientRequestId = clientRequestId },
            transaction,
            cancellationToken: cancellationToken));
}
