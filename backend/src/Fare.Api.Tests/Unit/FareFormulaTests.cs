using MageRide.Fare.Domain;

namespace MageRide.Fare.Tests.Unit;

/// <summary>
/// D5' §1.1's master formula and §1.3's rounding — the two definition-of-done items that are
/// arithmetic rather than infrastructure.
/// </summary>
public sealed class FareFormulaTests
{
    /// <summary>Rs → minor units, so the tariff table below reads like D5' §1.1 prints it.</summary>
    private const long Rupee = 100;

    private static Tariff TariffFor(string vehicleType, long firstKmRupees, long perKmRupees) =>
        new(
            Guid.NewGuid(),
            vehicleType,
            firstKmRupees * Rupee,
            perKmRupees * Rupee,
            PeakSurchargePct: 20,
            NightSurchargePct: 15,
            Currency: FareFormula.Currency,
            EffectiveFrom: DateTimeOffset.UnixEpoch);

    /// <summary>
    /// D5' §1.1 / URD §8's tariff table, every row, priced over the included kilometre and one
    /// beyond it. The arithmetic is <c>first_km + per_km × extra</c> and nothing else.
    /// </summary>
    [Theory]
    // vehicleType,   1st km Rs, per km Rs, distanceKm, expected total Rs
    [InlineData("motorbike", 80, 60, 1.0, 80)]
    [InlineData("motorbike", 80, 60, 2.0, 140)]
    [InlineData("motorbike", 80, 60, 5.0, 320)]
    [InlineData("three_wheeler", 100, 80, 1.0, 100)]
    [InlineData("three_wheeler", 100, 80, 2.0, 180)]
    [InlineData("three_wheeler", 100, 80, 4.8, 404)]
    [InlineData("flex", 130, 90, 1.0, 130)]
    [InlineData("flex", 130, 90, 3.0, 310)]
    [InlineData("sedan", 150, 100, 1.0, 150)]
    [InlineData("sedan", 150, 100, 10.0, 1050)]
    [InlineData("mini_van", 150, 110, 2.0, 260)]
    [InlineData("van", 150, 120, 2.0, 270)]
    [InlineData("van", 150, 120, 12.5, 1530)]
    public void The_tariff_table_prices_to_the_minor_unit(
        string vehicleType, int firstKmRupees, int perKmRupees, double distanceKm, int expectedRupees)
    {
        var fare = FareFormula.Price(
            TariffFor(vehicleType, firstKmRupees, perKmRupees), distanceKm, isPeak: false, isNight: false);

        Assert.Equal(expectedRupees * Rupee, fare.TotalMinor);
        Assert.Equal(0, fare.SurchargeMinor);
        Assert.Equal(FareFormula.Currency, fare.Currency);
    }

    /// <summary>
    /// The first kilometre is inside the first-km charge, and a shorter trip is not cheaper than it.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(0.99)]
    [InlineData(1.0)]
    public void A_trip_within_the_first_kilometre_costs_the_first_km_charge(double distanceKm)
    {
        var fare = FareFormula.Price(
            TariffFor("three_wheeler", 100, 80), distanceKm, isPeak: false, isNight: false);

        Assert.Equal(100 * Rupee, fare.TotalMinor);
    }

    /// <summary>D5' §1.1: peak is +20% of the base, night +15%.</summary>
    [Theory]
    [InlineData(false, false, 18_000)]   // Rs 180 base
    [InlineData(true, false, 21_600)]    // +20%
    [InlineData(false, true, 20_700)]    // +15%
    [InlineData(true, true, 24_300)]     // +35%, stacked additively on the base
    public void Peak_and_night_stack_additively_on_the_base(bool isPeak, bool isNight, long expectedMinor)
    {
        var fare = FareFormula.Price(TariffFor("three_wheeler", 100, 80), 2.0, isPeak, isNight);

        Assert.Equal(expectedMinor, fare.TotalMinor);
        Assert.Equal(18_000, fare.BaseMinor);
        Assert.Equal(expectedMinor - 18_000, fare.SurchargeMinor);
    }

    /// <summary>
    /// A trip that is both peak and night is base × 35%, never base × 1.20 × 1.15 (which would be
    /// Rs 248.40 rather than Rs 243). One product, one round — §1.1 computes
    /// <c>round(base * (peak + night) / 100)</c>.
    /// </summary>
    [Fact]
    public void Stacking_is_additive_and_not_compounded()
    {
        var tariff = TariffFor("three_wheeler", 100, 80);

        var both = FareFormula.Price(tariff, 2.0, isPeak: true, isNight: true);
        var compounded = (long)Math.Round(18_000 * 1.20 * 1.15, MidpointRounding.AwayFromZero);

        Assert.Equal(24_300, both.TotalMinor);
        Assert.NotEqual(compounded, both.TotalMinor);
    }

    /// <summary>
    /// §1.3: one round where a product is taken, away from zero. 0.5 of a minor unit rounds up, so
    /// a passenger reading "Rs 480" does not need to know which way it fell.
    /// </summary>
    [Theory]
    // baseMinor is 10 000 (first km only); the pct is chosen so base × pct / 100 lands on a half.
    [InlineData(1, 100)]      // 10 000 × 1%  = 100    exactly
    [InlineData(3, 300)]      // 10 000 × 3%  = 300    exactly
    public void The_surcharge_is_rounded_once_away_from_zero(int pct, long expectedSurcharge)
    {
        var tariff = TariffFor("three_wheeler", 100, 80) with { PeakSurchargePct = pct };

        var fare = FareFormula.Price(tariff, 1.0, isPeak: true, isNight: false);

        Assert.Equal(expectedSurcharge, fare.SurchargeMinor);
    }

    /// <summary>Half rounds away from zero, not to even — the §1.3 choice, at the boundary.</summary>
    [Theory]
    [InlineData(5, 10, 1)]      // 0.5 → 1
    [InlineData(15, 10, 2)]     // 1.5 → 2, where banker's rounding would give 2 as well
    [InlineData(25, 10, 3)]     // 2.5 → 3, where banker's rounding would give 2
    [InlineData(-5, 10, -1)]    // and away from zero on the other side
    public void Division_rounds_half_away_from_zero(long value, long divisor, long expected) =>
        Assert.Equal(expected, FareFormula.DivideRounded(value, divisor));

    /// <summary>
    /// The definition of done's "money never touches a floating-point type": the distance becomes
    /// whole metres before it meets a rate, so two distances that round to the same metre price
    /// identically and no representation error can put a rupee between them.
    /// </summary>
    [Fact]
    public void Distance_is_quantised_to_metres_before_it_meets_a_rate()
    {
        var tariff = TariffFor("sedan", 150, 100);

        // 4.0001 km and 4.00014 km are 4 000.1 m and 4 000.14 m — the same 4 000 metres.
        Assert.Equal(
            FareFormula.Price(tariff, 4.0001, false, false).TotalMinor,
            FareFormula.Price(tariff, 4.00014, false, false).TotalMinor);

        Assert.Equal(4_000, FareFormula.MetresOf(4.0001));
        Assert.Equal(4_001, FareFormula.MetresOf(4.0006));
        Assert.Equal(0, FareFormula.MetresOf(double.NaN));
        Assert.Equal(0, FareFormula.MetresOf(-3));
    }

    /// <summary>
    /// The classic float trap, priced. 0.1 + 0.2 is not 0.3 in binary floating point; the fare for
    /// 1.3 km must still be exactly the first km plus 300 m of the per-km rate.
    /// </summary>
    [Fact]
    public void A_distance_that_is_inexact_in_binary_still_prices_exactly()
    {
        var tariff = TariffFor("three_wheeler", 100, 80);

        var fare = FareFormula.Price(tariff, 1.0 + 0.1 + 0.2, isPeak: false, isNight: false);

        // Rs 100 + 0.3 km × Rs 80 = Rs 124.00, to the minor unit.
        Assert.Equal(12_400, fare.TotalMinor);
    }

    /// <summary>The D3' worked example: a Rs 480 estimate on a 4.8 km three-wheeler trip at peak.</summary>
    [Fact]
    public void The_contract_example_reproduces()
    {
        var fare = FareFormula.Price(TariffFor("three_wheeler", 100, 80), 4.8, isPeak: true, isNight: false);

        // base = 10 000 + round(3.8 × 8 000) = 40 400; +20% = 8 080.
        Assert.Equal(40_400, fare.BaseMinor);
        Assert.Equal(8_080, fare.SurchargeMinor);
        Assert.Equal(48_480, fare.TotalMinor);
        Assert.Equal(20, fare.PeakSurchargePct);
        Assert.Equal(0, fare.NightSurchargePct);
    }
}

/// <summary>The midnight-wrapping window rule migration 1001 declines to CHECK.</summary>
public sealed class PeakWindowTests
{
    private static PeakWindow Window(string kind, string start, string end) =>
        new(Guid.NewGuid(), kind, TimeOnly.Parse(start), TimeOnly.Parse(end), 20);

    [Theory]
    [InlineData("06:59", false)]
    [InlineData("07:00", true)]
    [InlineData("08:30", true)]
    [InlineData("08:59", true)]
    [InlineData("09:00", false)]   // half-open: the window ends at 09:00
    public void A_daytime_window_is_half_open(string local, bool covered) =>
        Assert.Equal(covered, Window(PeakWindow.Peak, "07:00", "09:00").Covers(TimeOnly.Parse(local)));

    /// <summary>
    /// The seeded night window is 22:00–05:00, so <c>end &lt; start</c>. A naive range test would
    /// make the night surcharge unreachable rather than merely wrong.
    /// </summary>
    [Theory]
    [InlineData("21:59", false)]
    [InlineData("22:00", true)]
    [InlineData("23:59", true)]
    [InlineData("00:00", true)]
    [InlineData("04:59", true)]
    [InlineData("05:00", false)]
    [InlineData("12:00", false)]
    public void The_night_window_wraps_midnight(string local, bool covered) =>
        Assert.Equal(covered, Window(PeakWindow.Night, "22:00", "05:00").Covers(TimeOnly.Parse(local)));
}
