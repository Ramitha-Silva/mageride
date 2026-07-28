using MageRide.Ride.Domain;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// The aggregate's vocabulary and every move it permits, against ADD Appendix B.2, D5' §6 and the
/// §11.12 matrix.
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

    /// <summary>
    /// The whole machine, edge for edge. Every entry is traceable to a printed line: the happy path
    /// to D5' §6's diagram, the terminals to the §11.12 matrix, the four money states to R-05.
    /// </summary>
    [Fact]
    public void The_service_implements_exactly_the_specified_machine()
    {
        (string From, string To)[] expected =
        [
            // --- D5' §6's happy path -------------------------------------------------------
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

            // --- §11.12's terminals --------------------------------------------------------
            ("Requested", "CancelledByRiderBeforeAccept"),
            ("Matching", "CancelledByRiderBeforeAccept"),
            ("Offered", "CancelledByRiderBeforeAccept"),
            ("Matching", "ExpiredNoDriver"),
            ("Accepted", "CancelledByRiderAfterAccept"),
            ("DriverArrived", "CancelledByRiderAfterAccept"),
            ("InProgress", "CancelledByRiderAfterAccept"),
            ("Accepted", "CancelledByDriver"),
            ("DriverArrived", "CancelledByDriver"),
            ("InProgress", "CancelledByDriver"),
            ("Accepted", "NoShowDriver"),
            ("DriverArrived", "NoShowDriver"),
            ("DriverArrived", "NoShowRider"),
            ("InProgress", "Disputed"),

            // --- R-05's payment terminals --------------------------------------------------
            ("PaymentPending", "Paid"),
            ("PaymentPending", "CashSettled"),
            ("PaymentPending", "CashOnDeliveryCollected"),
            ("PaymentPending", "Disputed"),
        ];

        Assert.Equal(
            expected.Order(),
            RideTransitions.All.Order());
    }

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
    // Nothing skips the machine.
    [InlineData("Requested", "Accepted")]
    [InlineData("Accepted", "Completed")]
    [InlineData("Completed", "InProgress")]
    // A rider cancel is pre- or post-acceptance, never the other one.
    [InlineData("Matching", "CancelledByRiderAfterAccept")]
    [InlineData("Accepted", "CancelledByRiderBeforeAccept")]
    // ExpiredNoDriver means the cascade ran out, which can only have happened while Matching.
    [InlineData("Requested", "ExpiredNoDriver")]
    [InlineData("Offered", "ExpiredNoDriver")]
    // A rider cannot be a no-show before the driver has arrived.
    [InlineData("Accepted", "NoShowRider")]
    [InlineData("InProgress", "NoShowRider")]
    // A driver who is already driving the passenger cannot have failed to reach them.
    [InlineData("InProgress", "NoShowDriver")]
    // R-05: only a ride awaiting payment settles, and only into the four money states.
    [InlineData("Completed", "Paid")]
    [InlineData("InProgress", "Paid")]
    [InlineData("PaymentPending", "Completed")]
    // Nothing may be cancelled once the money is owed — that is a dispute (§11.14).
    [InlineData("PaymentPending", "CancelledByRiderAfterAccept")]
    [InlineData("PaymentPending", "CancelledByDriver")]
    public void Moves_the_machine_does_not_draw_are_refused(string from, string to) =>
        Assert.False(RideTransitions.IsAllowed(from, to));

    /// <summary>
    /// The two tables that describe the same machine agree: every outcome the §11.12 matrix can
    /// produce is an edge <see cref="RideTransitions"/> draws.
    /// </summary>
    [Fact]
    public void Every_matrix_outcome_is_a_move_the_machine_allows()
    {
        foreach (var (state, trigger, outcome) in RideCancellationMatrix.All)
        {
            Assert.True(
                RideTransitions.IsAllowed(state, outcome.ToState),
                $"The matrix takes {state} + {trigger} to {outcome.ToState}, which the state machine does not draw.");
        }
    }

    /// <summary>R-16's four windows, and only those four.</summary>
    [Fact]
    public void The_offline_graces_are_the_four_R16_windows()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), RideGracePolicy.For("Accepted"));
        Assert.Equal(TimeSpan.FromSeconds(120), RideGracePolicy.For("DriverArrived"));
        Assert.Equal(TimeSpan.FromMinutes(5), RideGracePolicy.For("InProgress"));
        Assert.Equal(TimeSpan.FromMinutes(10), RideGracePolicy.For("PaymentPending"));

        // Before acceptance nobody is assigned, so a last will takes nothing away; after a terminal
        // there is nothing left to take.
        Assert.Null(RideGracePolicy.For("Requested"));
        Assert.Null(RideGracePolicy.For("Matching"));
        Assert.Null(RideGracePolicy.For("Offered"));
        Assert.Null(RideGracePolicy.For("Completed"));
        Assert.Null(RideGracePolicy.For("Paid"));
    }

    /// <summary>
    /// ride-svc claims four of <c>ck_timers_kind</c>'s eight. <c>offer_expiry</c> is dispatch-svc's
    /// (ADD §6, C023) and the other three are C037's — a claim that widened silently would take
    /// another service's backstop.
    /// </summary>
    [Fact]
    public void The_timer_kinds_this_service_owns_are_the_four_it_arms()
    {
        Assert.Equal(
            new[] { "arrival_grace", "no_show", "offline_grace", "payment_pending" }.Order(StringComparer.Ordinal),
            RideTimerKinds.Owned.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(RideTimerKinds.OfferExpiry, (IReadOnlySet<string>)RideTimerKinds.Owned);
    }

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
