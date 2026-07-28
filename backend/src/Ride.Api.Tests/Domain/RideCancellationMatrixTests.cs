using MageRide.Ride.Domain;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// D5' §7 and ADD §11.12, row for row.
/// </summary>
/// <remarks>
/// The matrix is the part of this component a reader is most likely to get wrong, so every printed
/// row is restated here as From/Trigger → To + Penalty and checked against the table the service
/// actually consults. <c>CancellationMatrixTests</c> is the same list driven end to end through
/// HTTP; this one is what says the table itself is right.
/// </remarks>
public sealed class RideCancellationMatrixTests
{
    /// <summary>Every row D5' §7's table prints, in its order.</summary>
    public static TheoryData<string, RideCancellationTrigger, string, RidePenaltyBasis> SpecRows() => new()
    {
        // | Requested/Matching | Rider cancel | CancelledByRiderBeforeAccept | None (US-6A.9) |
        { "Requested", RideCancellationTrigger.RiderCancel, "CancelledByRiderBeforeAccept", RidePenaltyBasis.None },
        { "Matching", RideCancellationTrigger.RiderCancel, "CancelledByRiderBeforeAccept", RidePenaltyBasis.None },

        // | Matching | no driver 2-min/N rounds | ExpiredNoDriver | None |
        { "Matching", RideCancellationTrigger.NoDriverFound, "ExpiredNoDriver", RidePenaltyBasis.None },

        // | Accepted | Rider cancel | CancelledByRiderAfterAccept | Rs 50 (D-05) |
        { "Accepted", RideCancellationTrigger.RiderCancel, "CancelledByRiderAfterAccept", RidePenaltyBasis.RiderCancellation },

        // | Accepted | Driver cancel | CancelledByDriver | reputation hit, brief delist |
        { "Accepted", RideCancellationTrigger.DriverCancel, "CancelledByDriver", RidePenaltyBasis.None },

        // | Accepted | Driver LWT>60s | CancelledByDriver(system) | same |
        { "Accepted", RideCancellationTrigger.DriverOfflineGraceExpired, "CancelledByDriver", RidePenaltyBasis.None },

        // | DriverArrived | Rider no-show 5min+2 SMS | NoShowRider | Rs 100 + driver comp base/2 |
        { "DriverArrived", RideCancellationTrigger.RiderNoShow, "NoShowRider", RidePenaltyBasis.RiderNoShow },

        // | DriverArrived | Driver LWT>120s | CancelledByDriver | reputation hit |
        { "DriverArrived", RideCancellationTrigger.DriverOfflineGraceExpired, "CancelledByDriver", RidePenaltyBasis.None },

        // | Accepted/DriverArrived | never reaches pickup, grace exceeded | NoShowDriver | reputation hit |
        { "Accepted", RideCancellationTrigger.DriverNoShow, "NoShowDriver", RidePenaltyBasis.None },
        { "DriverArrived", RideCancellationTrigger.DriverNoShow, "NoShowDriver", RidePenaltyBasis.None },

        // | InProgress | Rider cancel | CancelledByRiderAfterAccept | full fare |
        { "InProgress", RideCancellationTrigger.RiderCancel, "CancelledByRiderAfterAccept", RidePenaltyBasis.FullFare },

        // | InProgress | Driver LWT>5min, GPS stalled | Disputed | manual review |
        { "InProgress", RideCancellationTrigger.DriverOfflineGraceExpired, "Disputed", RidePenaltyBasis.None },

        // ADD §11.12 adds one row D5' §7 leaves out.
        // | InProgress | Driver taps Cancel | CancelledByDriver | Reputation hit + escalation |
        { "InProgress", RideCancellationTrigger.DriverCancel, "CancelledByDriver", RidePenaltyBasis.None },
    };

    [Theory]
    [MemberData(nameof(SpecRows))]
    public void Every_printed_row_resolves_as_printed(
        string from, RideCancellationTrigger trigger, string to, RidePenaltyBasis penalty)
    {
        Assert.True(RideCancellationMatrix.TryResolve(from, trigger, out var outcome));
        Assert.Equal(to, outcome.ToState);
        Assert.Equal(penalty, outcome.Penalty);
    }

    /// <summary>
    /// The "Events emitted" column. Every terminal has exactly one primary event and the matrix
    /// derives it from where the ride lands.
    /// </summary>
    [Theory]
    [InlineData("Requested", RideCancellationTrigger.RiderCancel, "ride.cancelled")]
    [InlineData("Accepted", RideCancellationTrigger.RiderCancel, "ride.cancelled")]
    [InlineData("Accepted", RideCancellationTrigger.DriverCancel, "ride.cancelled")]
    [InlineData("Matching", RideCancellationTrigger.NoDriverFound, "ride.expired_no_driver")]
    [InlineData("DriverArrived", RideCancellationTrigger.RiderNoShow, "ride.no_show_rider")]
    [InlineData("Accepted", RideCancellationTrigger.DriverNoShow, "ride.no_show_driver")]
    [InlineData("InProgress", RideCancellationTrigger.DriverOfflineGraceExpired, "ride.disputed")]
    public void The_event_matches_the_matrix_events_column(
        string from, RideCancellationTrigger trigger, string eventType)
    {
        Assert.True(RideCancellationMatrix.TryResolve(from, trigger, out var outcome));
        Assert.Equal(eventType, outcome.EventType);
    }

    /// <summary>
    /// AL-16, stated as a property of the table: exactly the post-acceptance rider cancels count.
    /// A pre-acceptance one never does (US-6A.9), and neither does anything the platform decided —
    /// a passenger whose driver went offline has done nothing wrong.
    /// </summary>
    [Fact]
    public void Only_post_acceptance_rider_cancels_count_toward_the_booking_disable()
    {
        foreach (var (state, trigger, outcome) in RideCancellationMatrix.All)
        {
            var expected = trigger == RideCancellationTrigger.RiderCancel
                && outcome.ToState == RideStates.CancelledByRiderAfterAccept;

            Assert.Equal(expected, outcome.CountsTowardBookingDisable);

            if (outcome.CountsTowardBookingDisable)
            {
                Assert.Contains(state, new[] { "Accepted", "DriverArrived", "InProgress" });
            }
        }
    }

    /// <summary>
    /// The reputation hit lands on the driver-side rows and nowhere else. Charging it to a ride the
    /// rider abandoned would down-level a driver for somebody else's decision.
    /// </summary>
    [Theory]
    [InlineData("Accepted", RideCancellationTrigger.DriverCancel, true)]
    [InlineData("Accepted", RideCancellationTrigger.DriverOfflineGraceExpired, true)]
    [InlineData("DriverArrived", RideCancellationTrigger.DriverNoShow, true)]
    [InlineData("Accepted", RideCancellationTrigger.RiderCancel, false)]
    [InlineData("DriverArrived", RideCancellationTrigger.RiderNoShow, false)]
    [InlineData("Matching", RideCancellationTrigger.NoDriverFound, false)]
    // A dispute is a question, not a verdict: §11.12 sends it to manual review rather than to
    // reputation-svc, so nothing is counted against the driver until somebody has looked.
    [InlineData("InProgress", RideCancellationTrigger.DriverOfflineGraceExpired, false)]
    public void The_reputation_hit_follows_the_matrix(string from, RideCancellationTrigger trigger, bool hit)
    {
        Assert.True(RideCancellationMatrix.TryResolve(from, trigger, out var outcome));
        Assert.Equal(hit, outcome.ReputationHit);
    }

    /// <summary>Every cell lands on a terminal state. There is no such thing as a partial cancel.</summary>
    [Fact]
    public void Every_outcome_is_terminal()
    {
        foreach (var (_, _, outcome) in RideCancellationMatrix.All)
        {
            Assert.True(
                RideStates.IsTerminal(outcome.ToState),
                $"{outcome.ToState} is not terminal, so the matrix would leave the ride running.");
        }
    }

    /// <summary>
    /// Combinations the matrix must not have a row for. Each is a way the aggregate could be moved
    /// somewhere the specs never draw.
    /// </summary>
    [Theory]
    // A ride nobody has accepted has no driver to cancel it or to fail to arrive.
    [InlineData("Requested", RideCancellationTrigger.DriverCancel)]
    [InlineData("Matching", RideCancellationTrigger.DriverNoShow)]
    [InlineData("Offered", RideCancellationTrigger.DriverOfflineGraceExpired)]
    // The cascade can only run out while it is running.
    [InlineData("Requested", RideCancellationTrigger.NoDriverFound)]
    [InlineData("Accepted", RideCancellationTrigger.NoDriverFound)]
    // The rider cannot be a no-show before anyone has arrived, nor after the trip started.
    [InlineData("Accepted", RideCancellationTrigger.RiderNoShow)]
    [InlineData("InProgress", RideCancellationTrigger.RiderNoShow)]
    // Once the fare is owed, nobody cancels: §11.14 makes it a dispute.
    [InlineData("PaymentPending", RideCancellationTrigger.RiderCancel)]
    [InlineData("PaymentPending", RideCancellationTrigger.DriverCancel)]
    // Completed is transient — the ride is already through it inside `complete`'s transaction.
    [InlineData("Completed", RideCancellationTrigger.RiderCancel)]
    // And a finished ride cannot be re-finished.
    [InlineData("Paid", RideCancellationTrigger.RiderCancel)]
    [InlineData("CancelledByDriver", RideCancellationTrigger.RiderCancel)]
    public void Combinations_the_specs_do_not_draw_have_no_row(string from, RideCancellationTrigger trigger) =>
        Assert.False(RideCancellationMatrix.TryResolve(from, trigger, out _));

    [Fact]
    public void An_unknown_state_resolves_to_nothing() =>
        Assert.False(RideCancellationMatrix.TryResolve("Teleported", RideCancellationTrigger.RiderCancel, out _));
}
