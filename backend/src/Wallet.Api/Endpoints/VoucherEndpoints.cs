using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Wallet.Domain;
using MageRide.Wallet.Money;
using MageRide.Wallet.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Wallet.Endpoints;

/// <summary>
/// The bulk-voucher ladder and the purchase that credits a driver's own wallet (US-9.19, US-9A.15).
/// </summary>
/// <remarks>
/// <b>The discount is the whole of AL-01's "reseller margin".</b> A driver buys Rs 1,000 of credit for
/// Rs 900 and hands it on at par, because a transfer moves the exact value. There is no reseller role to
/// grant, no commission to configure per driver, and no fee leg the ledger could record.
/// </remarks>
public static class VoucherEndpoints
{
    public static IEndpointRouteBuilder MapVoucherEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var wallet = endpoints.MapGroup("/v1/wallet").WithTags("wallet");

        // Any bearer may read the ladder: the app renders "pay Rs 900, get Rs 1,000" on the top-up
        // screen before a purchase, and the numbers are a published price list rather than anybody's data.
        wallet.MapGet("/voucher/discount-tiers", GetTiersAsync)
            .RequireAuthorization()
            .WithName("listVoucherDiscountTiers");

        wallet.MapPost("/voucher/purchase", PurchaseAsync)
            .RequireMageRideRole(MageRideRoles.Driver, MageRideRoles.FleetOwner)
            .WithName("purchaseVoucherFromWallet");

        var admin = endpoints.MapGroup("/v1/wallet/admin")
            .WithTags("wallet-admin")
            .RequireMageRideRole(
                MageRideRoles.Admin, MageRideRoles.SuperAdmin, MageRideRoles.FinanceOfficer);

        admin.MapGet("/voucher-discount-tiers", GetAdminTiersAsync)
            .WithName("adminListVoucherDiscountTiers");
        admin.MapPut("/voucher-discount-tiers", PutAdminTiersAsync)
            .WithName("adminUpdateVoucherDiscountTiers");

        return endpoints;
    }

    private static async Task<Ok<VoucherTiersResponse>> GetTiersAsync(
        IVoucherRepository vouchers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vouchers);

        var tiers = await vouchers.ReadTiersAsync(cancellationToken);

        // Active only on the driver-facing read: an inactive tier is not a price anybody can pay, and
        // showing it would put an unbuyable rung on the ladder.
        return TypedResults.Ok(new VoucherTiersResponse(
        [
            .. tiers.Where(tier => tier.Active)
                .Select(tier => new VoucherTierResponse(
                    tier.DenominationMinor, tier.DiscountBps, tier.Active, null)),
        ]));
    }

    private static async Task<Created<VoucherPurchaseResponse>> PurchaseAsync(
        PurchaseVoucherBody? body,
        HttpContext context,
        VoucherService vouchers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vouchers);

        if (body?.DenominationMinor is not { } denomination || denomination <= 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount, "denominationMinor is required and must be positive.");
        }

        var outcome = await vouchers.PurchaseAsync(
            context.User.RequireSubjectId(), denomination, body.GatewayRef, cancellationToken);

        var purchase = outcome.Purchase;

        return TypedResults.Created(
            $"/v1/wallet/{purchase.BuyerId}/transactions",
            new VoucherPurchaseResponse(
                purchase.Id,
                purchase.DenominationMinor,
                purchase.DiscountBpsApplied,
                purchase.PaidMinor,
                purchase.CreditedMinor,
                purchase.Currency,
                outcome.BalanceAfterMinor,
                purchase.JournalEntryId,
                purchase.CreatedAt));
    }

    /// <remarks>
    /// Finance sees every tier, active or not, plus what has been bought at each — which is what makes
    /// the aggregate reseller margin visible (US-9A.15).
    /// </remarks>
    private static async Task<Ok<AdminVoucherTiersResponse>> GetAdminTiersAsync(
        IVoucherRepository vouchers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vouchers);

        var tiers = await vouchers.ReadTiersWithUsageAsync(cancellationToken);

        return TypedResults.Ok(new AdminVoucherTiersResponse(
        [
            .. tiers.Select(tier => new VoucherTierUsageResponse(
                tier.DenominationMinor,
                tier.DiscountBps,
                tier.Active,
                tier.UpdatedAt,
                tier.PurchaseCount,
                tier.PurchasedValueMinor)),
        ]));
    }

    /// <remarks>
    /// <para>
    /// <b>An upsert, not a replacement.</b> A denomination the admin did not submit is left alone: the
    /// payload is a form, and a rung that vanished because it was off-screen would change what every
    /// driver pays. Retiring one is <c>active: false</c>, which somebody has to choose.
    /// </para>
    /// <para>
    /// <b>Audited by admin-bff (D-35), not here.</b> Every admin call arrives through that BFF, which
    /// records the actor and both images for the whole portal; a second audit row would double-count the
    /// change and leave the two copies to disagree. <c>voucher_discount_tiers.updated_by</c> keeps the
    /// after-image's actor permanently.
    /// </para>
    /// </remarks>
    private static async Task<Ok<VoucherTiersResponse>> PutAdminTiersAsync(
        UpdateVoucherTiersBody? body,
        HttpContext context,
        IVoucherRepository vouchers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vouchers);

        if (body?.Tiers is not { Count: > 0 } submitted)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["tiers"] = ["At least one tier is required."],
            });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var parsed = new List<VoucherTier>(submitted.Count);

        for (var i = 0; i < submitted.Count; i++)
        {
            var tier = submitted[i];

            if (tier.DenominationMinor is not { } denomination || denomination <= 0)
            {
                errors[$"tiers[{i}].denominationMinor"] = ["A denomination is a positive minor-unit amount."];
                continue;
            }

            if (tier.DiscountBps is not { } bps || bps is < 0 or > 10_000)
            {
                errors[$"tiers[{i}].discountBps"] = ["A discount is 0-10000 basis points (10000 = 100 %)."];
                continue;
            }

            parsed.Add(new VoucherTier(denomination, bps, tier.Active ?? true));
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var duplicates = parsed
            .GroupBy(tier => tier.DenominationMinor)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            // Two rates for one denomination: whichever won would be arbitrary, and the loser is the
            // rate the admin thinks they set.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["tiers"] = [$"One rate per denomination. Repeated: {string.Join(", ", duplicates)}."],
            });
        }

        var after = await vouchers.UpsertTiersAsync(
            parsed, context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new VoucherTiersResponse(
        [
            .. after.Select(tier => new VoucherTierResponse(
                tier.DenominationMinor, tier.DiscountBps, tier.Active, null)),
        ]));
    }
}
