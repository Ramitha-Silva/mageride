using System.Globalization;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Finance;
using MageRide.AdminBff.Reporting;
using MageRide.AdminBff.Verification;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-006 — finance and reconciliation, plus the two review queues the ADD files beside it
/// (US-9A.15, US-14.11, E-05, R-19, E-03, E-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Four URD §2.3 rows, and each is the row whose cells are the answer to a different question.</b>
/// Gating the whole family on one would be wrong four ways:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Reconciliation and the transactions report → Finance · Read.</b> The row is
/// "Finance — payouts, settlements, reconciliation, wallet reversals/adjustments" in as many words:
/// ADM 👁, S.ADM ✅, FIN ✅, AUD 👁, and ➖ for the CSR and the Verification Officer. That last pair
/// is the point — a Support CSR investigating a ticket has the passenger directory (C064) and has no
/// business reading the platform's settlement position.
/// </item>
/// <item>
/// <b>The fee reversal → Driver wallet adjustments / reversals · Write.</b> The one row on the
/// matrix that exists solely for this button, and the only one whose cells are ✅ for exactly
/// <b>Super Admin and Finance</b> — Admin is 👁, Auditor is 👁, everybody else ➖. So C065's fence
/// ("Finance/Super-Admin only, always audited, always ledger-balanced") is not enforced by a role
/// list written here; it is what the matrix already says, which is why no <c>◐</c> fence is needed
/// on this route: there is no <c>◐</c> in the row.
/// </item>
/// <item>
/// <b>Refunds → Refunds · Read / Write.</b> A row of its own, and the CSR's cell is the reason:
/// <c>◐ raise/recommend</c>, which <c>PermissionCell.Parse</c> turns into Read + Raise <em>minus</em>
/// Write. So a CSR sees the queue and cannot execute — which is exactly what the row's other half
/// (<c>FIN ✅ approve/execute</c>) is written against, and it falls out of the matrix without a
/// platform-wide fence. <b>Admin holds ✅ here and 👁 on the reversal row</b>, and the two are
/// deliberately different: refunding a passenger their own fare is not the same authority as putting
/// credit into a driver's wallet.
/// </item>
/// <item>
/// <b>The document-expiry queue → Onboarding/Verification · Read</b>, because an expiring insurance
/// certificate is a document review and E-03's expiry is what AL-10's approval gate turns on.
/// <b>The fraud queue → Moderation · Read</b>, because what a confirmed E-07 signal leads to is a
/// suspension. Neither is a finance question; both are on this component because the ADD's admin-bff
/// row lists the three queues together, and both are gated where they belong rather than where they
/// were built.
/// </item>
/// </list>
/// <para>
/// <b>Two writes on this whole surface, and neither of them writes.</b> The reversal is posted by
/// wallet-svc and the refund by fare-svc — see <see cref="IWalletAdjustmentService"/> and
/// <see cref="IRefundService"/> for why. What admin-bff contributes is the RBAC gate, the queue the
/// decision is made from, and the D-35 row saying a human made it.
/// </para>
/// </remarks>
internal static class FinanceEndpoints
{
    /// <summary>How many rows a queue or a report returns when the caller names no limit.</summary>
    /// <remarks>
    /// A constant rather than a setting, for C064's reason: a knob would be a promise this component
    /// can serve an unbounded read, and <c>billing.journal_entries</c> is the largest table on the
    /// platform. 200 fills a screen and a CSV of a normal day; the export routes take the same
    /// ceiling and say so in the file's own preamble when they hit it.
    /// </remarks>
    private const int DefaultRows = 200;

    private const int MaxRows = 2000;

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/finance/reconciliation", GetReconciliationAsync)
            .WithName("getSettlementReconciliation")
            .WithSummary("OnePay/LankaQR settlement against the ledger, per rail per day (D6' §7.2).")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/finance/reconciliation/exceptions", GetSettlementExceptionsAsync)
            .WithName("listSettlementExceptions")
            .WithSummary("Gateway sessions that need a human — mismatches, unposted and unsettled.")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/finance/refunds", ListRefundQueueAsync)
            .WithName("listRefundQueue")
            .WithSummary("The refund queue, including R-19's Overpaid payments (ADD §11.14).")
            .RequireFeature(FeatureAreas.Refunds, PermissionGrant.Read);

        admin.MapPost("/finance/refunds", IssueRefundAsync)
            .WithName("issueRefund")
            .WithSummary("Raise a full, partial or overpaid reversal through fare-svc (E-05).")
            .RequireFeature(FeatureAreas.Refunds, PermissionGrant.Write)
            .Audited(AdminAuditActions.RefundIssued, AdminAuditActions.PaymentEntity);

        admin.MapPost("/drivers/wallet/{driverId:guid}/reverse-fee", ReverseFeeAsync)
            .WithName("reverseDriverFee")
            .WithSummary("Reverse a daily-fee deduction onto a driver's wallet (US-14.11).")
            .RequireFeature(FeatureAreas.DriverWalletAdjustments, PermissionGrant.Write)
            .Audited(AdminAuditActions.FeeReversed, AdminAuditActions.WalletEntity);

        admin.MapGet("/finance/transactions", GetTransactionsAsync)
            .WithName("listWalletTransactions")
            .WithSummary("Top-ups, daily fees, voucher purchases and credit transfers (US-9A.15).")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/finance/transactions.csv", ExportTransactionsCsvAsync)
            .WithName("exportWalletTransactionsCsv")
            .WithSummary("The same rows as a CSV download.")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/finance/transactions.pdf", ExportTransactionsPdfAsync)
            .WithName("exportWalletTransactionsPdf")
            .WithSummary("The same rows as a paginated PDF table.")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/documents/expiring", ListDocumentExpiryAsync)
            .WithName("listDocumentExpiryQueue")
            .WithSummary("Documents inside E-03's notice horizon, or already expired.")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapGet("/fraud/queue", ListFraudQueueAsync)
            .WithName("listFraudReviewQueue")
            .WithSummary("E-07 anti-collusion signals awaiting review (fraud.suspected).")
            .RequireFeature(FeatureAreas.Moderation, PermissionGrant.Read);

        return admin;
    }

    // ---------------------------------------------------------------------------------------
    // Reconciliation
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The exception <em>count</em> travels with the summary because SCR-AP-006 draws the badge on
    /// the reconciliation tab and would otherwise need a second round trip to know whether to draw
    /// it. It is the same query the queue route runs, capped — a count of "how many are waiting" is
    /// worth nothing beyond the point where the answer is "more than a screenful".
    /// </remarks>
    private static async Task<Ok<SettlementSummaryResponse>> GetReconciliationAsync(
        DateOnly? from,
        DateOnly? to,
        string? method,
        IFinanceService finance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);

        var settlement = await finance.SettlementAsync(from, to, method, cancellationToken);
        var exceptions = await finance.SettlementExceptionsAsync(kind: null, MaxRows, cancellationToken);

        var rows = settlement.Days
            .Select(day => new SettlementDayResponse(
                day.BusinessDate,
                day.Method,
                day.OpenedCount,
                day.SettledCount,
                day.FailedCount,
                day.PendingCount,
                day.SettledMinor,
                day.PostedMinor,
                day.SettledMinor - day.PostedMinor,
                day.Currency ?? "LKR"))
            .ToArray();

        return TypedResults.Ok(new SettlementSummaryResponse(
            settlement.Window.From,
            settlement.Window.To,
            rows.Sum(row => row.SettledMinor),
            rows.Sum(row => row.PostedMinor),
            rows.Sum(row => row.VarianceMinor),
            exceptions.Count,
            rows));
    }

    private static async Task<Ok<IReadOnlyList<SettlementExceptionResponse>>> GetSettlementExceptionsAsync(
        string? kind,
        int? limit,
        IFinanceService finance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);

        var rows = await finance.SettlementExceptionsAsync(kind, Rows(limit), cancellationToken);

        return TypedResults.Ok<IReadOnlyList<SettlementExceptionResponse>>(
        [
            .. rows.Select(row => new SettlementExceptionResponse(
                row.TopupId,
                row.Kind,
                row.Method,
                row.State,
                row.DriverId,
                row.DriverName,
                row.AmountMinor,
                row.PostedMinor,
                row.Currency,
                row.ProviderTransactionId,
                row.ProviderOrderId,
                row.FailureReason,
                row.CreatedAt,
                row.SettledAt)),
        ]);
    }

    // ---------------------------------------------------------------------------------------
    // Refunds
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<RefundQueueRowResponse>>> ListRefundQueueAsync(
        string? source,
        string? status,
        int? limit,
        IRefundService refunds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refunds);

        var rows = await refunds.QueueAsync(source, status, Rows(limit), cancellationToken);

        return TypedResults.Ok<IReadOnlyList<RefundQueueRowResponse>>(
        [
            .. rows.Select(row => new RefundQueueRowResponse(
                row.RefundId,
                row.Source,
                row.PaymentId,
                row.RideId,
                row.PaymentState,
                row.Method,
                row.Kind,
                row.Status,
                row.AmountMinor,
                row.PaymentAmountMinor,
                row.Currency,
                row.ReasonCode,
                row.ProviderRefundId,
                row.PassengerId,
                row.PassengerName,
                row.RequestedAt,
                row.SettledAt)),
        ]);
    }

    private static async Task<Ok<RefundResponse>> IssueRefundAsync(
        IssueRefundBody? body,
        HttpContext context,
        IRefundService refunds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(refunds);

        var paymentId = body?.PaymentId
            ?? throw Invalid("paymentId", "paymentId is required — a refund is raised against a payment attempt.");

        var kind = body.Kind?.Trim();

        if (string.IsNullOrEmpty(kind))
        {
            throw Invalid("kind", "kind is one of: full, partial, overpaid_reversal.");
        }

        var reasonCode = body.ReasonCode?.Trim();

        if (string.IsNullOrEmpty(reasonCode))
        {
            // Not optional. A refund with no recorded reason is one nobody can explain to the driver
            // whose earning it reverses, which is the half of D-35 the audit row cannot supply.
            throw Invalid("reasonCode", "reasonCode is required and is recorded on the refund and in the audit log.");
        }

        var outcome = await refunds.IssueAsync(
            paymentId, kind, body.AmountMinor, reasonCode, context.User.RequireSubjectId(), context, cancellationToken);

        return TypedResults.Ok(
            new RefundResponse(outcome.RefundId, outcome.Status, outcome.AmountMinor, outcome.Currency));
    }

    // ---------------------------------------------------------------------------------------
    // Wallet reversal
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<ReverseFeeResponse>> ReverseFeeAsync(
        Guid driverId,
        ReverseFeeBody? body,
        HttpContext context,
        IWalletAdjustmentService wallet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wallet);

        var feeDate = body?.FeeDate
            ?? throw Invalid("feeDate", "feeDate is required — a reversal compensates one Colombo business day (D-13).");

        var vehicleId = body.VehicleId
            ?? throw Invalid("vehicleId", "vehicleId is required — the daily fee is charged per driver per vehicle.");

        var reason = body.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            throw Invalid("reason", "reason is required and is recorded in the audit log.");
        }

        var outcome = await wallet.ReverseFeeAsync(
            driverId, vehicleId, feeDate, body.AmountMinor, reason,
            context.User.RequireSubjectId(), context, cancellationToken);

        return TypedResults.Ok(new ReverseFeeResponse(
            outcome.EntryId, outcome.AmountMinor, outcome.Currency, outcome.BalanceAfterMinor, outcome.Replayed));
    }

    // ---------------------------------------------------------------------------------------
    // Transactions report
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<TransactionsReportResponse>> GetTransactionsAsync(
        DateOnly? from,
        DateOnly? to,
        string? kind,
        Guid? partyId,
        int? limit,
        IFinanceService finance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);

        var result = await finance.TransactionsAsync(from, to, kind, partyId, Rows(limit), cancellationToken);

        return TypedResults.Ok(new TransactionsReportResponse(
            result.Window.From,
            result.Window.To,
            result.Kind,
            result.Rows.Sum(row => row.AmountMinor),
            [.. result.Rows.Select(Map)]));
    }

    private static Task<IResult> ExportTransactionsCsvAsync(
        DateOnly? from,
        DateOnly? to,
        string? kind,
        Guid? partyId,
        int? limit,
        IFinanceService finance,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        ExportAsync(from, to, kind, partyId, limit, finance, clock, "csv", cancellationToken);

    private static Task<IResult> ExportTransactionsPdfAsync(
        DateOnly? from,
        DateOnly? to,
        string? kind,
        Guid? partyId,
        int? limit,
        IFinanceService finance,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        ExportAsync(from, to, kind, partyId, limit, finance, clock, "pdf", cancellationToken);

    /// <remarks>
    /// <b>Both files come from the same query the JSON route runs</b>, so "the export matches the
    /// screen" is structural rather than a coincidence two queries happen to share — C061's CSV is
    /// written under the same rule. The filename carries the window because a folder of
    /// <c>transactions.csv</c> files is a folder nobody can tell apart.
    /// </remarks>
    private static async Task<IResult> ExportAsync(
        DateOnly? from,
        DateOnly? to,
        string? kind,
        Guid? partyId,
        int? limit,
        IFinanceService finance,
        TimeProvider clock,
        string format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);
        ArgumentNullException.ThrowIfNull(clock);

        var result = await finance.TransactionsAsync(from, to, kind, partyId, Rows(limit), cancellationToken);
        var now = clock.GetUtcNow();

        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"mageride-transactions-{result.Window.From:yyyyMMdd}-{result.Window.To:yyyyMMdd}.{format}");

        return format switch
        {
            "csv" => Results.File(
                TransactionExport.RenderCsv(result.Window, result.Kind, result.Rows, now),
                "text/csv; charset=utf-8",
                name),
            _ => Results.File(
                TransactionExport.RenderPdf(result.Window, result.Kind, result.Rows, now),
                "application/pdf",
                name),
        };
    }

    private static TransactionRowResponse Map(TransactionRow row) => new(
        row.EntryId,
        row.Kind,
        row.AmountMinor,
        row.Currency,
        row.FromPartyId,
        row.FromName,
        row.FromAccountType,
        row.ToPartyId,
        row.ToName,
        row.ToAccountType,
        row.Description,
        row.Ts);

    // ---------------------------------------------------------------------------------------
    // The two review queues
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<DocumentExpiryRowResponse>>> ListDocumentExpiryAsync(
        int? withinDays,
        string? kind,
        int? limit,
        IFinanceService finance,
        IDocumentLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);
        ArgumentNullException.ThrowIfNull(links);

        var rows = await finance.DocumentExpiryQueueAsync(withinDays, kind, Rows(limit), cancellationToken);

        return TypedResults.Ok<IReadOnlyList<DocumentExpiryRowResponse>>(
        [
            .. rows.Select(row => new DocumentExpiryRowResponse(
                row.DocId,
                row.Kind,
                row.Status,
                row.ExpiresAt,
                row.DaysRemaining,
                row.ThresholdDays,
                row.DriverId,
                row.DriverName,
                row.FleetId,
                row.FleetName,
                row.VehicleId,
                row.RegNo,
                row.DispatchState,
                links.Create(row.DocId, DocumentVariants.Thumb),
                links.Create(row.DocId, DocumentVariants.Full))),
        ]);
    }

    private static async Task<Ok<IReadOnlyList<FraudFlagRowResponse>>> ListFraudQueueAsync(
        string? status,
        string? kind,
        int? limit,
        IFinanceService finance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finance);

        var rows = await finance.FraudQueueAsync(status, kind, Rows(limit), cancellationToken);

        return TypedResults.Ok<IReadOnlyList<FraudFlagRowResponse>>(
        [
            .. rows.Select(row => new FraudFlagRowResponse(
                row.FlagId,
                row.Kind,
                row.Status,
                row.SubjectId,
                row.SubjectType,
                row.SubjectName,
                row.RelatedId,
                row.RelatedName,
                row.WindowKey,
                // Re-emitted as the JSON reputation-svc's detector stored rather than reshaped: the
                // heuristics are expected to grow (0802's own comment) and a CLR shape here would
                // drop every field a new detector adds.
                row.Detail is null ? null : System.Text.Json.Nodes.JsonNode.Parse(row.Detail),
                row.ResolvedBy,
                row.ResolvedAt,
                row.Ts,
                $"/v1/admin/reputation/flags/{row.FlagId:D}/resolve")),
        ]);
    }

    // ---------------------------------------------------------------------------------------

    private static int Rows(int? limit) => limit is null or < 1 ? DefaultRows : Math.Min(limit.Value, MaxRows);

    private static MageRideValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
