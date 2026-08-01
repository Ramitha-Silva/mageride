using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.FleetBilling.Billing;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.FleetBilling.Endpoints;

/// <summary>
/// <c>POST /v1/internal/fleet-billing/run</c> — generation, settlement and dunning, on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>One route, and it does what the hourly runner does.</b> Every phase is idempotent, so this is
/// not a second implementation of anything: it is the same three services, driven by an operator or
/// by admin-bff instead of by a timer. It exists because "the platform was not billed for March" is
/// a thing somebody has to be able to fix at 9 a.m. without waiting for a tick or restarting a pod,
/// and because <c>FleetBilling:InvoicingEnabled=false</c> is a deployment shape in which the timer
/// does not run at all.
/// </para>
/// <para>
/// <b>Idempotency-exempt because the operation's key is the month.</b> A header-based guard dedupes
/// identical <em>requests</em>; what has to be single-shot here is the invoice per (fleet, month) and
/// the posting per invoice, and both of those are unique indexes. The same choice C047 made for its
/// two internal fee routes.
/// </para>
/// <para>
/// Protected like every other internal family: mTLS by D3' §0, refused at the gateway edge, and
/// guarded by <c>FleetBilling:InternalApiKey</c> until C042's mesh identity lands. <b>Without the key
/// the route is not mapped at all</b> — it raises invoices and moves money.
/// </para>
/// </remarks>
public static class InternalFleetBillingEndpoints
{
    /// <summary>Carries <c>FleetBilling:InternalApiKey</c>. Replaced by the mesh peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalFleetBillingEndpoints(
        this IEndpointRouteBuilder endpoints, string internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalApiKey);

        var internalGroup = endpoints.MapGroup("/v1/internal/fleet-billing")
            .WithTags("fleet-billing")
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey));

        internalGroup.MapPost("/run", RunAsync).WithName("runFleetBilling");

        return endpoints;
    }

    /// <param name="periodMonth">
    /// <c>yyyy-MM</c> or <c>yyyy-MM-dd</c>. Absent means the current Colombo month, which is what the
    /// timer uses; naming an earlier one is how a month that was missed gets invoiced.
    /// </param>
    /// <param name="fleetId">Narrows the settlement phase to one organisation. Generation is monthly.</param>
    private static async Task<Ok<BillingRunResponse>> RunAsync(
        string? periodMonth,
        string? fleetId,
        IInvoiceRunService generation,
        IInvoiceSettlementService settlement,
        IDunningService dunning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(dunning);

        var period = ParsePeriod(periodMonth) ?? generation.CurrentPeriod();
        var fleet = fleetId is null ? (Guid?)null : RequestIds.Require(fleetId, "fleetId");

        var raised = await generation.RunAsync(period, cancellationToken);
        var settled = await settlement.RunAsync(fleet, cancellationToken);
        var dunned = await dunning.RunAsync(cancellationToken);

        return TypedResults.Ok(new BillingRunResponse(
            period,
            raised.InvoicesRaised,
            raised.LinesAdded,
            settled.Attempted,
            settled.Settled,
            settled.Insufficient,
            dunned.MarkedOverdue,
            dunned.Notified));
    }

    /// <remarks>
    /// Normalised to the first of the month whatever was sent, because
    /// <c>ck_fleet_invoices_period_first_day</c> refuses anything else — and a caller who wrote
    /// <c>2026-07-15</c> meant July.
    /// </remarks>
    private static DateOnly? ParsePeriod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateOnly.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
        {
            return new DateOnly(full.Year, full.Month, 1);
        }

        if (DateTime.TryParseExact(
                value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return new DateOnly(month.Year, month.Month, 1);
        }

        throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["periodMonth"] = ["periodMonth is yyyy-MM or yyyy-MM-dd."],
        });
    }
}

/// <summary>
/// Rejects a call that does not carry <c>FleetBilling:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c> prefix
/// (C008): a caller who is not entitled to the internal plane should not be able to map it.
/// Fixed-time comparison — a length-varying compare leaks the key a character at a time.
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[
            InternalFleetBillingEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
