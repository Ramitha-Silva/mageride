using MageRide.Ride.Domain;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// The aggregate's vocabulary and the moves this slice permits, against ADD Appendix B.2 and
/// D5' §6. These are the fences C032/C037/C049 will widen, so they are asserted rather than
/// described.
/// </summary>
public sealed class RideStateMachineTests
{
    /// <summary>D5' §6 and the <c>ck_rides_state</c> CHECK landed by C004, character for character.</summary>
    private static readonly string[] SpecStates =
    [
        "Requested", "Matching", "Offered", "Accepted", "DriverArrived", "InProgress", "Completed",
        "PaymentPending", "Paid", "CashSettled", "CashOnDeliveryCollected", "Disputed",
        "CancelledByRiderBeforeAccept", "CancelledByRiderAfterAccept", "CancelledByDriver",
        "ExpiredNoDriver", "NoShowRider", "NoShowDriver",
    ];

    [Fact]
    public void The_eighteen_states_match_the_spec_exactly()
    {
        Assert.Equal(18, RideStates.All.Count);
        Assert.Equal(
            SpecStates.Order(StringComparer.Ordinal),
            RideStates.All.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Completed is NOT terminal: the ride still owes a payment, and D5' §6 draws
    /// <c>Completed --> PaymentPending</c>. <c>ux_rides_open_passenger</c> exempts it anyway
    /// (C004 note (b)), which is the trap this test exists to keep out of the domain.
    /// </summary>
    [Theory]
    [InlineData("Paid", true)]
    [InlineData("CashSettled", true)]
    [InlineData("CancelledByDriver", true)]
    [InlineData("ExpiredNoDriver", true)]
    [InlineData("NoShowRider", true)]
    [InlineData("Completed", false)]
    [InlineData("PaymentPending", false)]
    [InlineData("Offered", false)]
    public void Terminality_follows_the_state_machine_not_the_open_ride_index(string state, bool terminal)
    {
        Assert.True(RideStates.IsKnown(state));
        Assert.Equal(terminal, RideStates.IsTerminal(state));
    }

    [Fact]
    public void Driver_busy_is_the_four_states_the_O2_index_covers()
    {
        Assert.Equal(
            new[] { "Accepted", "DriverArrived", "InProgress", "PaymentPending" }.Order(StringComparer.Ordinal),
            RideStates.DriverBusy.Order(StringComparer.Ordinal));
    }

    /// <summary>The happy path of the C022 scope, end to end and nothing else.</summary>
    [Fact]
    public void The_slice_implements_exactly_the_happy_path()
    {
        (string From, string To)[] expected =
        [
            ("Requested", "Matching"),
            ("Matching", "Offered"),
            // §11.11's UPDATE guards on state IN ('Matching','Offered'); C015's client table
            // carries the same edge.
            ("Matching", "Accepted"),
            ("Offered", "Accepted"),
            ("Offered", "Matching"),
            ("Accepted", "DriverArrived"),
            ("Accepted", "InProgress"),
            ("DriverArrived", "InProgress"),
            ("InProgress", "Completed"),
            ("Completed", "PaymentPending"),
        ];

        Assert.Equal(
            expected.Order(),
            RideTransitions.All.Order());
    }

    /// <summary>
    /// Every move the slice claims is one D5' §6 draws. The <c>Accepted → InProgress</c> edge is
    /// the single exception and it comes from the contract's `start` description, not from a
    /// liberty taken here (C022 handoff, gap (e)).
    /// </summary>
    [Fact]
    public void Every_permitted_move_lands_on_a_real_state()
    {
        foreach (var (from, to) in RideTransitions.All)
        {
            Assert.True(RideStates.IsKnown(from), $"'{from}' is not one of the eighteen states.");
            Assert.True(RideStates.IsKnown(to), $"'{to}' is not one of the eighteen states.");
            Assert.False(RideStates.IsTerminal(from), $"'{from}' is terminal and cannot be moved out of.");
        }
    }

    [Theory]
    // The cancellation matrix is C032; none of its edges may be reachable from here.
    [InlineData("Accepted", "CancelledByRiderAfterAccept")]
    [InlineData("Matching", "ExpiredNoDriver")]
    [InlineData("DriverArrived", "NoShowRider")]
    // The payment terminals are fare-svc's (R-05, C049/C050).
    [InlineData("PaymentPending", "Paid")]
    [InlineData("PaymentPending", "CashSettled")]
    // And nothing skips the machine.
    [InlineData("Requested", "Accepted")]
    [InlineData("Accepted", "Completed")]
    [InlineData("Completed", "InProgress")]
    public void Moves_outside_the_slice_are_refused(string from, string to) =>
        Assert.False(RideTransitions.IsAllowed(from, to));

    [Fact]
    public void An_unknown_state_is_never_allowed() =>
        Assert.False(RideTransitions.IsAllowed("Requested", "Teleported"));

    [Fact]
    public void Kind_maps_both_ways_across_the_smallint_column()
    {
        foreach (var kind in RideKinds.All)
        {
            Assert.Equal(kind, RideKinds.FromDatabase(RideKinds.ToDatabase(kind)));
        }

        Assert.Equal(0, RideKinds.ToDatabase(RideKinds.Passenger));
        Assert.Equal(1, RideKinds.ToDatabase(RideKinds.Proxy));
        Assert.Equal(2, RideKinds.ToDatabase(RideKinds.Package));
    }

    /// <summary>AL-09: the eight Mode C tiers. `bus` and `train` are Mode A and never bookable.</summary>
    [Theory]
    [InlineData("motorbike", true)]
    [InlineData("three_wheeler", true)]
    [InlineData("mini_truck", true)]
    [InlineData("bus", false)]
    [InlineData("train", false)]
    [InlineData("car", false)]
    [InlineData(null, false)]
    public void Only_the_mode_c_tiers_are_bookable(string? vehicleType, bool bookable) =>
        Assert.Equal(bookable, RideVehicleTypes.IsBookable(vehicleType));
}
