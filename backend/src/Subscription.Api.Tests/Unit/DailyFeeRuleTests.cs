using MageRide.Subscriptions.Domain;

namespace MageRide.Subscriptions.Tests.Unit;

/// <summary>
/// D5' §2.2's four branches, walked without a database.
/// </summary>
/// <remarks>
/// The integration suite proves the rule holds against the two schemas that enforce it; this proves the
/// branch itself, including the combinations a database test would need a contrived fixture to reach.
/// </remarks>
public sealed class DailyFeeRuleTests
{
    [Fact]
    public void The_first_trip_of_the_day_is_waived_whatever_the_rate_says()
    {
        Assert.Equal(
            FeeOutcome.WaivedFirstTrip,
            DailyFeeRule.Decide(tripsToday: 0, alreadyPaid: false, dailyFeeMinor: 30_000, freeTripsPerDay: 1));
    }

    /// <summary>
    /// The waiver is decided before anything else is consulted — US-9.1's "no wallet check".
    /// </summary>
    [Fact]
    public void The_first_trip_is_waived_even_when_a_paid_row_somehow_exists()
    {
        Assert.Equal(
            FeeOutcome.WaivedFirstTrip,
            DailyFeeRule.Decide(tripsToday: 0, alreadyPaid: true, dailyFeeMinor: 10_000, freeTripsPerDay: 1));
    }

    [Fact]
    public void The_second_trip_is_chargeable()
    {
        Assert.Equal(
            FeeOutcome.Chargeable,
            DailyFeeRule.Decide(tripsToday: 1, alreadyPaid: false, dailyFeeMinor: 10_000, freeTripsPerDay: 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(40)]
    public void Later_trips_on_a_settled_day_take_nothing(int tripsToday)
    {
        Assert.Equal(
            FeeOutcome.AlreadyCharged,
            DailyFeeRule.Decide(tripsToday, alreadyPaid: true, dailyFeeMinor: 10_000, freeTripsPerDay: 1));
    }

    /// <summary>
    /// A zero rate — Mode A, or a type Finance has deliberately zeroed — owes nothing and burns no
    /// idempotency key.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void A_zero_rate_never_becomes_a_charge(int tripsToday)
    {
        Assert.Equal(
            FeeOutcome.NothingOwed,
            DailyFeeRule.Decide(tripsToday, alreadyPaid: false, dailyFeeMinor: 0, freeTripsPerDay: 1));
    }

    /// <summary>
    /// The ledger key is the business fact, spelled exactly as C005 decision 4 and 1107's header fix it.
    /// </summary>
    /// <remarks>
    /// This string is a cross-service contract: wallet-svc's UNIQUE
    /// <c>billing.journal_entries.idempotency_key</c> is what makes a second charge a replay, and a
    /// change to the spelling here would silently start taking a second fee. Asserted literally, so a
    /// well-meaning refactor has to change a test that says why.
    /// </remarks>
    [Fact]
    public void The_ledger_key_is_the_spelling_wallet_svc_deduplicates_on()
    {
        var driverId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var vehicleId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        Assert.Equal(
            "daily_fee:11111111-2222-3333-4444-555555555555:66666666-7777-8888-9999-aaaaaaaaaaaa:2026-07-30",
            DailyFeeRule.LedgerKey(driverId, vehicleId, new DateOnly(2026, 7, 30)));
    }

    /// <summary>
    /// The key is formatted invariantly, so a host whose culture renders dates differently cannot mint a
    /// second key for the same day.
    /// </summary>
    [Fact]
    public void The_ledger_key_does_not_depend_on_the_host_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("ar-SA");

            Assert.EndsWith(
                ":2026-07-30",
                DailyFeeRule.LedgerKey(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 7, 30)),
                StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
