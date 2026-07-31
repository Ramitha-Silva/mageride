using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Fare.Persistence;

/// <summary>One row of <c>fares.refunds</c> (migration 1003) — the gateway round-trip, not the money.</summary>
public sealed record Refund(
    Guid Id,
    Guid RidePaymentId,
    string Kind,
    long AmountMinor,
    string Currency,
    string Status,
    string? ProviderRefundId,
    string? ReasonCode,
    Guid? RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SettledAt);

/// <summary><c>fares.refunds.kind</c> and <c>.status</c> (migration 1003).</summary>
public static class RefundKinds
{
    public const string Full = "full";
    public const string Partial = "partial";

    /// <summary>R-19: a gateway paid a fare that was already settled in cash.</summary>
    public const string OverpaidReversal = "overpaid_reversal";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Full, Partial, OverpaidReversal,
    };
}

/// <inheritdoc cref="RefundKinds"/>
public static class RefundStatuses
{
    public const string Requested = "Requested";
    public const string Submitted = "Submitted";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>
/// <c>fares.refunds</c> — E-05's refund and dispute workflow.
/// </summary>
/// <remarks>
/// <b>This table is the Finance queue.</b> `ix_refunds_open` (1003) is a partial index over
/// <c>status IN ('Requested','Submitted')</c> ordered by <c>requested_at</c>, and its own comment
/// names it "the Finance Officer refund queue (SCR-AP-009), oldest first". So a refund becoming
/// visible to Finance is a row landing here — not an event, and not a screen this service owns.
/// </remarks>
internal interface IRefundRepository
{
    Task<Refund> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid ridePaymentId,
        string kind,
        long amountMinor,
        string currency,
        string? reasonCode,
        Guid? requestedBy,
        string status,
        CancellationToken cancellationToken);

    /// <summary>Every refund against one payment — how much of it has already been given back.</summary>
    Task<IReadOnlyList<Refund>> ListForPaymentAsync(Guid ridePaymentId, CancellationToken cancellationToken);

    /// <summary>The open queue, oldest first (SCR-AP-009).</summary>
    Task<IReadOnlyList<Refund>> ListOpenAsync(int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRefundRepository"/>
internal sealed class RefundRepository(INpgsqlConnectionFactory connections) : IRefundRepository
{
    private const string Columns =
        """
        id, ride_payment_id, kind, amount_minor::bigint AS amount_minor, currency, status,
        provider_refund_id, reason_code, requested_by, requested_at, settled_at
        """;

    public Task<Refund> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid ridePaymentId,
        string kind,
        long amountMinor,
        string currency,
        string? reasonCode,
        Guid? requestedBy,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // settled_at is set with the status rather than afterwards: 1003 has no CHECK pairing them,
        // so the pairing is this statement's job and a second UPDATE would be a window in which a
        // Succeeded refund had no settlement instant.
        return unitOfWork.Connection.QuerySingleAsync<Refund>(new CommandDefinition(
            $"""
             INSERT INTO fares.refunds
               (ride_payment_id, kind, amount_minor, currency, status, reason_code, requested_by, settled_at)
             VALUES
               (@RidePaymentId, @Kind, @AmountMinor::int, @Currency, @Status, @ReasonCode, @RequestedBy,
                CASE WHEN @Status IN ('{RefundStatuses.Succeeded}', '{RefundStatuses.Failed}')
                     THEN now() ELSE NULL END)
             RETURNING {Columns};
             """,
            new
            {
                RidePaymentId = ridePaymentId,
                Kind = kind,
                AmountMinor = amountMinor,
                Currency = currency,
                Status = status,
                ReasonCode = reasonCode,
                RequestedBy = requestedBy,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Refund>> ListForPaymentAsync(
        Guid ridePaymentId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<Refund>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM fares.refunds
              WHERE ride_payment_id = @RidePaymentId
              ORDER BY requested_at;
             """,
            new { RidePaymentId = ridePaymentId },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<Refund>> ListOpenAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<Refund>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM fares.refunds
              WHERE status IN ('{RefundStatuses.Requested}', '{RefundStatuses.Submitted}')
              ORDER BY requested_at
              LIMIT @Limit;
             """,
            new { Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

/// <summary>
/// The one cross-context write this service makes: an AL-47 driver-QR dispute ticket.
/// </summary>
/// <remarks>
/// <para>
/// <c>support.tickets</c>, one row. 1303 leaves <c>category</c> free text on purpose — support-svc
/// (C053) owns the vocabulary rather than a CHECK an admin cannot edit — and D3' routes this ticket
/// "Support → Finance". What makes it fare-svc's to write is the validation: <b>only fare-svc can
/// say whether a driver-QR payment is actually unresolved</b>, and a ticket raised against a
/// confirmed settlement is a queue item Finance has to close by hand.
/// </para>
/// <para>
/// The lifecycle is not ours. This writes one <c>OPEN</c> row; it never sets <c>status</c>, never
/// writes <c>admin_response</c> and never resolves anything. The same split subscription-svc's
/// fee-refund intake uses, and it becomes a forward to support-svc's ticket route when C053 lands.
/// </para>
/// </remarks>
internal interface ISupportTicketRepository
{
    Task<Guid> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        string category,
        string description,
        Guid rideId,
        string? screenshotUrl,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISupportTicketRepository"/>
internal sealed class SupportTicketRepository : ISupportTicketRepository
{
    /// <summary>
    /// The category a driver-QR dispute carries.
    /// </summary>
    /// <remarks>
    /// Distinct from subscription-svc's <c>daily_fee_refund</c> and from an ordinary fare complaint,
    /// so the Finance queue can tell "the money went bank-to-bank and one party says it did not
    /// arrive" from every other kind of payment question. AL-47's evidence — the claim screenshot —
    /// travels on <c>screenshot_url</c>.
    /// </remarks>
    public const string DriverQrCategory = "driver_qr_dispute";

    public Task<Guid> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        string category,
        string description,
        Guid rideId,
        string? screenshotUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO support.tickets (user_id, category, description, ride_id, screenshot_url)
            VALUES (@UserId, @Category, @Description, @RideId, @ScreenshotUrl)
            RETURNING id;
            """,
            new
            {
                UserId = userId,
                Category = category,
                Description = description,
                RideId = rideId,
                ScreenshotUrl = screenshotUrl,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}
