using System.Security.Cryptography;
using System.Text;
using MageRide.Payout.Domain;
using MageRide.Payout.Payouts;
using MageRide.Payout.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Payout.Endpoints;

/// <summary>`Payout` — one instruction as SCR-DA-022a and SCR-AP-006 render it.</summary>
public sealed record PayoutResponse(
    Guid PayoutId,
    Guid BatchId,
    Guid DriverId,
    long AmountMinor,
    string Currency,
    string Status,
    string? AccountNoMasked,
    string? FailureReason,
    string? ProviderReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt);

/// <summary>`PayoutBatch` — one weekly sweep.</summary>
public sealed record PayoutBatchResponse(
    Guid BatchId,
    DateOnly RunDate,
    DateTimeOffset TzAt,
    string Status,
    int InstructionCount,
    long TotalMinor,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>`POST /v1/internal/payouts/{payoutId}/result` — the bank adapter reporting back.</summary>
public sealed record PayoutResultBody(string? Status, string? ProviderReference, string? FailureReason);

/// <summary>
/// The driver's own payout history, Finance's view of every instruction, and the manual run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Finance, not admin.</b> URD §2.3's Finance row is where money leaving the platform belongs;
/// an Admin who may configure a tariff has no business releasing a week's payouts. The driver's own
/// history is scoped to the caller by <c>SubjectScope</c>, exactly as their wallet is.
/// </para>
/// <para>
/// <b>There is no retry route, and the contract lost one.</b> `payout.yaml` declared
/// <c>POST /v1/admin/payouts/{id}/retry</c>; implementing AL-58 showed it to be incoherent with the
/// rule beside it. A <c>FAILED</c> instruction has <em>already</em> had its debit reversed — the
/// money is back on the driver's wallet — so there is nothing left to re-submit, and the next
/// weekly run sweeps the restored balance. Where an operator genuinely needs to pay somebody before
/// Sunday, <c>POST /v1/admin/payouts/batches</c> is that capability and is idempotent on the date.
/// Raised as a contract correction in the C133 handoff.
/// </para>
/// </remarks>
internal static class PayoutEndpoints
{
    public static IEndpointRouteBuilder MapPayoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var drivers = endpoints.MapGroup("/v1/drivers/payouts").WithTags("payouts").RequireAuthorization();

        drivers.MapGet(string.Empty, ListForDriverAsync).WithName("listDriverPayouts");

        var admin = endpoints.MapGroup("/v1/admin/payouts").WithTags("payout-admin").RequireAuthorization();

        admin.MapGet(string.Empty, ListAsync)
            .WithName("listPayouts")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapGet("/batches", ListBatchesAsync)
            .WithName("listPayoutBatches")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Read);

        admin.MapPost("/batches", RunAsync)
            .WithName("runPayoutBatch")
            .RequireFeature(FeatureAreas.Finance, PermissionGrant.Write);

        return endpoints;
    }

    /// <remarks>
    /// A driver with no verified payout profile sees an empty page. That is the honest answer — they
    /// have never been swept — and SCR-DA-022a is where the screen explains why: the money is on
    /// their wallet, and the profile is what releases it.
    /// </remarks>
    private static async Task<Ok<CursorPage<PayoutResponse>>> ListForDriverAsync(
        int? limit,
        HttpContext context,
        IPayoutRepository payouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payouts);

        var page = PageRequest.Create(null, limit);
        var driverId = context.User.RequireSubjectId();

        var rows = await payouts.ListForDriverAsync(driverId, page.Limit, cancellationToken);

        return TypedResults.Ok(new CursorPage<PayoutResponse>([.. rows.Select(ToResponse)], null, false));
    }

    private static async Task<Ok<CursorPage<PayoutResponse>>> ListAsync(
        Guid? batchId,
        string? status,
        Guid? driverId,
        int? limit,
        IPayoutRepository payouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payouts);

        if (status is not null && !PayoutStatuses.All.Contains(status))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = [$"status must be one of {string.Join(", ", PayoutStatuses.All.Order(StringComparer.Ordinal))}."],
            });
        }

        var page = PageRequest.Create(null, limit);
        var rows = await payouts.ListAsync(batchId, status, driverId, page.Limit, cancellationToken);

        return TypedResults.Ok(new CursorPage<PayoutResponse>([.. rows.Select(ToResponse)], null, false));
    }

    private static async Task<Ok<CursorPage<PayoutBatchResponse>>> ListBatchesAsync(
        int? limit, IPayoutRepository payouts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payouts);

        var page = PageRequest.Create(null, limit);
        var rows = await payouts.ListBatchesAsync(page.Limit, cancellationToken);

        return TypedResults.Ok(new CursorPage<PayoutBatchResponse>([.. rows.Select(ToResponse)], null, false));
    }

    /// <remarks>
    /// Idempotent on the Colombo business date rather than on the header: the sweep pays a driver's
    /// <em>whole</em> balance, so a second run the same day would raise an empty instruction for
    /// every driver it had just emptied. `409 payout-batch-exists` says so.
    /// </remarks>
    private static async Task<Accepted<PayoutBatchResponse>> RunAsync(
        PayoutRunService run, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var result = await run.RunAsync(BusinessCalendar.Today(clock), force: true, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.PayoutBatchExists, "This date has already been swept.");

        return TypedResults.Accepted((string?)null, ToResponse(result.Batch));
    }

    internal static PayoutResponse ToResponse(PayoutInstruction row) => new(
        row.Id,
        row.BatchId,
        row.DriverId,
        row.AmountMinor,
        "LKR",
        row.Status,
        row.AccountNoMasked,
        row.FailureReason,
        row.ProviderReference,
        row.CreatedAt,
        row.Status is PayoutStatuses.Paid or PayoutStatuses.Failed ? row.UpdatedAt : null);

    internal static PayoutBatchResponse ToResponse(PayoutBatch row) => new(
        row.Id,
        row.RunDate,
        row.TzAt,
        row.Status,
        row.InstructionCount,
        row.TotalMinor,
        row.StartedAt,
        row.CompletedAt);
}

/// <summary>
/// <c>/v1/internal/payouts/{payoutId}/result</c> — the bank origination adapter reporting back.
/// </summary>
/// <remarks>
/// D3' §0 puts the whole <c>/v1/internal/**</c> family on service-to-service mTLS and the gateway
/// refuses the prefix at the edge (C008). Until a mesh exists the hop is guarded by a shared secret;
/// <b>without <c>Payout:InternalApiKey</c> the route is not mapped at all</b>, so a deployment that
/// forgets it gets 404s rather than an open door onto marking somebody's money paid.
/// </remarks>
internal static class InternalPayoutEndpoints
{
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalPayoutEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a bank adapter, not a user; the filter authenticates
        // it. Idempotency-exempt because an external caller cannot mint our header — the result
        // dedupes on `provider_reference` and on the guarded status transition (R-19's shape).
        var internalPayouts = endpoints.MapGroup("/v1/internal/payouts")
            .WithTags("payout-internal")
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .AddEndpointFilter(new PayoutInternalKeyFilter(apiKey));

        internalPayouts.MapPost("/{payoutId:guid}/result", ReportAsync).WithName("reportPayoutResult");

        return endpoints;
    }

    private static async Task<Ok<PayoutResponse>> ReportAsync(
        Guid payoutId,
        PayoutResultBody? body,
        PayoutRunService run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var settled = await run.ReportAsync(
            payoutId, body?.Status?.Trim().ToUpperInvariant() ?? string.Empty, body?.FailureReason, cancellationToken);

        return TypedResults.Ok(PayoutEndpoints.ToResponse(settled));
    }
}

/// <summary>Refuses a call that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. The same filter every other internal
/// plane on this platform carries.
/// </remarks>
internal sealed class PayoutInternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalPayoutEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
