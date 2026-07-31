using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Subscriptions.Fees;
using MageRide.Subscriptions.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Subscriptions.Endpoints;

/// <summary>
/// <c>/v1/admin/fees/rates</c>, <c>/v1/admin/voucher-discount-tiers</c> and the Mode B billing view —
/// Admin Portal Config (SCR-AP-007).
/// </summary>
/// <remarks>
/// <para>
/// <b>Finance Officer, Admin and Super Admin.</b> URD §Epic 14's role mapping is explicit that
/// "fee/tariff rates (US-14.4, US-14.5)" belong to the <b>Finance Officer</b> (and Super Admin); Admin
/// is included because D3' marks the routes "admin" and AL-06 makes Admin the blanket platform role.
/// The other three back-office roles are out: a Verification Officer or a Support CSR who could reprice
/// every driver on the platform is not a permission any spec grants, and the narrow gate widens later
/// without a migration.
/// </para>
/// <para>
/// <b>The audit row is not written here.</b> D-35's immutable admin log is <c>audit.events</c> and
/// admin-bff owns it (C065): every one of these calls arrives through that BFF, which records the
/// actor, the before-image and the after-image for the whole Admin Portal. A second row written here
/// would double-count every edit and leave the two copies to disagree. What this service contributes is
/// the after-image — <c>billing.voucher_discount_tiers.updated_by</c> and
/// <c>billing.plans.updated_at</c> — plus an information-level log naming the actor.
/// </para>
/// <para>
/// <b>A rate change reaches the next charge and never a past one.</b> There is no code path in this
/// service that revisits a <c>billing.daily_fee_charges</c> row: the charge path reads
/// <c>billing.plans</c> at the moment it charges and writes the amount it actually took. That is the
/// whole of "no retro-billing", and it is a property of what is absent rather than of anything here.
/// </para>
/// </remarks>
public static class AdminFeeEndpoints
{
    /// <summary>The three roles URD §Epic 14 puts on fee and tariff configuration.</summary>
    private static readonly string[] FinanceRoles =
        [MageRideRoles.FinanceOfficer, MageRideRoles.Admin, MageRideRoles.SuperAdmin];

    /// <summary>
    /// AL-09's canonical vehicle types — the CHECK on <c>registry.vehicles.vehicle_type</c>.
    /// </summary>
    /// <remarks>
    /// <c>billing.plans.vehicle_type</c> is a bare <c>TEXT</c> primary key with no CHECK of its own, so
    /// without this an admin could configure a rate for <c>car</c> or <c>lorry</c> — a row that looks
    /// configured on the Config screen, matches no vehicle ever, and leaves the type it was meant for
    /// unable to go online. There is no <c>car</c>: it maps to <c>sedan</c>.
    /// </remarks>
    private static readonly HashSet<string> CanonicalVehicleTypes = new(StringComparer.Ordinal)
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck", "bus", "train",
    };

    public static IEndpointRouteBuilder MapAdminFeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var admin = endpoints.MapGroup("/v1/admin")
            .WithTags("subscription-admin")
            .RequireMageRideRole(FinanceRoles);

        admin.MapPut("/fees/rates", UpdateRatesAsync).WithName("updateDailyFeeRates");
        admin.MapPut("/voucher-discount-tiers", UpdateTiersAsync).WithName("updateVoucherDiscountTiers");
        admin.MapGet("/voucher-discount-tiers", ListTiersAsync).WithName("listSubscriptionVoucherDiscountTiers");

        admin.MapGet("/fees/mode-b/charges", ListModeBChargesAsync).WithName("listModeBPlatformCharges");

        return endpoints;
    }

    /// <remarks>
    /// <b>An upsert of what was sent, not a replacement of the table.</b> A <c>PUT</c> that deleted the
    /// rows it was not given would let a Config screen rendering six of the eight tiers silently
    /// un-configure the other two — and an un-configured type cannot go online at all.
    /// </remarks>
    private static async Task<Ok<DailyFeeRatesResponse>> UpdateRatesAsync(
        UpdateDailyFeeRatesBody? body,
        HttpContext context,
        IPlanRepository plans,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(loggers);

        var items = body?.Items;

        if (items is null || items.Count == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["items"] = ["items is required and must carry at least one rate."],
            });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var parsed = new List<FeePlanInput>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var type = item.VehicleType?.Trim();
            var mode = item.Mode?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(type) || !CanonicalVehicleTypes.Contains(type))
            {
                errors[$"items[{index}].vehicleType"] =
                [
                    $"'{item.VehicleType}' is not one of AL-09's canonical vehicle types: "
                    + $"{string.Join(", ", CanonicalVehicleTypes.Order(StringComparer.Ordinal))}.",
                ];

                continue;
            }

            if (!OperatingModes.IsKnown(mode))
            {
                errors[$"items[{index}].mode"] = [$"mode must be A, B or C; '{item.Mode}' is not."];
                continue;
            }

            if (item.DailyFeeMinor is not { } fee || fee < 0)
            {
                errors[$"items[{index}].dailyFeeMinor"] =
                    ["dailyFeeMinor is required and is an unsigned integer in minor units (Rs × 100)."];

                continue;
            }

            // The fence, held where the number is written rather than only where it is read. AL-09 and
            // the URD both say Mode A pays nothing; a Config screen that could put Rs 300 on `bus`
            // would start billing public transport, and the charge path zeroes it defensively anyway.
            if (mode == OperatingModes.PublicTransport && fee != 0)
            {
                errors[$"items[{index}].dailyFeeMinor"] =
                    ["Mode A is free. Bus and train journeys carry no daily platform fee (AL-09)."];

                continue;
            }

            parsed.Add(new FeePlanInput(type, fee, mode!));
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var rates = await plans.UpsertAsync(parsed, cancellationToken);

        loggers.CreateLogger(typeof(AdminFeeEndpoints)).LogInformation(
            "Daily-fee rates updated by {Actor}: {Rates}. Applies from the next charge; no row already "
            + "written is revisited.",
            context.User.RequireSubjectId(),
            string.Join(", ", parsed.Select(rate => $"{rate.VehicleType}={rate.DailyFeeMinor}")));

        return TypedResults.Ok(new DailyFeeRatesResponse(
            [.. rates.Select(rate => new DailyFeeRateResponse(
                rate.VehicleType, rate.DailyFeeMinor, rate.Mode, rate.Currency))]));
    }

    /// <remarks>
    /// <b>The same table wallet-svc's <c>PUT /v1/wallet/admin/voucher-discount-tiers</c> writes.</b> D3'
    /// Part 2 prints both spellings; C046 landed one and this is the other, and they are one row set with
    /// one meaning. One should be retired — C007 said so first, and it is raised again in the C047
    /// handoff.
    /// </remarks>
    private static async Task<Ok<VoucherDiscountTiersResponse>> UpdateTiersAsync(
        UpdateVoucherDiscountTiersBody? body,
        HttpContext context,
        IVoucherTierRepository tiers,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(loggers);

        var items = body?.Tiers;

        if (items is null || items.Count == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["tiers"] = ["tiers is required and must carry at least one denomination."],
            });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var parsed = new List<VoucherTierInput>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            if (item.DenominationMinor is not { } denomination || denomination <= 0)
            {
                errors[$"tiers[{index}].denominationMinor"] =
                    ["denominationMinor is the voucher's face value in minor units and must be positive."];

                continue;
            }

            // 10 000 bps is 100%: a free voucher. Refused above that rather than clamped, because a
            // rate over 100% would mean paying a driver to take credit, and the CHECK on
            // billing.voucher_discount_tiers would answer with a 500 instead of a field error.
            if (item.DiscountBps is not { } discount || discount is < 0 or > 10_000)
            {
                errors[$"tiers[{index}].discountBps"] =
                    ["discountBps is basis points off the price and must be between 0 and 10000 (0–100%)."];

                continue;
            }

            parsed.Add(new VoucherTierInput(denomination, discount, item.Active ?? true));
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var actor = context.User.RequireSubjectId();
        var updated = await tiers.UpsertAsync(parsed, actor, cancellationToken);

        loggers.CreateLogger(typeof(AdminFeeEndpoints)).LogInformation(
            "Bulk-voucher discount tiers updated by {Actor}: {Tiers}. This percentage is the whole of the "
            + "informal reseller's margin — there is no per-transfer commission anywhere (AL-01).",
            actor,
            string.Join(", ", parsed.Select(tier => $"{tier.DenominationMinor}@{tier.DiscountBps}bps")));

        return TypedResults.Ok(Project(updated));
    }

    private static async Task<Ok<VoucherDiscountTiersResponse>> ListTiersAsync(
        IVoucherTierRepository tiers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        return TypedResults.Ok(Project(await tiers.ListAsync(cancellationToken)));
    }

    /// <summary>
    /// The month's Mode B platform charges, consolidated per fleet — the AL-03 hand-off to C060.
    /// </summary>
    /// <remarks>
    /// The per-fleet totals are computed here rather than stored, because
    /// <c>billing.fleet_invoices</c> is fleet-billing-svc's table and a second writer of it would give
    /// one fleet two invoices for one month. What this answers is the input to that invoice: the lines,
    /// their totals, and the fleet each belongs to. Vehicles in no fleet group under a null
    /// <c>fleetId</c> — they are individually-owned Mode B vehicles and belong to no consolidated
    /// invoice at all.
    /// </remarks>
    private static async Task<Ok<ModeBChargesResponse>> ListModeBChargesAsync(
        string? month,
        string? fleetId,
        ModeBBillingService billing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(billing);

        var period = month is null ? billing.CurrentPeriod() : BusinessDates.Require(month, "month");
        var charges = await billing.ListAsync(period, RequestIds.Optional(fleetId), cancellationToken);

        var fleets = charges
            .GroupBy(charge => charge.FleetId)
            .Select(group => new ModeBFleetTotalResponse(
                group.Key,
                group.Count(),
                group.Sum(charge => charge.AmountMinor),
                group.First().Currency))
            .OrderBy(fleet => fleet.FleetId.HasValue ? 0 : 1)
            .ThenBy(fleet => fleet.FleetId)
            .ToArray();

        return TypedResults.Ok(new ModeBChargesResponse(
            period,
            charges.Sum(charge => charge.AmountMinor),
            Currencies.Lkr,
            fleets,
            [.. charges.Select(charge => new ModeBChargeResponse(
                charge.VehicleId,
                charge.RegistrationNumber,
                charge.VehicleType,
                charge.OwnerId,
                charge.FleetId,
                charge.PeriodMonth,
                charge.AmountMinor,
                charge.Currency,
                charge.Status))]));
    }

    private static VoucherDiscountTiersResponse Project(IReadOnlyList<VoucherTier> tiers) =>
        new([.. tiers.Select(tier => new VoucherDiscountTierResponse(
            tier.DenominationMinor, tier.DiscountBps, tier.Active, tier.UpdatedAt))]);
}
