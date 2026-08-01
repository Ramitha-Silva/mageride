using MageRide.Analytics.Domain;
using MageRide.Fare.Domain;

// fare-svc keeps its own copy of the ride-state names, so the two `RideStates` collide. ride-svc is
// the sole writer of the aggregate (R-01), so its spelling is the one this read model matches on.
using RideStates = MageRide.Ride.Domain.RideStates;

namespace MageRide.Analytics.Tests.Unit;

/// <summary>
/// The literals this read model matches on, against the services that own them.
/// </summary>
/// <remarks>
/// <para>
/// A read model is a copy of somebody else's vocabulary, and a copy that has drifted fails quietly:
/// if fare-svc widened R-05's terminal set, the dashboard would under-count revenue and every other
/// test in this suite would stay green, because they all seed rows using this component's own idea
/// of what a settled payment is.
/// </para>
/// <para>
/// This is why <c>Analytics.Tests</c> references <c>Ride.Api</c> and <c>Fare.Api</c>. Neither is
/// booted; only their domain constants are read.
/// </para>
/// </remarks>
public sealed class VocabularyTests
{
    /// <summary>
    /// Gross fare counts exactly the states a driver's earning posts on (R-05). The two are the same
    /// question — "did this fare get collected?" — asked by two services, so they must be one list.
    /// </summary>
    [Fact]
    public void Settled_payment_states_are_fare_svcs_terminal_set()
    {
        Assert.Equal(
            RidePaymentStates.Terminal.OrderBy(state => state, StringComparer.Ordinal),
            AnalyticsVocabulary.SettledPaymentStates.OrderBy(state => state, StringComparer.Ordinal));
    }

    /// <summary>
    /// The four that are counted, spelled out — so a reader of this suite sees the list without
    /// having to open fare-svc, and so a change to it is a decision rather than a merge.
    /// </summary>
    [Fact]
    public void The_settled_set_is_the_four_states_that_collected_money()
    {
        Assert.Equal(
            ["CashOnDeliveryCollected", "DriverConfirmedQR", "FellBackToCash", "Succeeded"],
            AnalyticsVocabulary.SettledPaymentStates.OrderBy(state => state, StringComparer.Ordinal));
    }

    /// <summary>
    /// A refunded, disputed or overpaid fare is not revenue, and none of them may creep into the set
    /// through a widened CHECK.
    /// </summary>
    [Theory]
    [InlineData("Disputed")]
    [InlineData("Refunded")]
    [InlineData("PartiallyRefunded")]
    [InlineData("Overpaid")]
    [InlineData("Initiated")]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Retried")]
    [InlineData("CashOnDelivery")]
    [InlineData("QrClaimedByPassenger")]
    public void A_fare_that_was_not_collected_is_not_gross_fare(string state)
    {
        Assert.DoesNotContain(state, AnalyticsVocabulary.SettledPaymentStates);
    }

    /// <summary>The completion marker is ride-svc's own state name.</summary>
    [Fact]
    public void The_completed_state_is_ride_svcs()
    {
        Assert.Equal(RideStates.Completed, AnalyticsVocabulary.RideCompleted);
    }

    /// <summary>
    /// And it is deliberately <b>not</b> terminal: a ride never rests in <c>Completed</c>, which is
    /// exactly why the rollup reads <c>rides.transitions</c> rather than <c>rides.rides.state</c>.
    /// </summary>
    [Fact]
    public void The_completed_state_is_not_a_state_a_ride_rests_in()
    {
        Assert.False(RideStates.IsTerminal(AnalyticsVocabulary.RideCompleted));
    }
}
