using MageRide.Fare.Domain;
using MageRide.Fare.Observability;
using MageRide.Fare.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fare.Tests.Integration;

/// <summary>
/// ADD §13.3.1 row 7 as a gauge: payments in <c>Overpaid</c> (R-19, ADD §11.14, Δ C119).
/// </summary>
/// <remarks>
/// The alert is <c>&gt; 0 for 1h</c>, so what has to be true is that the predicate is exactly the
/// row's and not a superset — every case below is a near miss that must <em>not</em> be counted.
/// </remarks>
[Collection(FareCollection.Name)]
public sealed class OverpaidGaugeTests(PostgresFixture postgres)
{
    private static Task<int> CountAsync(FareHarness harness) =>
        OverpaidGauge.CountAsync(harness.Services, CancellationToken.None);

    [Fact]
    public async Task An_overpaid_payment_is_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        await harness.Seed.PaymentAsync(ride.RideId, RidePaymentStates.Overpaid);

        Assert.Equal(1, await CountAsync(harness));
    }

    /// <summary>
    /// The states either side of it in D-10's retry chain. A settled fare is revenue and a refunded
    /// one is money already returned; only <c>Overpaid</c> is a decision nobody has taken.
    /// </summary>
    [Theory]
    [InlineData(RidePaymentStates.Succeeded)]
    [InlineData(RidePaymentStates.FellBackToCash)]
    [InlineData(RidePaymentStates.Refunded)]
    [InlineData(RidePaymentStates.Failed)]
    public async Task A_payment_in_any_other_state_is_not_counted(string state)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        await harness.Seed.PaymentAsync(ride.RideId, state);

        Assert.Equal(0, await CountAsync(harness));
    }

    /// <summary>
    /// A quiet platform reads zero rather than reading nothing. The gauge is compared against zero
    /// by the alert, so "no rows" and "no gauge" must not be the same thing.
    /// </summary>
    [Fact]
    public async Task A_platform_with_no_payments_reports_zero()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await FareHarness.StartAsync(postgres);

        Assert.Equal(0, await CountAsync(harness));
    }
}
