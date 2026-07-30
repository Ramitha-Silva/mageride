using MageRide.Shared.Errors;
using MageRide.Wallet.Domain;

namespace MageRide.Wallet.Tests.Unit;

/// <summary>
/// The bulk-voucher arithmetic as a pure function — no container, because none of it depends on one.
/// </summary>
public sealed class VoucherPricingTests
{
    /// <summary>D5' §9.3's worked example, which is the one number every spec agrees on.</summary>
    [Fact]
    public void The_spec_example_is_pay_900_receive_1000()
    {
        var price = VoucherPricing.Price(100_000, [new VoucherTier(100_000, 1_000, true)]);

        Assert.Equal(90_000, price.PaidMinor);
        Assert.Equal(100_000, price.CreditedMinor);
        Assert.Equal(100_000, price.DenominationMinor);
        Assert.Equal(1_000, price.DiscountBps);
    }

    [Theory]
    [InlineData(100_000, 0, 100_000)]
    [InlineData(100_000, 10_000, 0)]
    [InlineData(200_000, 1_100, 178_000)]
    [InlineData(1_000_000, 1_500, 850_000)]
    public void The_discount_comes_off_the_price(long denomination, int bps, long expected) =>
        Assert.Equal(expected, VoucherPricing.PaidFor(denomination, bps));

    /// <summary>
    /// The credit is the face value at every rate, including 100 % off — the discount is never allowed
    /// to touch it (`ck_voucher_purchases_credited`, C005).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1_000)]
    [InlineData(9_999)]
    [InlineData(10_000)]
    public void The_credit_is_always_the_face_value(int bps)
    {
        var price = VoucherPricing.Price(500_000, [new VoucherTier(500_000, bps, true)]);

        Assert.Equal(500_000, price.CreditedMinor);
        Assert.True(price.PaidMinor <= price.CreditedMinor);
    }

    /// <summary>
    /// A rate that does not divide the denomination leaves a fraction of a minor unit; it stays with the
    /// platform, which is the direction an unattended rounding error should fall.
    /// </summary>
    [Fact]
    public void An_indivisible_rate_rounds_the_price_up()
    {
        // 333 bps of 99,999 is 3,329.966… — the discount truncates to 3,329, so the price is 96,670
        // rather than 96,669.
        Assert.Equal(96_670, VoucherPricing.PaidFor(99_999, 333));
    }

    /// <summary>
    /// A denomination that is not an active tier is refused, and the error names the ones that are — the
    /// rate is per voucher value (AL-01) and interpolating one invents a number somebody is paid.
    /// </summary>
    [Fact]
    public void A_denomination_that_is_not_a_tier_is_refused()
    {
        var tiers = new[]
        {
            new VoucherTier(100_000, 1_000, true),
            new VoucherTier(200_000, 1_100, true),
        };

        var exception = Assert.Throws<MageRideValidationException>(() => VoucherPricing.Price(150_000, tiers));

        Assert.Equal(MageRideErrors.ValidationFailed, exception.Error);

        var message = Assert.Single(exception.Errors["denominationMinor"]);

        Assert.Contains("100000", message);
        Assert.Contains("200000", message);
    }

    /// <summary>An inactive tier is not a price anybody can pay.</summary>
    [Fact]
    public void An_inactive_tier_cannot_be_priced()
    {
        var tiers = new[] { new VoucherTier(100_000, 1_000, false) };

        var exception = Assert.Throws<MageRideValidationException>(() => VoucherPricing.Price(100_000, tiers));

        // No active denominations at all, so the message says so rather than listing an empty set.
        Assert.Contains("No bulk-voucher denomination is active", Assert.Single(exception.Errors["denominationMinor"]));
    }

    /// <summary>An empty ladder is a configuration state, not a crash.</summary>
    [Fact]
    public void An_empty_ladder_is_refused_with_a_reason() =>
        Assert.Throws<MageRideValidationException>(() => VoucherPricing.Price(100_000, []));

    [Theory]
    [InlineData(0, 1_000)]
    [InlineData(-1, 1_000)]
    [InlineData(100_000, -1)]
    [InlineData(100_000, 10_001)]
    public void An_impossible_input_throws_rather_than_pricing_it(long denomination, int bps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VoucherPricing.PaidFor(denomination, bps));
}
