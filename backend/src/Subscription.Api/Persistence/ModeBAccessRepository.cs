using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Subscriptions.Domain;
using Npgsql;

namespace MageRide.Subscriptions.Persistence;

/// <summary>One row of <c>subscription.access_requests</c> (US-4.9/4.10, AL-23).</summary>
public sealed record AccessRequestRow(
    Guid RequestId, Guid VehicleId, Guid PassengerId, string Status, DateTimeOffset CreatedAt);

/// <summary>One row of <c>subscription.grants</c> — the tracking-access grant AL-25 keeps muted.</summary>
public sealed record GrantRow(
    Guid GrantId,
    Guid VehicleId,
    Guid PassengerId,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? UnsubscribedAt,
    DateTimeOffset? DeletedAt);

/// <summary>One row of <c>subscription.subscriptions</c> — the per-subscriber fare and cycle.</summary>
public sealed record SubscriptionRow(
    Guid SubscriptionId,
    Guid GrantId,
    Guid VehicleId,
    Guid PassengerId,
    string Billing,
    long? MonthlyFareMinor,
    string Currency,
    string Cycle,
    int? JoinDay,
    DateOnly? NextDue,
    DateTimeOffset NextDueTzAt,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>One line of the owner's roster (item 16, SCR-FP-011).</summary>
/// <param name="SubscriberId">
/// The <b>grant</b> id. It is the roster row's identity — the thing an owner deletes and sets a fare
/// on — and it survives a subscription being cancelled and re-created by a rejoin, which is what
/// makes the Fleet Portal's per-subscriber ledger continuous across one.
/// </param>
/// <param name="ThisMonthPaymentStatus">
/// The live <c>subscription.payments</c> row for the current Colombo month, or <see langword="null"/>
/// when there is none.
/// </param>
public sealed record SubscriberRosterRow(
    Guid SubscriberId,
    Guid PassengerId,
    string GrantStatus,
    DateTimeOffset GrantedAt,
    Guid? SubscriptionId,
    string? Billing,
    long? MonthlyFareMinor,
    string? Currency,
    string? Cycle,
    DateOnly? NextDue,
    string? ThisMonthPaymentStatus);

/// <summary>
/// <c>subscription.access_requests</c>, <c>subscription.grants</c> and
/// <c>subscription.subscriptions</c> — everything in Epic 23 except the money.
/// </summary>
/// <remarks>
/// <b>Every method is per vehicle.</b> AL-23 is a fence, and it is held here by the queries rather
/// than by the callers: there is no method on this interface that takes a fleet, an owner or an
/// account, so there is no shape in which an account-global grant could be written.
/// </remarks>
internal interface IModeBAccessRepository
{
    // -- access requests ------------------------------------------------------------------------

    /// <summary>
    /// Raises a request, or returns the one already open for the pair.
    /// </summary>
    /// <remarks>
    /// <c>ux_access_request_open</c> is partial over <c>status = 'pending'</c>, so a second ask
    /// collides and is collapsed onto the first — a passenger tapping twice sees one request, not a
    /// <c>409</c> they cannot act on. A <em>rejected</em> request is outside the predicate, which is
    /// what lets somebody ask again later.
    /// </remarks>
    Task<AccessRequestRow> RequestAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken);

    Task<AccessRequestRow?> FindRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>The vehicle's pending queue, oldest first — the order an owner works it in.</summary>
    Task<IReadOnlyList<AccessRequestRow>> ListPendingRequestsAsync(
        NpgsqlConnection connection,
        Guid vehicleId,
        (DateTimeOffset RequestedAt, Guid RequestId)? after,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a pending request to <c>accepted</c> or <c>rejected</c>. Returns <see langword="null"/>
    /// when it was already decided — a second decision must not overwrite the first.
    /// </summary>
    Task<AccessRequestRow?> DecideRequestAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string status,
        Guid decidedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    // -- grants ---------------------------------------------------------------------------------

    Task<GrantRow?> FindGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid grantId,
        CancellationToken cancellationToken);

    Task<GrantRow?> FindGrantForPairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The grant an accept produces: a new one, or the muted one this pair already holds, made
    /// active again.
    /// </summary>
    /// <remarks>
    /// <b>A rejoin reuses the row rather than inserting a second one</b>, and it has to:
    /// <c>ux_grant_active</c> is partial on <c>deleted_at IS NULL</c>, so an unsubscribed grant still
    /// occupies the (vehicle, passenger) slot until the owner deletes it (AL-25). Inserting would
    /// fail exactly for the passenger who is rejoining, which is the case US-4.12 is about.
    /// </remarks>
    Task<GrantRow> GrantAccessAsync(
        IUnitOfWork unitOfWork,
        Guid vehicleId,
        Guid passengerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// The owner's hard delete (US-4.12). Returns <see langword="null"/> when the grant is not
    /// unsubscribed — an active subscriber is removed by unsubscribing, not by the owner.
    /// </summary>
    Task<GrantRow?> DeleteGrantAsync(
        IUnitOfWork unitOfWork,
        Guid vehicleId,
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    // -- subscriptions --------------------------------------------------------------------------

    /// <summary>
    /// Starts the subscription an accept creates, or returns the live one the grant already carries.
    /// </summary>
    Task<SubscriptionRow> StartSubscriptionAsync(
        IUnitOfWork unitOfWork,
        GrantRow grant,
        string billing,
        long? monthlyFareMinor,
        string currency,
        string cycle,
        int joinDay,
        DateOnly? nextDue,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<SubscriptionRow?> FindSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    /// <summary>The live subscription on a grant, or <see langword="null"/> when it has none.</summary>
    Task<SubscriptionRow?> FindLiveSubscriptionForGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The passenger's own subscriptions (SCR-PA-025). Only those whose grant is live — an
    /// unsubscribed passenger "can no longer see the vehicle" (US-23.11).
    /// </summary>
    Task<IReadOnlyList<SubscriptionRow>> ListPassengerSubscriptionsAsync(
        Guid passengerId,
        (DateTimeOffset CreatedAt, Guid SubscriptionId)? after,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The passenger's unsubscribe: the grant is muted and the subscription cancelled, together.
    /// Returns <see langword="null"/> when the grant was not active.
    /// </summary>
    Task<SubscriptionRow?> UnsubscribeAsync(
        IUnitOfWork unitOfWork,
        Guid subscriptionId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>US-23.7 — the owner's per-subscriber fare override.</summary>
    Task<SubscriptionRow?> SetFareAsync(
        Guid grantId, long monthlyFareMinor, CancellationToken cancellationToken);

    /// <summary>Rolls the due date on once a month has been paid for (BR-23.9).</summary>
    Task<SubscriptionRow?> AdvanceNextDueAsync(
        IUnitOfWork unitOfWork,
        Guid subscriptionId,
        DateOnly nextDue,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    // -- roster ---------------------------------------------------------------------------------

    /// <summary>
    /// The vehicle's roster, newest grant first, including the muted rows (item 16, US-4.12).
    /// </summary>
    /// <param name="subscriberId">
    /// Narrows to one row. The fare override answers with a roster line, and re-reading it through
    /// the same query is what stops the write path and the read path describing a subscriber
    /// differently.
    /// </param>
    Task<IReadOnlyList<SubscriberRosterRow>> ListRosterAsync(
        Guid vehicleId,
        DateOnly periodMonth,
        (DateTimeOffset GrantedAt, Guid SubscriberId)? after,
        int limit,
        CancellationToken cancellationToken,
        Guid? subscriberId = null);
}

/// <inheritdoc cref="IModeBAccessRepository"/>
internal sealed class ModeBAccessRepository(INpgsqlConnectionFactory connections) : IModeBAccessRepository
{
    private const string RequestColumns =
        "id AS request_id, vehicle_id, passenger_id, status, requested_at AS created_at";

    private const string GrantColumns =
        "id AS grant_id, vehicle_id, passenger_id, status, granted_at, unsubscribed_at, deleted_at";

    /// <remarks>
    /// <c>monthly_fare_minor</c> is <c>INTEGER</c> in §18b and <c>join_day</c> is <c>SMALLINT</c>,
    /// while the contract types money as int64 and the day as int32. Dapper's constructor binding
    /// matches parameter types <em>exactly</em>, so an un-cast column does not fail to convert — it
    /// fails to materialise the record at all, and the row comes back null.
    /// </remarks>
    private const string SubscriptionColumns =
        """
        id AS subscription_id, grant_id, vehicle_id, passenger_id, billing,
        monthly_fare_minor::bigint AS monthly_fare_minor, currency, cycle,
        join_day::int AS join_day, next_due, next_due_tz_at, status, created_at
        """;

    // -- access requests ----------------------------------------------------------------------

    public async Task<AccessRequestRow> RequestAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var inserted = await connection.QuerySingleOrDefaultAsync<AccessRequestRow>(new CommandDefinition(
            $"""
             INSERT INTO subscription.access_requests (vehicle_id, passenger_id)
             VALUES (@VehicleId, @PassengerId)
             ON CONFLICT DO NOTHING
             RETURNING {RequestColumns};
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId },
            transaction,
            cancellationToken: cancellationToken));

        return inserted ?? await connection.QuerySingleAsync<AccessRequestRow>(new CommandDefinition(
            $"""
             SELECT {RequestColumns}
               FROM subscription.access_requests
              WHERE vehicle_id = @VehicleId AND passenger_id = @PassengerId AND status = 'pending';
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<AccessRequestRow?> FindRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<AccessRequestRow>(new CommandDefinition(
            $"SELECT {RequestColumns} FROM subscription.access_requests WHERE id = @RequestId;",
            new { RequestId = requestId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AccessRequestRow>> ListPendingRequestsAsync(
        NpgsqlConnection connection,
        Guid vehicleId,
        (DateTimeOffset RequestedAt, Guid RequestId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Oldest first, matching ix_access_requests_pending — a queue is worked in the order it
        // arrived, and the index exists for exactly this read.
        var rows = await connection.QueryAsync<AccessRequestRow>(new CommandDefinition(
            $"""
             SELECT {RequestColumns}
               FROM subscription.access_requests
              WHERE vehicle_id = @VehicleId
                AND status = 'pending'
                AND (@AfterAt::timestamptz IS NULL
                     OR (requested_at, id) > (@AfterAt, @AfterId))
              ORDER BY requested_at, id
              LIMIT @Limit;
             """,
            new
            {
                VehicleId = vehicleId,
                AfterAt = after?.RequestedAt,
                AfterId = after?.RequestId ?? Guid.Empty,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<AccessRequestRow?> DecideRequestAsync(
        IUnitOfWork unitOfWork,
        Guid requestId,
        string status,
        Guid decidedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // `status = 'pending'` is a predicate of the UPDATE, not a check before it: two owners
        // opening the same queue on two devices both reach here, and the database picks which one
        // decided. The loser gets no row and is answered 409 rather than silently overwriting.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<AccessRequestRow>(new CommandDefinition(
            $"""
             UPDATE subscription.access_requests
                SET status = @Status, decided_at = @Now, decided_by = @DecidedBy
              WHERE id = @RequestId AND status = 'pending'
             RETURNING {RequestColumns};
             """,
            new { RequestId = requestId, Status = status, DecidedBy = decidedBy, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    // -- grants -------------------------------------------------------------------------------

    public Task<GrantRow?> FindGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
            $"""
             SELECT {GrantColumns} FROM subscription.grants
              WHERE id = @GrantId AND vehicle_id = @VehicleId AND deleted_at IS NULL;
             """,
            new { GrantId = grantId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<GrantRow?> FindGrantForPairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
            $"""
             SELECT {GrantColumns} FROM subscription.grants
              WHERE vehicle_id = @VehicleId AND passenger_id = @PassengerId AND deleted_at IS NULL;
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<GrantRow> GrantAccessAsync(
        IUnitOfWork unitOfWork,
        Guid vehicleId,
        Guid passengerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var inserted = await unitOfWork.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
            $"""
             INSERT INTO subscription.grants (vehicle_id, passenger_id, status, granted_at)
             VALUES (@VehicleId, @PassengerId, '{GrantStatuses.Active}', @Now)
             ON CONFLICT DO NOTHING
             RETURNING {GrantColumns};
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        if (inserted is not null)
        {
            return inserted;
        }

        // The pair already holds a live row. `granted_at` moves only for a muted one, so re-accepting
        // an active grant is a no-op rather than a rewrite of when the subscriber actually joined —
        // which is what the roster orders by and what the anniversary cycle was anchored to.
        return await unitOfWork.Connection.QuerySingleAsync<GrantRow>(new CommandDefinition(
            $"""
             UPDATE subscription.grants
                SET status = '{GrantStatuses.Active}',
                    unsubscribed_at = NULL,
                    granted_at = CASE WHEN status = '{GrantStatuses.Unsubscribed}' THEN @Now ELSE granted_at END
              WHERE vehicle_id = @VehicleId AND passenger_id = @PassengerId AND deleted_at IS NULL
             RETURNING {GrantColumns};
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<GrantRow?> DeleteGrantAsync(
        IUnitOfWork unitOfWork,
        Guid vehicleId,
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
            $"""
             UPDATE subscription.grants
                SET deleted_at = @Now
              WHERE id = @GrantId
                AND vehicle_id = @VehicleId
                AND deleted_at IS NULL
                AND status = '{GrantStatuses.Unsubscribed}'
             RETURNING {GrantColumns};
             """,
            new { GrantId = grantId, VehicleId = vehicleId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    // -- subscriptions ------------------------------------------------------------------------

    public async Task<SubscriptionRow> StartSubscriptionAsync(
        IUnitOfWork unitOfWork,
        GrantRow grant,
        string billing,
        long? monthlyFareMinor,
        string currency,
        string cycle,
        int joinDay,
        DateOnly? nextDue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(grant);

        // ux_subscriptions_grant_live admits one non-cancelled subscription per grant, so a repeated
        // accept collides and is collapsed onto the live one. That is what makes the whole accept
        // idempotent without a second key: the grant is upserted, the subscription is not duplicated.
        var inserted = await unitOfWork.Connection.QuerySingleOrDefaultAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             INSERT INTO subscription.subscriptions
               (grant_id, vehicle_id, passenger_id, billing, monthly_fare_minor, currency,
                cycle, join_day, next_due, next_due_tz_at)
             VALUES
               (@GrantId, @VehicleId, @PassengerId, @Billing, @MonthlyFareMinor::int, @Currency,
                @Cycle, @JoinDay::smallint, @NextDue, @Now)
             ON CONFLICT DO NOTHING
             RETURNING {SubscriptionColumns};
             """,
            new
            {
                GrantId = grant.GrantId,
                grant.VehicleId,
                grant.PassengerId,
                Billing = billing,
                MonthlyFareMinor = monthlyFareMinor,
                Currency = currency,
                Cycle = cycle,
                JoinDay = joinDay,
                NextDue = nextDue,
                Now = now,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return inserted
               ?? await FindLiveSubscriptionForGrantAsync(
                   unitOfWork.Connection, unitOfWork.Transaction, grant.GrantId, cancellationToken)
               ?? throw new InvalidOperationException(
                   $"Grant {grant.GrantId} has neither a new nor a live subscription after an accept.");
    }

    public Task<SubscriptionRow?> FindSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<SubscriptionRow>(new CommandDefinition(
            $"SELECT {SubscriptionColumns} FROM subscription.subscriptions WHERE id = @SubscriptionId;",
            new { SubscriptionId = subscriptionId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SubscriptionRow?> FindLiveSubscriptionForGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             SELECT {SubscriptionColumns} FROM subscription.subscriptions
              WHERE grant_id = @GrantId AND status <> '{SubscriptionStatuses.Cancelled}';
             """,
            new { GrantId = grantId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SubscriptionRow>> ListPassengerSubscriptionsAsync(
        Guid passengerId,
        (DateTimeOffset CreatedAt, Guid SubscriptionId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // Both halves are load-bearing, and neither implies the other.
        //
        // The grant decides *visibility*: unsubscribing mutes it, and a row whose grant is muted or
        // deleted is gone from the passenger's app the moment they unsubscribe (US-23.11) — the same
        // rule fanout applies to the map.
        //
        // `s.status <> 'cancelled'` decides *which* subscription. A rejoin reactivates the very same
        // grant (ux_grant_active holds the slot until the owner deletes it), so without this the
        // subscription the passenger ended would come back beside the one they just started, and the
        // card list would grow by one every time somebody left and returned.
        var rows = await connection.QueryAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             SELECT {Prefixed(SubscriptionColumns, "s")}
               FROM subscription.subscriptions s
               JOIN subscription.grants g ON g.id = s.grant_id
              WHERE s.passenger_id = @PassengerId
                AND s.status <> '{SubscriptionStatuses.Cancelled}'
                AND g.deleted_at IS NULL
                AND g.status = '{GrantStatuses.Active}'
                AND (@AfterAt::timestamptz IS NULL
                     OR (s.created_at, s.id) < (@AfterAt, @AfterId))
              ORDER BY s.created_at DESC, s.id DESC
              LIMIT @Limit;
             """,
            new
            {
                PassengerId = passengerId,
                AfterAt = after?.CreatedAt,
                AfterId = after?.SubscriptionId ?? Guid.Empty,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<SubscriptionRow?> UnsubscribeAsync(
        IUnitOfWork unitOfWork,
        Guid subscriptionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // The grant first, because it is the guarded half: `status = 'active'` in the predicate is
        // what makes a second unsubscribe return nothing instead of muting an already-muted row and
        // publishing a second revocation. ck_grants_unsubscribed_pair keeps the status and the
        // instant one fact.
        var grant = await unitOfWork.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
            $"""
             UPDATE subscription.grants g
                SET status = '{GrantStatuses.Unsubscribed}', unsubscribed_at = @Now
               FROM subscription.subscriptions s
              WHERE s.id = @SubscriptionId
                AND g.id = s.grant_id
                AND g.status = '{GrantStatuses.Active}'
                AND g.deleted_at IS NULL
             RETURNING {Prefixed(GrantColumns, "g")};
             """,
            new { SubscriptionId = subscriptionId, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        if (grant is null)
        {
            return null;
        }

        // Billing stops with the visibility. A cancelled subscription leaves ux_subscriptions_grant_live
        // free, which is what lets a rejoin start a fresh one on the same grant.
        return await unitOfWork.Connection.QuerySingleAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             UPDATE subscription.subscriptions
                SET status = '{SubscriptionStatuses.Cancelled}'
              WHERE id = @SubscriptionId
             RETURNING {SubscriptionColumns};
             """,
            new { SubscriptionId = subscriptionId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<SubscriptionRow?> SetFareAsync(
        Guid grantId, long monthlyFareMinor, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // `billing = 'paid'` is a predicate rather than a check afterwards: ck_subscriptions_fare
        // refuses a fare on a Free subscription anyway, and letting the statement fail on the CHECK
        // would surface as a 500 where the caller deserves a 409.
        return await connection.QuerySingleOrDefaultAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             UPDATE subscription.subscriptions
                SET monthly_fare_minor = @MonthlyFareMinor::int
              WHERE grant_id = @GrantId
                AND status <> '{SubscriptionStatuses.Cancelled}'
                AND billing = '{SubscriptionBilling.Paid}'
             RETURNING {SubscriptionColumns};
             """,
            new { GrantId = grantId, MonthlyFareMinor = monthlyFareMinor },
            cancellationToken: cancellationToken));
    }

    public Task<SubscriptionRow?> AdvanceNextDueAsync(
        IUnitOfWork unitOfWork,
        Guid subscriptionId,
        DateOnly nextDue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<SubscriptionRow>(new CommandDefinition(
            $"""
             UPDATE subscription.subscriptions
                SET next_due = @NextDue, next_due_tz_at = @Now
              WHERE id = @SubscriptionId
             RETURNING {SubscriptionColumns};
             """,
            new { SubscriptionId = subscriptionId, NextDue = nextDue, Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    // -- roster -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SubscriberRosterRow>> ListRosterAsync(
        Guid vehicleId,
        DateOnly periodMonth,
        (DateTimeOffset GrantedAt, Guid SubscriberId)? after,
        int limit,
        CancellationToken cancellationToken,
        Guid? subscriberId = null)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // `g.deleted_at IS NULL`, not `g.status = 'active'`: an unsubscribed grant stays on the
        // owner's roster, muted, until they delete it (US-4.12/US-13.16). Hiding it here would make
        // "who left?" unanswerable from the screen the requirement is about — and would leave the
        // owner with no row to delete.
        var rows = await connection.QueryAsync<SubscriberRosterRow>(new CommandDefinition(
            $"""
             SELECT g.id AS subscriber_id,
                    g.passenger_id,
                    g.status AS grant_status,
                    g.granted_at,
                    s.id AS subscription_id,
                    s.billing,
                    s.monthly_fare_minor::bigint AS monthly_fare_minor,
                    s.currency,
                    s.cycle,
                    s.next_due,
                    -- The earliest live payment from the current Colombo month forward, which is
                    -- the month this subscriber is being collected for. `= @PeriodMonth` would be
                    -- wrong for the whole of Epic 23: a join_anniversary subscriber's first period
                    -- is *next* month (joined 5 June ⇒ due 6 July), so the owner would read
                    -- "unpaid" against somebody who owes nothing yet. `>= @PeriodMonth` with ASC
                    -- shows the nearest outstanding month and drops a settled one out of view as
                    -- soon as the month it covered has passed.
                    (SELECT p.status
                       FROM subscription.payments p
                      WHERE p.subscription_id = s.id
                        AND p.period_month >= @PeriodMonth
                        AND p.status IN ('{SubscriptionPaymentStatuses.Initiated}',
                                         '{SubscriptionPaymentStatuses.PendingVerification}',
                                         '{SubscriptionPaymentStatuses.Paid}')
                      ORDER BY p.period_month
                      LIMIT 1) AS this_month_payment_status
               FROM subscription.grants g
               -- The live subscription when there is one, and otherwise the most recent cancelled
               -- one. A muted row is a subscriber who left, and the owner's roster has to keep
               -- showing what they were paying — `billing` is a required field of the roster line,
               -- and a NULL there would be a row the Fleet Portal cannot render.
               LEFT JOIN LATERAL (
                 SELECT * FROM subscription.subscriptions sub
                  WHERE sub.grant_id = g.id
                  ORDER BY (sub.status <> '{SubscriptionStatuses.Cancelled}') DESC, sub.created_at DESC
                  LIMIT 1) s ON true
              WHERE g.vehicle_id = @VehicleId
                AND g.deleted_at IS NULL
                AND (@SubscriberId::uuid IS NULL OR g.id = @SubscriberId)
                AND (@AfterAt::timestamptz IS NULL
                     OR (g.granted_at, g.id) < (@AfterAt, @AfterId))
              ORDER BY g.granted_at DESC, g.id DESC
              LIMIT @Limit;
             """,
            new
            {
                VehicleId = vehicleId,
                PeriodMonth = periodMonth,
                SubscriberId = subscriberId,
                AfterAt = after?.GrantedAt,
                AfterId = after?.SubscriberId ?? Guid.Empty,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <summary>Qualifies a column list with a table alias, for the statements that join.</summary>
    private static string Prefixed(string columns, string alias) =>
        string.Join(
            ", ",
            columns
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(column => $"{alias}.{column}"));
}
