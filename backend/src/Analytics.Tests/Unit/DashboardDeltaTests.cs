using MageRide.Analytics.Domain;

namespace MageRide.Analytics.Tests.Unit;

/// <summary>
/// The vs-previous-period percentages (<c>admin-bff.yaml#DashboardDeltas</c>).
/// </summary>
public sealed class DashboardDeltaTests
{
    [Theory]
    [InlineData(120, 100, 20d)]
    [InlineData(80, 100, -20d)]
    [InlineData(100, 100, 0d)]
    [InlineData(0, 100, -100d)]
    [InlineData(300, 100, 200d)]
    public void A_percentage_is_the_change_over_the_previous_period(long current, long previous, double expected)
    {
        Assert.Equal(expected, DashboardDeltas.Pct(current, previous));
    }

    /// <summary>
    /// Rounded to two decimals, so two replicas computing the same quotient render the same badge.
    /// </summary>
    [Fact]
    public void A_percentage_is_rounded_to_two_decimals()
    {
        // 11/7 → 57.142857…
        Assert.Equal(57.14d, DashboardDeltas.Pct(11, 7));
    }

    /// <summary>
    /// Growth from nothing has no percentage. Null, not 0 (which would say "no change" about a
    /// metric that went from nothing to something) and not 100 (which would invent a baseline).
    /// </summary>
    [Fact]
    public void Growth_from_a_zero_period_is_undefined()
    {
        Assert.Null(DashboardDeltas.Pct(42, 0));
    }

    /// <summary>Both ends zero is genuinely no change.</summary>
    [Fact]
    public void Zero_against_zero_is_no_change()
    {
        Assert.Equal(0d, DashboardDeltas.Pct(0, 0));
    }

    [Fact]
    public void All_five_metrics_get_their_own_delta()
    {
        var current = new DashboardKpis(CompletedTrips: 12, GrossFareMinor: 60_000, NewRiders: 4, NewDrivers: 2, DailyFeeRevenueMinor: 1_000);
        var previous = new DashboardKpis(CompletedTrips: 10, GrossFareMinor: 50_000, NewRiders: 8, NewDrivers: 0, DailyFeeRevenueMinor: 0);

        var deltas = DashboardDeltas.Between(current, previous);

        Assert.Equal(20d, deltas.CompletedTripsPct);
        Assert.Equal(20d, deltas.GrossFarePct);
        Assert.Equal(-50d, deltas.NewRidersPct);

        // Both undefined: the previous period had none. Not zero — 2 drivers is not "no change".
        Assert.Null(deltas.NewDriversPct);
        Assert.Null(deltas.DailyFeeRevenuePct);
    }
}
