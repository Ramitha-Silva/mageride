using MageRide.Voip.Domain;

namespace MageRide.Voip.Tests.Unit;

/// <summary>
/// P-05, as a property of the projection rather than of a code path.
/// </summary>
public sealed class ParticipantTests
{
    private static readonly Guid Booker = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Rider = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Driver = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid Stranger = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    private static RideParticipants Proxy(string state = "InProgress") =>
        new(Guid.NewGuid(), Booker, Booker, Rider, IsProxy: true, Driver, state);

    /// <summary>P-01 + P-03: a third-party booking whose rider never registered.</summary>
    private static RideParticipants ProxyWithUnregisteredRider() =>
        new(Guid.NewGuid(), Booker, Booker, null, IsProxy: true, Driver, "InProgress");

    private static RideParticipants Ordinary(string state = "InProgress") =>
        new(Guid.NewGuid(), Booker, Booker, null, IsProxy: false, Driver, state);

    [Fact]
    public void On_a_proxy_ride_the_passenger_side_is_the_rider()
    {
        var ride = Proxy();

        Assert.Equal(Rider, ride.RiderIdentity);
        Assert.Equal(CallParty.Rider, ride.PartyFor(Rider));
        Assert.Equal(CallParty.Driver, ride.PartyFor(Driver));
    }

    [Fact]
    public void A_proxy_booker_is_not_a_call_participant()
    {
        // The fence, stated once: P-05 binds the driver to the person in the vehicle, and admitting
        // the booker to the same room is the one thing it forbids. They keep the ride detail, the
        // tracking link and support; they do not get a voice path to the driver.
        Assert.Null(Proxy().PartyFor(Booker));
    }

    [Fact]
    public void There_is_no_fallback_from_rider_to_booker()
    {
        // An unregistered proxy rider (P-03) has no account to admit. A `?? BookerId` here would be
        // the exact bug P-05 exists to prevent, and it would only show up on third-party bookings.
        var ride = ProxyWithUnregisteredRider();

        Assert.Null(ride.RiderIdentity);
        Assert.Null(ride.PartyFor(Booker));
        Assert.Null(ride.PartyFor(Rider));
        Assert.Equal(CallParty.Driver, ride.PartyFor(Driver));
    }

    [Fact]
    public void On_an_ordinary_ride_the_booker_is_the_rider()
    {
        var ride = Ordinary();

        Assert.Equal(Booker, ride.RiderIdentity);
        Assert.Equal(CallParty.Rider, ride.PartyFor(Booker));
    }

    [Fact]
    public void A_stranger_is_never_a_participant()
    {
        Assert.Null(Ordinary().PartyFor(Stranger));
        Assert.Null(Proxy().PartyFor(Stranger));
    }

    [Theory]
    [InlineData("Paid")]
    [InlineData("CashSettled")]
    [InlineData("CashOnDeliveryCollected")]
    [InlineData("Disputed")]
    [InlineData("CancelledByRiderBeforeAccept")]
    [InlineData("CancelledByRiderAfterAccept")]
    [InlineData("CancelledByDriver")]
    [InlineData("ExpiredNoDriver")]
    [InlineData("NoShowRider")]
    [InlineData("NoShowDriver")]
    public void Every_state_a_ride_never_leaves_is_terminal(string state) =>
        Assert.True(Ordinary(state).IsTerminal);

    [Theory]
    [InlineData("Accepted")]
    [InlineData("DriverArrived")]
    [InlineData("InProgress")]
    [InlineData("PaymentPending")]
    [InlineData("Completed")]
    public void A_ride_still_running_is_not(string state) => Assert.False(Ordinary(state).IsTerminal);

    [Fact]
    public void Completed_is_deliberately_not_terminal()
    {
        // The ride still owes a payment, and the driver and passenger are standing next to each
        // other. "My driver just left with my bag" is exactly the call this service carries, and a
        // token refused at Completed would be refused in the ninety seconds it is most needed.
        Assert.False(RideStates.IsTerminal("Completed"));
        Assert.False(RideStates.IsTerminal("PaymentPending"));
    }
}
