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
    string? ProviderTransactionId,
    DateTimeOffset? QrClaimedAt,
    DateTimeOffset? QrConfirmedAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>What the passenger owes in total — the fare plus OnePay's surcharge plus any tip.</summary>
    public long PayableMinor => AmountMinor + SurchargeMinor + TipAmountMinor;
}

/// <summary>
/// Columns a transition may set on its way past. Everything left <see langword="null"/> is untouched.
/// </summary>
/// <remarks>
/// One patch record rather than nine optional parameters: every transition is a single guarded
/// <c>UPDATE</c>, and the columns that move with a given state change are part of that change. A
/// separate write afterwards would be a second statement another caller could interleave with.
/// </remarks>
public sealed record PaymentPatch(
    string? Method = null,
    long? AmountMinor = null,
    long? SurchargeMinor = null,
    long? TipAmountMinor = null,
    string? ProviderTransactionId = null,
    string? PayerRole = null,
    Guid? PayerUserId = null,
    DateTimeOffset? QrClaimedAt = null,
    Guid? QrClaimArtifactId = null,
    DateTimeOffset? QrConfirmedAt = null);

/// <summary>
/// <c>fares.ride_payments</c> — the row C049 creates and the machine C050 drives it through.
/// </summary>
internal interface IRidePaymentRepository
{
    /// <summary>The live payment for a ride, or <see langword="null"/>.</summary>
    Task<RidePayment?> FindForRideAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>One payment by id.</summary>
    Task<RidePayment?> FindAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// The payment a gateway callback names, by the provider's own reference (R-19's dedupe key).
    /// </summary>
    Task<RidePayment?> FindByProviderRefAsync(string providerTransactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a payment, <b>only</b> from the state the machine resolved it from.
    /// </summary>
    /// <remarks>
    /// <b>The <c>WHERE state = @From</c> is the whole of the concurrency argument.</b> Two callers
    /// racing one payment — a retrying webhook and a passenger tapping "pay cash", a driver
    /// confirming while a claim lands — both read the same state and both compute the same
    /// transition; the database picks which one applies it, and the loser gets no row back and is
    /// answered a conflict rather than overwriting a settlement.
    /// </remarks>
    Task<RidePayment?> TransitionAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string fromState,
        string toState,
        PaymentPatch? patch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the next attempt on a ride — §11.8's retry, a new row pointing back at the one it
    /// replaces.
    /// </summary>
    /// <remarks>
    /// A new row rather than a mutation, because <c>provider_transaction_id</c> is UNIQUE and must
    /// stay one-to-one with a gateway call: reusing the row would make the retry chain
    /// unreconstructable and give two gateway attempts one reference (1002's header).
    /// </remarks>
    Task<RidePayment> CreateRetryAsync(
        IUnitOfWork unitOfWork, RidePayment previous, string method, long amountMinor, long surchargeMinor,
        CancellationToken cancellationToken);

    /// <summary>
    /// AL-47's escalation queue: claims the driver has not answered, oldest first.
    /// </summary>
    /// <remarks>
    /// Reads <c>ix_ridepay_qr_unconfirmed</c> (migration 1002), which exists for exactly this — its
    /// own comment says "D5 escalates these on a timer, so the scan is by age".
    /// </remarks>
    Task<IReadOnlyList<RidePayment>> ListUnconfirmedQrClaimsAsync(
        DateTimeOffset claimedBefore, int limit, CancellationToken cancellationToken);

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
        currency, payer_role, payer_user_id, attempt_no, provider_transaction_id,
        qr_claimed_at, qr_confirmed_at, created_at
        """;

    public async Task<RidePayment?> FindForRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await FindAsync(connection, null, rideId, cancellationToken);
    }

    public async Task<RidePayment?> FindAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<RidePayment>(new CommandDefinition(
            $"SELECT {Columns} FROM fares.ride_payments WHERE id = @PaymentId;",
            new { PaymentId = paymentId },
            cancellationToken: cancellationToken));
    }

    public async Task<RidePayment?> FindByProviderRefAsync(
        string providerTransactionId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<RidePayment>(new CommandDefinition(
            $"SELECT {Columns} FROM fares.ride_payments WHERE provider_transaction_id = @Ref;",
            new { Ref = providerTransactionId },
            cancellationToken: cancellationToken));
    }

    public Task<RidePayment?> TransitionAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string fromState,
        string toState,
        PaymentPatch? patch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var set = patch ?? new PaymentPatch();

        // coalesce on every optional column, so one statement serves every transition and a column
        // the caller did not name keeps whatever it already held.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<RidePayment>(new CommandDefinition(
            $"""
             UPDATE fares.ride_payments
                SET state = @ToState,
                    method = coalesce(@Method, method),
                    amount_minor = coalesce(@AmountMinor::int, amount_minor),
                    surcharge_minor = coalesce(@SurchargeMinor::int, surcharge_minor),
                    tip_amount_minor = coalesce(@TipAmountMinor::int, tip_amount_minor),
                    provider_transaction_id = coalesce(@ProviderTransactionId, provider_transaction_id),
                    payer_role = coalesce(@PayerRole, payer_role),
                    payer_user_id = coalesce(@PayerUserId, payer_user_id),
                    qr_claimed_at = coalesce(@QrClaimedAt, qr_claimed_at),
                    qr_claim_artifact_id = coalesce(@QrClaimArtifactId, qr_claim_artifact_id),
                    qr_confirmed_at = coalesce(@QrConfirmedAt, qr_confirmed_at)
              WHERE id = @PaymentId AND state = @FromState
             RETURNING {Columns};
             """,
            new
            {
                PaymentId = paymentId,
                FromState = fromState,
                ToState = toState,
                set.Method,
                set.AmountMinor,
                set.SurchargeMinor,
                set.TipAmountMinor,
                set.ProviderTransactionId,
                set.PayerRole,
                set.PayerUserId,
                set.QrClaimedAt,
                set.QrClaimArtifactId,
                set.QrConfirmedAt,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<RidePayment> CreateRetryAsync(
        IUnitOfWork unitOfWork,
        RidePayment previous,
        string method,
        long amountMinor,
        long surchargeMinor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(previous);

        return unitOfWork.Connection.QuerySingleAsync<RidePayment>(new CommandDefinition(
            $"""
             INSERT INTO fares.ride_payments
               (ride_id, state, method, amount_minor, surcharge_minor, tip_amount_minor, currency,
                payer_role, payer_user_id, retry_of_payment_id, attempt_no)
             VALUES
               (@RideId, '{RidePaymentStates.Initiated}', @Method, @AmountMinor::int,
                @SurchargeMinor::int, @TipAmountMinor::int, @Currency, @PayerRole, @PayerUserId,
                @RetryOf, @AttemptNo::smallint)
             RETURNING {Columns};
             """,
            new
            {
                previous.RideId,
                Method = method,
                AmountMinor = amountMinor,
                SurchargeMinor = surchargeMinor,
                previous.TipAmountMinor,
                previous.Currency,
                previous.PayerRole,
                previous.PayerUserId,
                RetryOf = previous.Id,
                AttemptNo = previous.AttemptNo + 1,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RidePayment>> ListUnconfirmedQrClaimsAsync(
        DateTimeOffset claimedBefore, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<RidePayment>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM fares.ride_payments
              WHERE state = '{RidePaymentStates.QrClaimedByPassenger}'
                AND qr_claimed_at < @ClaimedBefore
              ORDER BY qr_claimed_at
              LIMIT @Limit;
             """,
            new { ClaimedBefore = claimedBefore, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
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
