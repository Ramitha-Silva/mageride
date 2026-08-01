namespace MageRide.AdminBff.Endpoints;

// The wire shapes of backend/contracts/admin-bff.yaml's finance and PDPA families (C065). Every
// property on a request body is nullable on the way in, deliberately: a missing required field has
// to come back as `400 validation-failed` with the field named, not as a framework 400 with no
// error code — the same rule AdminContracts.cs states for the rest of the surface.

// -------------------------------------------------------------------------------------------------
// Gateway settlement reconciliation (D6' §7.2, AL-05)
// -------------------------------------------------------------------------------------------------

/// <summary>`SettlementDay` — one rail's figures for one Asia/Colombo business day (D-38).</summary>
/// <param name="VarianceMinor">
/// <c>settledMinor − postedMinor</c>. Zero is the whole point of the screen: the gateway and the
/// ledger agree. It is computed server-side rather than left to the portal so that "reconciled"
/// means one thing on the screen, in the CSV and in a screenshot pasted into a ticket.
/// </param>
public sealed record SettlementDayResponse(
    DateOnly BusinessDate,
    string Method,
    int OpenedCount,
    int SettledCount,
    int FailedCount,
    int PendingCount,
    long SettledMinor,
    long PostedMinor,
    long VarianceMinor,
    string Currency);

/// <summary>`SettlementSummary` — the window, its rails, and the totals under them.</summary>
public sealed record SettlementSummaryResponse(
    DateOnly From,
    DateOnly To,
    long SettledMinor,
    long PostedMinor,
    long VarianceMinor,
    int ExceptionCount,
    IReadOnlyList<SettlementDayResponse> Days);

/// <summary>`SettlementException` — one gateway session that needs a human.</summary>
public sealed record SettlementExceptionResponse(
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

// -------------------------------------------------------------------------------------------------
// Refunds (E-05, R-19)
// -------------------------------------------------------------------------------------------------

/// <summary>`RefundQueueRow` — a raised refund, or an R-19 `Overpaid` payment nobody has raised one for.</summary>
public sealed record RefundQueueRowResponse(
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

/// <summary>`POST /v1/admin/finance/refunds`. Omit `amountMinor` on a `full` refund.</summary>
public sealed record IssueRefundBody(Guid? PaymentId, string? Kind, long? AmountMinor, string? ReasonCode);

/// <summary>What fare-svc made of it, echoed back.</summary>
public sealed record RefundResponse(Guid RefundId, string Status, long AmountMinor, string Currency);

// -------------------------------------------------------------------------------------------------
// Wallet reversal (US-14.11)
// -------------------------------------------------------------------------------------------------

/// <summary>`POST /v1/admin/drivers/wallet/{driverId}/reverse-fee`.</summary>
public sealed record ReverseFeeBody(DateOnly? FeeDate, Guid? VehicleId, long? AmountMinor, string? Reason);

/// <summary>The compensating entry wallet-svc posted.</summary>
/// <param name="Replayed">
/// True when the ledger key had already been used — a double click, answered with the original entry
/// rather than a second credit. On the wire because an operator pressing the button twice deserves
/// to be told the second press did nothing.
/// </param>
public sealed record ReverseFeeResponse(
    Guid EntryId, long AmountMinor, string Currency, long BalanceAfterMinor, bool Replayed);

// -------------------------------------------------------------------------------------------------
// Transactions report (US-9A.15)
// -------------------------------------------------------------------------------------------------

/// <summary>`TransactionRow` — one money event of the four kinds SCR-AP-006 reports on.</summary>
public sealed record TransactionRowResponse(
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

/// <summary>`TransactionsReport` — the window that was actually applied, and the rows under it.</summary>
public sealed record TransactionsReportResponse(
    DateOnly From,
    DateOnly To,
    string? Kind,
    long TotalMinor,
    IReadOnlyList<TransactionRowResponse> Items);

// -------------------------------------------------------------------------------------------------
// Document expiry (E-03) and fraud review (E-07)
// -------------------------------------------------------------------------------------------------

/// <summary>`DocumentExpiryRow` — one document approaching or past its expiry.</summary>
/// <param name="DaysRemaining">Negative once it has expired, which is what sorts the queue.</param>
/// <param name="LastNoticeDays">
/// The tightest E-03 reminder already sent (30 / 7 / 1 / 0), or absent where none has been. It is
/// the difference between chasing the holder and apologising to them.
/// </param>
/// <param name="ThumbUrl">
/// C063's audited viewer, never a bucket URL: one look at somebody's insurance certificate is one
/// <c>DOC_VIEW</c> row, and a queue that handed out presigned links would be a second door onto the
/// same documents with nothing recording it.
/// </param>
public sealed record DocumentExpiryRowResponse(
    Guid DocId,
    string Kind,
    string Status,
    DateTimeOffset ExpiresAt,
    int DaysRemaining,
    short? LastNoticeDays,
    Guid? DriverId,
    string? DriverName,
    Guid? FleetId,
    string? FleetName,
    Guid? VehicleId,
    string? RegNo,
    string? DispatchState,
    string ThumbUrl,
    string FullUrl);

/// <summary>`FraudFlagRow` — one E-07 signal awaiting review (`fraud.suspected`).</summary>
/// <param name="ResolveUrl">
/// reputation-svc's own decision route. Named here rather than left to the portal because the
/// decision belongs to the service that owns the flag, and a queue that pointed anywhere else would
/// be inviting a second writer.
/// </param>
public sealed record FraudFlagRowResponse(
    Guid FlagId,
    string Kind,
    string Status,
    Guid? SubjectId,
    string? SubjectType,
    string? SubjectName,
    Guid? RelatedId,
    string? RelatedName,
    string? WindowKey,
    System.Text.Json.Nodes.JsonNode? Detail,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset Ts,
    string ResolveUrl);

// -------------------------------------------------------------------------------------------------
// PDPA (E-06)
// -------------------------------------------------------------------------------------------------

/// <summary>The 202 of `POST /v1/pdpa/export` · `/erasure`.</summary>
/// <param name="HoldReasons">
/// Present on an erasure, and a <em>preview</em> rather than a promise: what would be held if the
/// request were fulfilled now. A ride that ends tomorrow lifts one, which is why the list is
/// recomputed at fulfilment rather than stored here.
/// </param>
public sealed record PdpaAcceptedResponse(Guid RequestId, DateTimeOffset DueBy, IReadOnlyList<string>? HoldReasons);

/// <summary>`PdpaRequest` — status, and a short-lived signed download once an export is fulfilled.</summary>
public sealed record PdpaRequestResponse(
    Guid RequestId,
    string Kind,
    string Status,
    Guid SubjectId,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueBy,
    DateTimeOffset? FulfilledAt,
    string? HoldReason,
    string? RejectionReason,
    IReadOnlyList<PdpaHoldResponse> Holds,
    string? DownloadUrl,
    DateTimeOffset? DownloadExpiresAt);

/// <summary>One statutory hold, as a code the app and the portal look a message up by (D-26).</summary>
public sealed record PdpaHoldResponse(string Code, bool Blocking, int Count);

/// <summary>`POST /v1/admin/pdpa/{requestId}/fulfill`.</summary>
public sealed record FulfilPdpaBody(string? Outcome, string? HoldReason, string? ArtifactUrl);
