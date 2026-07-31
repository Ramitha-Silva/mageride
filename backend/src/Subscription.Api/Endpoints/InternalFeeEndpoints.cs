using MageRide.Shared.Http.Idempotency;
using MageRide.Subscriptions.Fees;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Subscriptions.Endpoints;

/// <summary>
/// <c>/v1/internal/fees</c> — the D-08/D-13 gate ride-svc calls, and the Mode B monthly run.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>charge-before-trip</c> is the only route on this platform that charges the daily fee</b>, and
/// D3' §325 has ride-svc call it during offer acceptance, immediately after the conditional
/// <c>UPDATE … AND version = :v</c> that wins the offer. Everything it does is idempotent on
/// <c>(driverId, vehicleId, feeDate)</c> in Asia/Colombo, so a redelivery, a retry and a second replica
/// all land on the same single deduction.
/// </para>
/// <para>
/// <b>Idempotency-exempt because the natural key is stronger than a header.</b> The composite key is
/// the primary key of <c>billing.daily_fee_charges</c> and the ledger key is UNIQUE in
/// <c>billing.journal_entries</c>; a header-based guard over the same money would be weaker — it would
/// dedupe identical *requests* rather than identical *days*, so two differently-keyed calls for one
/// Colombo day would both pass it. The <c>Idempotency-Key</c> the contract declares is accepted and
/// ignored, which is what lets a caller send one uniformly.
/// </para>
/// <para>
/// Protected like every other internal family: mTLS by D3' §0, refused at the gateway edge, and guarded
/// by <c>Subscription:InternalApiKey</c> until C042's mesh identity lands. <b>Without the key these
/// routes are not mapped at all</b> — following wallet-svc rather than content-svc, because a caller
/// who can reach this one can charge a driver's wallet.
/// </para>
/// </remarks>
public static class InternalFeeEndpoints
{
    public static IEndpointRouteBuilder MapInternalFeeEndpoints(
        this IEndpointRouteBuilder endpoints, string internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalApiKey);

        // AllowAnonymous because the caller is a service and presents no bearer; the filter is what
        // authenticates it, and the kernel's deny-by-default fallback would otherwise 401 before the
        // filter ran.
        var internalGroup = endpoints.MapGroup("/v1/internal/fees")
            .WithTags("fees")
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey));

        internalGroup.MapPost("/{driverId}/charge-before-trip", ChargeBeforeTripAsync)
            .WithName("chargeDailyFeeBeforeTrip");

        internalGroup.MapPost("/mode-b/run", RunModeBAsync).WithName("runModeBMonthlyCharge");

        return endpoints;
    }

    private static async Task<Ok<DailyFeeChargeResponse>> ChargeBeforeTripAsync(
        string driverId,
        ChargeBeforeTripBody? body,
        DailyFeeService fees,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fees);

        var driver = RequestIds.Require(driverId, "driverId");
        var vehicle = RequestIds.Require(body?.VehicleId, "vehicleId");

        var charge = await fees.ChargeBeforeTripAsync(
            driver, vehicle, RequestIds.Optional(body?.RideId), cancellationToken);

        return TypedResults.Ok(new DailyFeeChargeResponse(
            charge.DriverId,
            charge.VehicleId,
            charge.FeeDate,
            charge.FeeDateTzAt,
            charge.AmountMinor,
            charge.Currency,
            charge.TripsThatDay,
            charge.Status,
            charge.ChargedAt));
    }

    /// <summary>
    /// Raises the Mode B per-vehicle platform charge for a Colombo month (AL-03).
    /// </summary>
    /// <remarks>
    /// The background runner does this hourly; the route exists so fleet-billing-svc (C060) can force
    /// the month it is about to invoice rather than racing a timer, and so an operator can catch up a
    /// month after an outage. Idempotent by <c>(vehicle_id, period_month)</c> — running it twice raises
    /// nothing the second time.
    /// </remarks>
    private static async Task<Ok<ModeBRunResponse>> RunModeBAsync(
        string? month,
        ModeBBillingService billing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(billing);

        var period = month is null
            ? billing.CurrentPeriod()
            : BusinessDates.Require(month, "month");

        var result = await billing.RunAsync(period, cancellationToken);

        return TypedResults.Ok(new ModeBRunResponse(
            result.PeriodMonth, result.Raised, result.FreeMonths, result.TotalMinor, Currencies.Lkr));
    }
}
