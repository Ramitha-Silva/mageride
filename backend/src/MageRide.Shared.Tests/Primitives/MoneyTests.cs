using MageRide.Shared.Primitives;

namespace MageRide.Shared.Tests.Primitives;

/// <summary>Money is integer minor units end to end (CLAUDE.md, D3' §0).</summary>
public sealed class MoneyTests
{
    [Fact]
    public void Rupees_convert_to_cents()
    {
        Assert.Equal(48000, Money.FromMajor(480m).AmountMinor);
        Assert.Equal(50, Money.FromMajor(0.5m).AmountMinor);
        Assert.Equal(480m, Money.FromMinor(48000).ToMajor());
    }

    [Fact]
    public void Half_cents_round_away_from_zero()
    {
        Assert.Equal(1, Money.FromMajor(0.005m).AmountMinor);
        Assert.Equal(-1, Money.FromMajor(-0.005m).AmountMinor);
    }

    [Fact]
    public void Arithmetic_is_exact_over_many_entries()
    {
        // The classic float failure: 100 × Rs 0.10 must be exactly Rs 10.00.
        var total = Money.Zero;
        for (var i = 0; i < 100; i++)
        {
            total += Money.FromMajor(0.10m);
        }

        Assert.Equal(1000, total.AmountMinor);
        Assert.Equal(10m, total.ToMajor());
    }

    [Fact]
    public void Subtraction_and_negation_keep_the_currency()
    {
        var fare = Money.FromMinor(48000);
        var discount = Money.FromMinor(5000);

        Assert.Equal(43000, (fare - discount).AmountMinor);
        Assert.Equal(-48000, (-fare).AmountMinor);
        Assert.Equal(Money.Lkr, (fare - discount).Currency);
    }

    [Fact]
    public void Multiplication_scales_minor_units()
    {
        Assert.Equal(144000, (Money.FromMinor(48000) * 3).AmountMinor);
        Assert.Equal(144000, (3 * Money.FromMinor(48000)).AmountMinor);
    }

    [Fact]
    public void Mixing_currencies_throws_rather_than_silently_adding()
    {
        var lkr = Money.FromMinor(1000);
        var usd = Money.FromMinor(1000, "USD");

        Assert.Throws<InvalidOperationException>(() => lkr + usd);
        Assert.Throws<InvalidOperationException>(() => lkr.CompareTo(usd));
    }

    [Fact]
    public void Overflow_throws_rather_than_wrapping()
    {
        var huge = Money.FromMinor(long.MaxValue);
        Assert.Throws<OverflowException>(() => huge + Money.FromMinor(1));
    }

    [Fact]
    public void Comparison_orders_by_minor_units()
    {
        Assert.True(Money.FromMinor(100) < Money.FromMinor(200));
        Assert.True(Money.FromMinor(200) >= Money.FromMinor(200));
        Assert.True(Money.FromMinor(-1).IsNegative);
        Assert.True(Money.Zero.IsZero);
    }

    [Fact]
    public void Currency_must_be_an_iso_code()
    {
        Assert.Throws<ArgumentException>(() => new Money(100, "RUPEES"));
        Assert.Throws<ArgumentException>(() => new Money(100, ""));
        Assert.Equal("LKR", new Money(100, "lkr").Currency);
    }
}
