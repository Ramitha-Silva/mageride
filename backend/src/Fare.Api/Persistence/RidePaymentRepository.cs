using Dapper;
using MageRide.Fare.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Fare.Persistence;

/// <summary>One row of <c>fares.ride_payments</c> (migration 1002) — one payment <b>attempt</b>.</summary>
public sealed record RidePayment(
    Guid Id,
    Guid RideId,
    string State,
    string Method,
    long AmountMinor,
    long SurchargeMinor,
    long TipAmountMinor,
    string Currency,
    string PayerRole,
    Guid? PayerUserId,
    short AttemptNo,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>fares.ride_payments</c>, as far as C049 goes: the <c>Initiated</c> row a completed ride
/// produces.
/// </summary>
/// <remarks>
/// <b>The state machine is C050's.</b> This component writes exactly one state — the first — and
/// reads back what it wrote. Every transition out of <c>Initiated</c>, every gateway callback and
/// every refund belongs to the next component, and nothing here should grow an <c>UPDATE state</c>.
/// </remarks>
internal interface IRidePaymentRepository
{
    /// <summary>The live payment for a ride, or <see langword="null"/>.</summary>
    Task<RidePayment?> FindForRideAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the <c>Initiated</c> payment for a completed ride, or returns the one already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent on the ride, and the guard is an index.</b> ride-svc's <c>complete</c> is
    /// at-least-once and the contract puts an <c>Idempotency-Key</c> on this route, but a header
    /// dedupes identical <em>requests</em> and what must be single-shot is the <em>ride</em>: two
    /// different keys for one ride would otherwise leave a passenger with two fares.
    /// </para>
    /// <para>
    /// <b>A transaction alone does not do it, and that was tried.</b> A <c>SELECT … FOR UPDATE</c>
    /// that matches no row locks nothing, so six concurrent completions all read empty and all
    /// insert. <c>ux_ride_payments_first_attempt</c> (migration 1006) is the real guard — partial on
    /// <c>attempt_no = 1</c>, because D-10's retry chain is several attempts on one ride and a plain
    /// UNIQUE on <c>ride_id</c> would forbid the retry the state machine depends on.
    /// </para>
    /// </remarks>
    Task<RidePayment> CreateInitiatedAsync(
        IUnitOfWork unitOfWork,
        Guid rideId,
        string method,
        long amountMinor,
        string currency,
        string payerRole,
        Guid? payerUserId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRidePaymentRepository"/>
internal sealed class RidePaymentRepository(INpgsqlConnectionFactory connections) : IRidePaymentRepository
{
    /// <remarks>
    /// The three money columns are <c>INTEGER</c> in §9 while the contract types money as int64, and
    /// <c>attempt_no</c> is <c>SMALLINT</c>. Dapper matches constructor parameter types exactly.
    /// </remarks>
    private const string Columns =
        """
        id, ride_id, state, method, amount_minor::bigint AS amount_minor,
        surcharge_minor::bigint AS surcharge_minor, tip_amount_minor::bigint AS tip_amount_minor,
        currency, payer_role, payer_user_id, attempt_no, created_at
        """;

    public async Task<RidePayment?> FindForRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await FindAsync(connection, null, rideId, cancellationToken);
    }

    public async Task<RidePayment> CreateInitiatedAsync(
        IUnitOfWork unitOfWork,
        Guid rideId,
        string method,
        long amountMinor,
        string currency,
        string payerRole,
        Guid? payerUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // ON CONFLICT DO NOTHING against ux_ride_payments_first_attempt: the database picks the
        // winner, and every loser falls through to the read below and is handed what the winner
        // wrote. No lock is taken, because there is nothing to lock until the row exists.
        var inserted = await unitOfWork.Connection.QuerySingleOrDefaultAsync<RidePayment>(new CommandDefinition(
            $"""
             INSERT INTO fares.ride_payments
               (ride_id, state, method, amount_minor, currency, payer_role, payer_user_id)
             VALUES
               (@RideId, '{RidePaymentStates.Initiated}', @Method, @AmountMinor::int, @Currency,
                @PayerRole, @PayerUserId)
             ON CONFLICT DO NOTHING
             RETURNING {Columns};
             """,
            new
            {
                RideId = rideId,
                Method = method,
                AmountMinor = amountMinor,
                Currency = currency,
                PayerRole = payerRole,
                PayerUserId = payerUserId,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return inserted
               ?? await FindAsync(unitOfWork.Connection, unitOfWork.Transaction, rideId, cancellationToken)
               ?? throw new InvalidOperationException(
                   $"Ride {rideId} has neither a new nor an existing payment after an insert conflict.");
    }

    /// <remarks>
    /// The newest attempt, because D-10's retry chain puts several rows on one ride and the one that
    /// matters is the live one. No <c>FOR UPDATE</c>: the single-shot guarantee is
    /// <c>ux_ride_payments_first_attempt</c>, and a lock here would be a second, weaker guard over
    /// the same invariant — one that does nothing at all on the path that matters, because a row
    /// that does not exist yet cannot be locked.
    /// </remarks>
    private static Task<RidePayment?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<RidePayment>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM fares.ride_payments
              WHERE ride_id = @RideId
              ORDER BY attempt_no DESC, created_at DESC
              LIMIT 1;
             """,
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));
}

/// <summary>
/// <c>fares.driver_earnings</c> (migration 1004) — the per-driver per-day rollup behind
/// SCR-DA-021.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, never the master.</b> Migration 1004 says so at the table: the ledger holds the
/// money and this is a read model so the Earnings screen does not aggregate journal postings on
/// every open. A disagreement between the two is a bug in this rollup, never in the balance.
/// </para>
/// <para>
/// <b>R-05 decides when a row moves, and R-05 is not reachable from C049.</b> "Driver earning posts
/// only once the payment reaches a terminal state" — and the states that terminate a payment are
/// C050's, so <see cref="PostAsync"/> is written, tested and called by the payment machine when it
/// lands. It is here rather than there because the rollup is part of the fare model: the same
/// component that decides what a trip is worth decides what a driver earned from it. Named in the
/// C049 handoff.
/// </para>
/// </remarks>
internal interface IDriverEarningsRepository
{
    /// <summary>
    /// Adds one settled trip to a driver's Colombo day.
    /// </summary>
    /// <remarks>
    /// The upsert is additive, so it is <b>not</b> idempotent on its own — calling it twice for one
    /// ride counts the trip twice. That is deliberate: the guard belongs with the state transition
    /// that establishes the terminal, where it is a single-shot <c>UPDATE</c>, and duplicating it
    /// here would be a second opinion about whether a payment settled.
    /// </remarks>
    Task PostAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        DateOnly earnDate,
        DateTimeOffset earnDateTzAt,
        long grossMinor,
        CancellationToken cancellationToken);

    Task<(int Trips, long GrossMinor)?> ReadAsync(
        Guid driverId, DateOnly earnDate, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverEarningsRepository"/>
internal sealed class DriverEarningsRepository(INpgsqlConnectionFactory connections) : IDriverEarningsRepository
{
    public Task PostAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        DateOnly earnDate,
        DateTimeOffset earnDateTzAt,
        long grossMinor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO fares.driver_earnings
              (driver_id, earn_date, earn_date_tz_at, trips, gross_minor)
            VALUES (@DriverId, @EarnDate, @EarnDateTzAt, 1, @GrossMinor::int)
            ON CONFLICT (driver_id, earn_date) DO UPDATE
              SET trips = fares.driver_earnings.trips + 1,
                  gross_minor = fares.driver_earnings.gross_minor + EXCLUDED.gross_minor;
            """,
            new
            {
                DriverId = driverId,
                EarnDate = earnDate,
                EarnDateTzAt = earnDateTzAt,
                GrossMinor = grossMinor,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<(int Trips, long GrossMinor)?> ReadAsync(
        Guid driverId, DateOnly earnDate, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(int Trips, long GrossMinor)>(new CommandDefinition(
            """
            SELECT trips, gross_minor::bigint AS gross_minor
              FROM fares.driver_earnings
             WHERE driver_id = @DriverId AND earn_date = @EarnDate;
            """,
            new { DriverId = driverId, EarnDate = earnDate },
            cancellationToken: cancellationToken));

        return rows.Cast<(int Trips, long GrossMinor)?>().FirstOrDefault();
    }
}
