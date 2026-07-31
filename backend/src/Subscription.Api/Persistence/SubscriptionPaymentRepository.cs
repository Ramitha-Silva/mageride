using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Subscriptions.Domain;
using Npgsql;

namespace MageRide.Subscriptions.Persistence;

/// <summary>
/// One row of <c>subscription.payments</c> — a subscriber's monthly fare, on its way to the fleet
/// owner.
/// </summary>
/// <remarks>
/// <b>There is no <c>journal_entry_id</c> here and there must not be.</b> §18b is explicit: this
/// money moves passenger → owner bank-to-bank and never posts to <c>billing.journal_entries</c>. The
/// platform's own Mode B fee (<c>billing.monthly_subscriptions</c>) is the ledgered one, and the two
/// never net against each other.
/// </remarks>
public sealed record PaymentRow(
    Guid PaymentId,
    Guid SubscriptionId,
    Guid VehicleId,
    Guid PassengerId,
    DateOnly PeriodMonth,
    DateTimeOffset PeriodMonthTzAt,
    long AmountMinor,
    string Currency,
    string Method,
    string Status,
    string? SlipUrl,
    string? GatewayRef,
    Guid? ConfirmedBy,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);

/// <summary><c>subscription.payments</c> — the pass-through fare and its four settlement paths.</summary>
internal interface ISubscriptionPaymentRepository
{
    /// <summary>
    /// The live payment for a (subscription, month), or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// "Live" is <c>ux_subpay_period</c>'s predicate — <c>initiated</c>, <c>pending_verification</c>
    /// or <c>paid</c>. A <c>failed</c> attempt is outside it deliberately, so a passenger whose card
    /// was declined can try again.
    /// </remarks>
    Task<PaymentRow?> FindLiveForPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid subscriptionId,
        DateOnly periodMonth,
        CancellationToken cancellationToken);

    Task<PaymentRow?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid paymentId,
        CancellationToken cancellationToken);

    /// <summary>Finds a payment by the gateway's own transaction id — R-19's dedupe key.</summary>
    Task<PaymentRow?> FindByGatewayRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string gatewayRef,
        CancellationToken cancellationToken);

    Task<PaymentRow> InsertAsync(
        IUnitOfWork unitOfWork,
        SubscriptionRow subscription,
        DateOnly periodMonth,
        string method,
        long amountMinor,
        string status,
        Guid? confirmedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Re-issues an <c>initiated</c> pay sheet under a different method.</summary>
    Task<PaymentRow?> ChangeMethodAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string method,
        long amountMinor,
        CancellationToken cancellationToken);

    /// <summary>The transfer screenshot arriving (US-23.4) — <c>pending_verification</c> until confirmed.</summary>
    Task<PaymentRow?> AttachSlipAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string slipUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles a payment. The <c>status</c> predicate is what makes every settlement path
    /// single-shot — a redelivered callback, a double-tapped confirm and two owners on two devices
    /// all leave exactly one row moving to <c>paid</c>, and only that one advances the due date.
    /// </summary>
    Task<PaymentRow?> MarkPaidAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string method,
        long? amountMinor,
        Guid? confirmedBy,
        string? gatewayRef,
        string? requiredStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>A gateway saying the payment did not happen. Frees the month for another attempt.</summary>
    Task<PaymentRow?> MarkFailedAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string? gatewayRef,
        CancellationToken cancellationToken);

    /// <summary>SCR-PA-025b — the passenger's history for one subscription.</summary>
    Task<IReadOnlyList<PaymentRow>> ListForSubscriptionAsync(
        Guid subscriptionId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// SCR-FP-012 — the owner's ledger for one subscriber on one vehicle.
    /// </summary>
    /// <remarks>
    /// Keyed by (vehicle, passenger) rather than by subscription: a passenger who unsubscribed and
    /// rejoined has two subscription rows and one history, and the owner's screen is about the
    /// person.
    /// </remarks>
    Task<IReadOnlyList<PaymentRow>> ListForSubscriberAsync(
        Guid vehicleId,
        Guid passengerId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISubscriptionPaymentRepository"/>
internal sealed class SubscriptionPaymentRepository(INpgsqlConnectionFactory connections)
    : ISubscriptionPaymentRepository
{
    /// <remarks>
    /// <c>amount_minor</c> is <c>INTEGER</c> in §18b and the contract types money as int64; the cast
    /// is what lets Dapper's exact-type constructor binding materialise the record at all.
    /// </remarks>
    private const string Columns =
        """
        id AS payment_id, subscription_id, vehicle_id, passenger_id, period_month, period_month_tz_at,
        amount_minor::bigint AS amount_minor, currency, method, status, slip_url, gateway_ref,
        confirmed_by, paid_at, created_at
        """;

    private const string LiveStatuses =
        $"'{SubscriptionPaymentStatuses.Initiated}', "
        + $"'{SubscriptionPaymentStatuses.PendingVerification}', "
        + $"'{SubscriptionPaymentStatuses.Paid}'";

    public Task<PaymentRow?> FindLiveForPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid subscriptionId,
        DateOnly periodMonth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM subscription.payments
              WHERE subscription_id = @SubscriptionId
                AND period_month = @PeriodMonth
                AND status IN ({LiveStatuses});
             """,
            new { SubscriptionId = subscriptionId, PeriodMonth = periodMonth },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"SELECT {Columns} FROM subscription.payments WHERE id = @PaymentId;",
            new { PaymentId = paymentId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> FindByGatewayRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string gatewayRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM subscription.payments
              WHERE gateway_ref = @GatewayRef
              ORDER BY created_at DESC
              LIMIT 1;
             """,
            new { GatewayRef = gatewayRef },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow> InsertAsync(
        IUnitOfWork unitOfWork,
        SubscriptionRow subscription,
        DateOnly periodMonth,
        string method,
        long amountMinor,
        string status,
        Guid? confirmedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(subscription);

        // ck_subscription_payments_paid_at makes the status and the instant one fact, so paid_at is
        // derived from the status here rather than passed in and trusted.
        return unitOfWork.Connection.QuerySingleAsync<PaymentRow>(new CommandDefinition(
            $"""
             INSERT INTO subscription.payments
               (subscription_id, vehicle_id, passenger_id, period_month, period_month_tz_at,
                amount_minor, currency, method, status, confirmed_by, paid_at)
             VALUES
               (@SubscriptionId, @VehicleId, @PassengerId, @PeriodMonth, @Now,
                @AmountMinor::int, @Currency, @Method, @Status, @ConfirmedBy,
                CASE WHEN @Status = '{SubscriptionPaymentStatuses.Paid}' THEN @Now ELSE NULL END)
             RETURNING {Columns};
             """,
            new
            {
                subscription.SubscriptionId,
                subscription.VehicleId,
                subscription.PassengerId,
                PeriodMonth = periodMonth,
                AmountMinor = amountMinor,
                subscription.Currency,
                Method = method,
                Status = status,
                ConfirmedBy = confirmedBy,
                Now = now,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> ChangeMethodAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string method,
        long amountMinor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             UPDATE subscription.payments
                SET method = @Method, amount_minor = @AmountMinor::int
              WHERE id = @PaymentId AND status = '{SubscriptionPaymentStatuses.Initiated}'
             RETURNING {Columns};
             """,
            new { PaymentId = paymentId, Method = method, AmountMinor = amountMinor },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> AttachSlipAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string slipUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // A re-upload while still awaiting the owner is allowed and replaces the slip — a passenger
        // who sent a blurry screenshot has no other way to correct it. A paid month is not reopened.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             UPDATE subscription.payments
                SET slip_url = @SlipUrl, status = '{SubscriptionPaymentStatuses.PendingVerification}'
              WHERE id = @PaymentId
                AND status IN ('{SubscriptionPaymentStatuses.Initiated}',
                               '{SubscriptionPaymentStatuses.PendingVerification}')
             RETURNING {Columns};
             """,
            new { PaymentId = paymentId, SlipUrl = slipUrl },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> MarkPaidAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string method,
        long? amountMinor,
        Guid? confirmedBy,
        string? gatewayRef,
        string? requiredStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             UPDATE subscription.payments
                SET status = '{SubscriptionPaymentStatuses.Paid}',
                    method = @Method,
                    amount_minor = coalesce(@AmountMinor::int, amount_minor),
                    confirmed_by = coalesce(@ConfirmedBy, confirmed_by),
                    gateway_ref = coalesce(@GatewayRef, gateway_ref),
                    paid_at = @Now
              WHERE id = @PaymentId
                AND status <> '{SubscriptionPaymentStatuses.Paid}'
                AND (@RequiredStatus::text IS NULL OR status = @RequiredStatus)
             RETURNING {Columns};
             """,
            new
            {
                PaymentId = paymentId,
                Method = method,
                AmountMinor = amountMinor,
                ConfirmedBy = confirmedBy,
                GatewayRef = gatewayRef,
                RequiredStatus = requiredStatus,
                Now = now,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<PaymentRow?> MarkFailedAsync(
        IUnitOfWork unitOfWork,
        Guid paymentId,
        string? gatewayRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            $"""
             UPDATE subscription.payments
                SET status = '{SubscriptionPaymentStatuses.Failed}',
                    gateway_ref = coalesce(@GatewayRef, gateway_ref)
              WHERE id = @PaymentId AND status = '{SubscriptionPaymentStatuses.Initiated}'
             RETURNING {Columns};
             """,
            new { PaymentId = paymentId, GatewayRef = gatewayRef },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<IReadOnlyList<PaymentRow>> ListForSubscriptionAsync(
        Guid subscriptionId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken) =>
        ListAsync(
            "subscription_id = @SubscriptionId",
            new { SubscriptionId = subscriptionId, PassengerId = Guid.Empty, VehicleId = Guid.Empty },
            after,
            limit,
            cancellationToken);

    public Task<IReadOnlyList<PaymentRow>> ListForSubscriberAsync(
        Guid vehicleId,
        Guid passengerId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken) =>
        ListAsync(
            "vehicle_id = @VehicleId AND passenger_id = @PassengerId",
            new { SubscriptionId = Guid.Empty, VehicleId = vehicleId, PassengerId = passengerId },
            after,
            limit,
            cancellationToken);

    private async Task<IReadOnlyList<PaymentRow>> ListAsync(
        string scope,
        object scopeParameters,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var parameters = new DynamicParameters(scopeParameters);
        parameters.Add("AfterAt", after?.CreatedAt);
        parameters.Add("AfterId", after?.PaymentId ?? Guid.Empty);
        parameters.Add("Limit", limit);

        // Newest first, and the cursor is the (created_at, id) pair rather than the date: a
        // subscriber can have two rows on one day — a failed attempt and the transfer that replaced
        // it — and a date-only cursor would drop whichever straddled a page boundary.
        var rows = await connection.QueryAsync<PaymentRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM subscription.payments
              WHERE {scope}
                AND (@AfterAt::timestamptz IS NULL OR (created_at, id) < (@AfterAt, @AfterId))
              ORDER BY created_at DESC, id DESC
              LIMIT @Limit;
             """,
            parameters,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
