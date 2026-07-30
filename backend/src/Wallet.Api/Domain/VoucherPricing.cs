using MageRide.Shared.Errors;

namespace MageRide.Wallet.Domain;

/// <summary>One row of <c>billing.voucher_discount_tiers</c>.</summary>
/// <param name="DenominationMinor">The voucher's face value — the key the discount is set per.</param>
/// <param name="DiscountBps">Basis points off the price. 1000 = 10 %.</param>
internal sealed record VoucherTier(long DenominationMinor, int DiscountBps, bool Active);

/// <summary>What a purchase costs and what it credits.</summary>
internal sealed record VoucherPrice(long DenominationMinor, int DiscountBps, long PaidMinor, long CreditedMinor);

/// <summary>
/// The bulk-voucher discount arithmetic (US-9.19, AL-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>The discount reduces the price and never the credit.</b> D5' §9.3's worked example is the
/// whole rule: "a 10 % voucher → pay Rs 900, wallet credited Rs 1,000". So
/// <c>creditedMinor = denominationMinor</c> always — which C005 made a database constraint
/// (<c>ck_voucher_purchases_credited</c>) because the two columns are otherwise free to drift and a
/// wrong credit is a direct loss.
/// </para>
/// <para>
/// <b>That gap is the informal reseller's entire margin</b> (AL-01): they buy at 900 and pass credit
/// on at par, because a driver-to-driver transfer moves the exact value. There is no per-transfer
/// commission anywhere, and no journal kind that could record one.
/// </para>
/// </remarks>
internal static class VoucherPricing
{
    /// <summary>
    /// Prices a purchase against an active tier.
    /// </summary>
    /// <remarks>
    /// <b>The denomination must match a tier exactly.</b> An amount between tiers is rejected rather
    /// than rounded or interpolated: the discount is configured *per voucher value* (AL-01) and
    /// interpolating one would invent a rate no admin set — which, on the buy side of a margin, is a
    /// number somebody is paid.
    /// </remarks>
    public static VoucherPrice Price(long denominationMinor, IReadOnlyList<VoucherTier> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        var tier = tiers.FirstOrDefault(
            candidate => candidate.Active && candidate.DenominationMinor == denominationMinor);

        if (tier is null)
        {
            var available = tiers
                .Where(candidate => candidate.Active)
                .Select(candidate => candidate.DenominationMinor)
                .Order()
                .ToArray();

            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["denominationMinor"] =
                    [
                        available.Length == 0
                            ? "No bulk-voucher denomination is active. An admin sets them in the Admin Portal (AL-01)."
                            : $"Must be one of the active denominations: {string.Join(", ", available)}.",
                    ],
                },
                "The bulk-voucher discount is configured per voucher value, so a denomination between "
                + "tiers has no rate and is not priced by interpolation.");
        }

        return new VoucherPrice(
            denominationMinor,
            tier.DiscountBps,
            PaidFor(denominationMinor, tier.DiscountBps),
            denominationMinor);
    }

    /// <summary>
    /// What the buyer pays: the face value less the discount, in whole minor units.
    /// </summary>
    /// <remarks>
    /// <b>The discount is truncated, so the price rounds up.</b> A tier whose basis points do not
    /// divide the denomination evenly leaves a fraction of a minor unit, and this platform transacts
    /// in integers only (CLAUDE.md). Integer division drops that fraction from the *discount*, which
    /// leaves it with the platform rather than handing it to the buyer — the direction an unattended
    /// rounding error should always fall. The credit is unaffected: it is the face value, always.
    /// </remarks>
    public static long PaidFor(long denominationMinor, int discountBps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominationMinor);
        ArgumentOutOfRangeException.ThrowIfNegative(discountBps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(discountBps, 10_000);

        // Integer arithmetic throughout — a double would make Rs 10,000 at 15 % depend on the
        // rounding mode of a floating-point unit.
        var discount = denominationMinor * discountBps / 10_000;

        return denominationMinor - discount;
    }
}
