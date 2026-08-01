namespace MageRide.AdminBff.Domain;

// The rows FinanceRepository materialises. Separate from the wire shapes in
// Endpoints/FinanceContracts.cs for the same reason every other family here is: a repository row
// carries the paging key and the raw column values, and a response carries what the contract says.

/// <summary>
/// One rail's settlement for one Asia/Colombo business day (D6' §7.2, D-38).
/// </summary>
/// <param name="Method"><c>onepay</c> or <c>lankaqr</c> — <c>ck_topups_method</c>'s two values (AL-05).</param>
/// <param name="SettledMinor">What the gateway confirmed, summed off the sessions it settled.</param>
/// <param name="PostedMinor">
/// What reached the ledger, summed off the credit leg of each session's own journal entry. The
/// difference between the two is the reconciliation: they agree or somebody has to find out why.
/// </param>
public sealed record SettlementDayRow(
    DateOnly BusinessDate,
    string Method,
    int OpenedCount,
    int SettledCount,
    int FailedCount,
    int PendingCount,
    long SettledMinor,
    long PostedMinor,
    string Currency);

/// <summary>One gateway session that needs a human (D6' §7.2's "exceptions → Finance queue").</summary>
/// <param name="Kind">One of <see cref="SettlementExceptionKinds"/>.</param>
/// <param name="PostedMinor">
/// The ledger's figure where there is one. Null means nothing was posted, which is itself the
/// exception on a settled session.
/// </param>
public sealed record SettlementExceptionRow(
    Guid TopupId,
    string Kind,
    string Method,
    string State,
    Guid DriverId,
    string? DriverName,
    long AmountMinor,
    long? PostedMinor,
    string Currency,
    string? ProviderTransactionId,
    string? ProviderOrderId,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt);

/// <summary>
/// Why a gateway session is in the exception queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four classes, each derived from the row rather than stored on it.</b> wallet-svc records no
/// "exception" column — on an amount mismatch it logs and leaves the session <c>Pending</c>
/// (<c>TopupService.SettleAsync</c>) — so the queue's job is to name what the state actually means.
/// Deriving it also means a session that resolves itself leaves the queue with no second write.
/// </para>
/// <para>
/// <b>The two that are really the same failure are kept apart anyway.</b> A lost callback and a
/// refused amount mismatch both leave a session open past the window and the schema cannot tell
/// them apart — so both are <see cref="Unsettled"/>, and the operator is told that is what it means
/// rather than being shown a guess. What <em>is</em> separable is a session the ledger and the
/// gateway disagree about (<see cref="AmountMismatch"/>), which is the one this queue exists to
/// catch and the one no poll or retry will ever fix.
/// </para>
/// </remarks>
public static class SettlementExceptionKinds
{
    /// <summary>
    /// Settled, posted, and the ledger's credit leg is not the amount the gateway confirmed.
    /// </summary>
    public const string AmountMismatch = "amount-mismatch";

    /// <summary>Settled and no journal entry — money the payer parted with and no wallet moved.</summary>
    public const string SettledNotPosted = "settled-not-posted";

    /// <summary>
    /// Still open past <c>AdminBff:Finance:SettlementGracePeriod</c>: a callback that never arrived,
    /// or one whose amount disagreed with the session and was refused.
    /// </summary>
    public const string Unsettled = "unsettled";

    /// <summary>The gateway reported FAILED after issuing a transaction id of its own.</summary>
    public const string GatewayFailed = "gateway-failed";

    public static readonly IReadOnlyList<string> All =
        [AmountMismatch, SettledNotPosted, Unsettled, GatewayFailed];

    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// One row of the refund queue (E-05 and R-19's <c>Overpaid</c>).
/// </summary>
/// <param name="Source">
/// <c>refund</c> — a <c>fares.refunds</c> row already raised and awaiting settlement — or
/// <c>overpaid</c>, a payment §11.14 moved to <c>Overpaid</c> that nobody has raised a refund for
/// yet. Two populations on one screen because they are one operator's queue, and the field is what
/// tells the portal whether the row's button says "chase" or "refund".
/// </param>
public sealed record RefundQueueRow(
    Guid? RefundId,
    string Source,
    Guid PaymentId,
    Guid RideId,
    string PaymentState,
    string Method,
    string? Kind,
    string? Status,
    long AmountMinor,
    long PaymentAmountMinor,
    string Currency,
    string? ReasonCode,
    string? ProviderRefundId,
    Guid? PassengerId,
    string? PassengerName,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SettledAt);

/// <summary>One money event of the four SCR-AP-006 reports on (US-9A.15).</summary>
/// <param name="FromName">Owner of the negative leg — who paid. Null for the platform's own account.</param>
/// <param name="ToName">Owner of the positive leg — who received.</param>
public sealed record TransactionRow(
    Guid EntryId,
    string Kind,
    long AmountMinor,
    string Currency,
    Guid? FromPartyId,
    string? FromName,
    string FromAccountType,
    Guid? ToPartyId,
    string? ToName,
    string ToAccountType,
    string? Description,
    DateTimeOffset Ts);

/// <summary>The four <c>billing.journal_entries.kind</c> values the transactions report covers.</summary>
/// <remarks>
/// <b>Exactly the deliverable's list and nothing else.</b> The ledger admits twelve kinds; a report
/// that showed all of them would put a trip payment, a penalty settlement and a weekly payout on a
/// screen whose column headings describe none of the three. The other eight have their own surfaces
/// — the driver directory's wallet tab (C064), the payout run (C133), the fleet invoice (C060).
/// </remarks>
public static class TransactionKinds
{
    public const string Topup = "topup";
    public const string DailyFee = "daily_fee";
    public const string VoucherPurchase = "voucher_purchase";
    public const string DriverTransfer = "driver_transfer";

    public static readonly IReadOnlyList<string> All = [Topup, DailyFee, VoucherPurchase, DriverTransfer];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>One document approaching or past its expiry (E-03, AL-10).</summary>
/// <param name="ThresholdDays">
/// The tightest E-03 notice already emitted for this document (30 / 7 / 1 / 0), or null when none
/// has been. It is what tells an operator whether the holder has been warned, which is the
/// difference between chasing them and apologising to them.
/// </param>
public sealed record DocumentExpiryRow(
    Guid DocId,
    string Kind,
    string Status,
    DateTimeOffset ExpiresAt,
    int DaysRemaining,
    short? ThresholdDays,
    Guid? DriverId,
    string? DriverName,
    Guid? FleetId,
    string? FleetName,
    Guid? VehicleId,
    string? RegNo,
    string? DispatchState);

/// <summary>One E-07 anti-collusion signal awaiting review (<c>fraud.suspected</c>).</summary>
public sealed record FraudFlagRow(
    Guid FlagId,
    string Kind,
    string Status,
    Guid? SubjectId,
    string? SubjectType,
    string? SubjectName,
    Guid? RelatedId,
    string? RelatedName,
    string? WindowKey,
    string? Detail,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset Ts);
