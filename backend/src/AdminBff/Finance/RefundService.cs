using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;

namespace MageRide.AdminBff.Finance;

/// <summary>What fare-svc made of a refund request (E-05).</summary>
public sealed record RefundOutcome(Guid RefundId, string Status, long AmountMinor, string Currency);

/// <summary>
/// E-05's refund queue and the decision taken from it (ADD §11.14, SCR-AP-006).
/// </summary>
public interface IRefundService
{
    Task<IReadOnlyList<RefundQueueRow>> QueueAsync(
        string? source, string? status, int limit, CancellationToken cancellationToken);

    Task<RefundOutcome> IssueAsync(
        Guid paymentId,
        string kind,
        long? amountMinor,
        string reasonCode,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRefundService"/>
/// <remarks>
/// <para>
/// <b>The queue is this service's read and the decision is fare-svc's write.</b> No service exposes
/// a refund <em>queue</em> — <c>fare.yaml</c> carries only <c>POST /v1/admin/fare/refund</c> — so
/// the list is assembled here, from <c>fares.refunds</c> and R-19's unraised <c>Overpaid</c>
/// payments, joined to the rider the row is about. The execution is forwarded, because
/// <c>fares.refunds</c> and its balanced <c>payment_refund</c> / <c>overpaid_reversal</c> entry are
/// fare-svc's rows and the gateway reverse call is its integration.
/// </para>
/// <para>
/// <b>The operator's own bearer is forwarded, not the shared internal key.</b>
/// <c>POST /v1/admin/fare/refund</c> is a role-gated <c>/v1/admin/**</c> route — the same shape
/// content-svc and transit-svc expose — so fare-svc re-checks the caller. Sending the internal key
/// instead would bypass a check that exists, and in the deployed topology the gateway sends that
/// path straight to fare-svc at Order 20 anyway; what admin-bff adds is the queue the decision is
/// made from and the D-35 row saying a human made it.
/// </para>
/// <para>
/// <b>Two rows for one refund is the right failure.</b> This one records "an operator refunded
/// payment X for reason Y"; fare-svc's records "the refund row went from Requested to Submitted and
/// the ledger moved". Only the second survives this route being renamed, and only the first survives
/// the refund row being purged.
/// </para>
/// </remarks>
internal sealed class RefundService(
    IFinanceRepository finance,
    IAdminUpstream upstream,
    IAdminAuditContext audit,
    ILogger<RefundService> logger) : IRefundService
{
    /// <summary><c>fares.refunds.kind</c> (migration 1003).</summary>
    private static readonly string[] Kinds = ["full", "partial", "overpaid_reversal"];

    /// <summary><c>fares.refunds.status</c> (migration 1003).</summary>
    private static readonly string[] Statuses = ["Requested", "Submitted", "Succeeded", "Failed"];

    /// <summary>The two populations the queue unions.</summary>
    private static readonly string[] Sources = ["refund", "overpaid"];

    public Task<IReadOnlyList<RefundQueueRow>> QueueAsync(
        string? source, string? status, int limit, CancellationToken cancellationToken)
    {
        if (source is not null && !Sources.Contains(source, StringComparer.Ordinal))
        {
            throw Invalid("source", $"source is one of: {string.Join(", ", Sources)}.");
        }

        if (status is not null && !Statuses.Contains(status, StringComparer.Ordinal))
        {
            throw Invalid("status", $"status is one of: {string.Join(", ", Statuses)}.");
        }

        return finance.RefundQueueAsync(source, status, limit, cancellationToken);
    }

    public async Task<RefundOutcome> IssueAsync(
        Guid paymentId,
        string kind,
        long? amountMinor,
        string reasonCode,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw Invalid("kind", $"kind is one of: {string.Join(", ", Kinds)}.");
        }

        // Read before the forward, for the audit row's `before` image and for the two refusals that
        // are cheaper here than a round trip: a payment id that names nothing, and an amount above
        // what was actually collected. fare-svc checks the second as well — it owns the rule — and
        // checking here is what makes the operator's error a sentence about their own screen.
        var payment = await finance.FindPaymentAsync(paymentId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.NotFound, "No payment attempt has that id.");

        // A `full` refund with no amount means the whole payment, which is the only reading that
        // does not require the operator to retype a figure the row already shows them.
        var amount = amountMinor ?? (string.Equals(kind, "partial", StringComparison.Ordinal)
            ? throw Invalid("amountMinor", "a partial refund must say how much.")
            : payment.PaymentAmountMinor);

        if (amount <= 0)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "A refund moves money, so it must be above zero.");
        }

        if (amount > payment.PaymentAmountMinor)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                $"The payment collected {payment.PaymentAmountMinor} and a refund cannot exceed it.");
        }

        using var request = upstream.Request(AdminUpstreams.Fare, HttpMethod.Post, "/v1/admin/fare/refund");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new
            {
                paymentId,
                kind,
                amountMinor = amount,
                currency = payment.Currency,
                reasonCode,
            },
            options: MageRideJson.Options);

        var issued = await upstream.SendAsync<FareRefundResult>(
            AdminUpstreams.Fare, request, context, cancellationToken);

        audit.Record(
            paymentId,
            before: new
            {
                rideId = payment.RideId,
                paymentState = payment.PaymentState,
                method = payment.Method,
                collectedMinor = payment.PaymentAmountMinor,
                currency = payment.Currency,
                passengerId = payment.PassengerId,
                existingRefundId = payment.RefundId,
            },
            after: new
            {
                refundId = issued.RefundId,
                status = issued.Status,
                kind,
                refundedMinor = issued.AmountMinor,
                currency = issued.Currency,
                reasonCode,
            });

        logger.LogInformation(
            "Refund {RefundId} ({Kind}, {Amount} {Currency}) raised against payment {PaymentId} by {ActorId}: "
            + "{ReasonCode}. fare-svc reports {Status}.",
            issued.RefundId, kind, issued.AmountMinor, issued.Currency, paymentId, actorId, reasonCode, issued.Status);

        return new RefundOutcome(issued.RefundId, issued.Status, issued.AmountMinor, issued.Currency);
    }

    private static MageRideValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    /// <summary>fare.yaml's 201 body.</summary>
    private sealed record FareRefundResult(Guid RefundId, string Status, long AmountMinor, string Currency);
}
