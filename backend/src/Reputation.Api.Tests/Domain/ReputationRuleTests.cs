using MageRide.Reputation.Configuration;
using MageRide.Reputation.Domain;

namespace MageRide.Reputation.Tests.Domain;

/// <summary>
/// The D5' block-state rules, asserted against the spec lines they come from.
/// </summary>
/// <remarks>
/// Pure: no container, no clock, no database. This is the file to read next to
/// <c>D5_mageride_business_logic.md</c> §4.2 and §7.2 — everything else in the component is
/// plumbing around this table.
/// </remarks>
public sealed class ReputationRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly ReputationOptions Options = new();

    [Fact]
    public void Nothing_counted_is_OK()
    {
        var decision = ReputationRules.Derive(CounterRow.Empty(Guid.NewGuid(), Now), Options, Now);

        Assert.Equal(BlockStates.Ok, decision.State);
        Assert.Null(decision.ExpiresAt);
    }

    /// <summary>D5' §7.2 / US-6A.10b / AL-16: three consecutive post-acceptance cancels.</summary>
    [Theory]
    [InlineData(0, BlockStates.Ok)]
    [InlineData(1, BlockStates.Ok)]
    [InlineData(2, BlockStates.Warn)]
    [InlineData(3, BlockStates.BookingDisabled)]
    [InlineData(9, BlockStates.BookingDisabled)]
    public void Consecutive_cancellations_disable_booking_at_three(int cancellations, string expected)
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with { CancellationsContinuous = cancellations };

        Assert.Equal(expected, ReputationRules.Derive(counters, Options, Now).State);
    }

    /// <summary>D5' §4.2 / US-12.6: three confirmed reports, and the delisting is time-boxed.</summary>
    [Theory]
    [InlineData(2, BlockStates.Warn)]
    [InlineData(3, BlockStates.Delisted)]
    public void Confirmed_reports_delist_at_three(int reports, string expected)
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with { ReportsTotal = reports };
        var decision = ReputationRules.Derive(counters, Options, Now);

        Assert.Equal(expected, decision.State);

        if (expected == BlockStates.Delisted)
        {
            // "temporary delisting … time-boxed" — a delisting with no expiry would be a ban, which
            // D5' §4.2 explicitly is not what three reports buys.
            Assert.Equal(Now + Options.ReportDelistDuration, decision.ExpiresAt);
            Assert.Equal(BlockReasons.ReportsDelist, decision.Reason);
        }
    }

    /// <summary>A delisting outranks a booking-disable: D5' §3.2 excludes on both, and the more
    /// severe one is what an admin should see.</summary>
    [Fact]
    public void Delisting_outranks_booking_disable()
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with
        {
            CancellationsContinuous = 5,
            ReportsTotal = 4,
        };

        Assert.Equal(BlockStates.Delisted, ReputationRules.Derive(counters, Options, Now).State);
    }

    /// <summary>ADD §11.12: a driver-side cancel earns a brief delist on the first offence.</summary>
    [Fact]
    public void Driver_cancel_delists_briefly_on_the_first_one()
    {
        var fact = Fact(IntakeKinds.Cancellation, SubjectRoles.Driver);
        var imposed = ReputationRules.DeriveFromEvent(fact, Options, Now);

        Assert.NotNull(imposed);
        Assert.Equal(BlockStates.Delisted, imposed!.Value.State);
        Assert.Equal(BlockReasons.DriverCancelDelist, imposed.Value.Reason);
        Assert.Equal(Now + Options.DriverCancelDelistDuration, imposed.Value.ExpiresAt);
    }

    /// <summary>A passenger cancel moves the AL-16 run and imposes nothing by itself.</summary>
    [Fact]
    public void Passenger_cancel_imposes_no_immediate_state()
    {
        Assert.Null(ReputationRules.DeriveFromEvent(Fact(IntakeKinds.Cancellation, SubjectRoles.Passenger), Options, Now));
    }

    [Fact]
    public void An_uncounted_fact_imposes_nothing()
    {
        var fact = Fact(IntakeKinds.Cancellation, SubjectRoles.Driver) with { Counted = false };

        Assert.Null(ReputationRules.DeriveFromEvent(fact, Options, Now));
    }

    /// <summary>D5' §7.2: "Counter resets to 0 on any completed ride." The run, and only the run.</summary>
    [Fact]
    public void A_completed_ride_resets_the_run_and_nothing_else()
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with
        {
            CancellationsContinuous = 2,
            ReportsTotal = 1,
            NoShows = 1,
        };

        var after = ReputationRules.Apply(counters, Fact(IntakeKinds.Completion, SubjectRoles.Passenger), Options, Now);

        Assert.Equal(0, after.CancellationsContinuous);
        Assert.Equal(1, after.ReportsTotal);
        Assert.Equal(1, after.NoShows);
    }

    /// <summary>D-04's rolling window clears the two window-scoped counters and leaves the run.</summary>
    [Fact]
    public void The_rolling_window_clears_reports_and_no_shows_but_not_the_run()
    {
        var started = Now - Options.CounterWindow - TimeSpan.FromDays(1);
        var counters = new CounterRow(Guid.NewGuid(), 2, 2, 2, started, started);

        var rolled = ReputationRules.Roll(counters, Options, Now);

        Assert.Equal(0, rolled.ReportsTotal);
        Assert.Equal(0, rolled.NoShows);

        // A time-based reset of the run would let a passenger wait out a strike rather than
        // complete a ride to clear it (D5' §7.2 gives exactly one reset condition).
        Assert.Equal(2, rolled.CancellationsContinuous);
        Assert.Equal(Now, rolled.WindowStartedAt);
    }

    [Fact]
    public void An_unelapsed_window_is_left_alone()
    {
        var started = Now - TimeSpan.FromDays(1);
        var counters = new CounterRow(Guid.NewGuid(), 0, 2, 0, started, started);

        Assert.Same(counters, ReputationRules.Roll(counters, Options, Now));
    }

    /// <summary>The window rolls before the delta, so a report after a long gap starts at 1.</summary>
    [Fact]
    public void A_report_after_the_window_elapsed_starts_a_new_window_at_one()
    {
        var started = Now - Options.CounterWindow - TimeSpan.FromDays(1);
        var counters = new CounterRow(Guid.NewGuid(), 0, 2, 0, started, started);

        var after = ReputationRules.Apply(counters, Fact(IntakeKinds.Report, SubjectRoles.Driver), Options, Now);

        Assert.Equal(1, after.ReportsTotal);
        Assert.Equal(Now, after.WindowStartedAt);
    }

    /// <summary>A manual override survives a recompute (migration 0804's whole reason).</summary>
    [Fact]
    public void A_manual_state_is_not_recomputed_away()
    {
        var existing = new BlockStateRow(
            Guid.NewGuid(), BlockStates.Ok, null, BlockSources.Manual, BlockReasons.Manual, Guid.NewGuid(), Now);

        var derived = new BlockDecision(BlockStates.Delisted, BlockReasons.ReportsDelist, Now.AddDays(7));

        Assert.Equal(BlockStates.Ok, ReputationRules.Resolve(existing, derived, null, Now).State);
    }

    /// <summary>An expired manual override stops holding.</summary>
    [Fact]
    public void An_expired_manual_state_yields_to_the_rules()
    {
        var existing = new BlockStateRow(
            Guid.NewGuid(), BlockStates.Ok, Now.AddMinutes(-1), BlockSources.Manual, BlockReasons.Manual,
            Guid.NewGuid(), Now);

        var derived = new BlockDecision(BlockStates.BookingDisabled, BlockReasons.CancellationsDisabled, null);

        Assert.Equal(BlockStates.BookingDisabled, ReputationRules.Resolve(existing, derived, null, Now).State);
    }

    /// <summary>A time box that has not run out is not lowered by the next recompute.</summary>
    [Fact]
    public void An_unexpired_penalty_is_not_lowered()
    {
        var existing = new BlockStateRow(
            Guid.NewGuid(), BlockStates.Delisted, Now.AddMinutes(20), BlockSources.Auto,
            BlockReasons.DriverCancelDelist, null, Now);

        var derived = new BlockDecision(BlockStates.Warn, BlockReasons.ApproachingThreshold, null);

        var resolved = ReputationRules.Resolve(existing, derived, null, Now);

        Assert.Equal(BlockStates.Delisted, resolved.State);
        Assert.Equal(Now.AddMinutes(20), resolved.ExpiresAt);
    }

    /// <summary>An equally severe new penalty refreshes the deadline rather than riding out the old.</summary>
    [Fact]
    public void A_second_driver_cancel_restarts_the_clock()
    {
        var existing = new BlockStateRow(
            Guid.NewGuid(), BlockStates.Delisted, Now.AddMinutes(5), BlockSources.Auto,
            BlockReasons.DriverCancelDelist, null, Now);

        var imposed = new BlockDecision(
            BlockStates.Delisted, BlockReasons.DriverCancelDelist, Now + Options.DriverCancelDelistDuration);

        var resolved = ReputationRules.Resolve(existing, BlockDecision.Clear, imposed, Now);

        Assert.Equal(Now + Options.DriverCancelDelistDuration, resolved.ExpiresAt);
    }

    /// <summary>D5' §4.2: the third report costs a level, the fourth in the same window does not.</summary>
    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    public void The_report_that_crosses_the_threshold_costs_a_level(int reportsAfter, int expected)
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with { ReportsTotal = reportsAfter };

        Assert.Equal(
            expected,
            ReputationRules.LevelPenalty(Fact(IntakeKinds.Report, SubjectRoles.Driver), counters, Options));
    }

    /// <summary>US-6A.7: every driver no-show costs a level.</summary>
    [Fact]
    public void Every_driver_no_show_costs_a_level()
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with { NoShows = 1 };

        Assert.Equal(1, ReputationRules.LevelPenalty(Fact(IntakeKinds.NoShow, SubjectRoles.Driver), counters, Options));
    }

    /// <summary>A passenger has no level, so no fact of theirs can cost one.</summary>
    [Fact]
    public void A_passenger_fact_never_costs_a_level()
    {
        var counters = CounterRow.Empty(Guid.NewGuid(), Now) with { NoShows = 5, ReportsTotal = 5 };

        Assert.Equal(0, ReputationRules.LevelPenalty(Fact(IntakeKinds.NoShow, SubjectRoles.Passenger), counters, Options));
        Assert.Equal(0, ReputationRules.LevelPenalty(Fact(IntakeKinds.Report, SubjectRoles.Passenger), counters, Options));
    }

    /// <summary>D5' §3.2's hard gate, spelled once so every caller applies the same one.</summary>
    [Theory]
    [InlineData(BlockStates.Ok, true)]
    [InlineData(BlockStates.Warn, true)]
    [InlineData(BlockStates.BookingDisabled, false)]
    [InlineData(BlockStates.Delisted, false)]
    public void Dispatch_is_gated_on_the_last_two_states_only(string state, bool allowed) =>
        Assert.Equal(allowed, BlockStates.AllowsDispatch(state));

    private static ReputationFact Fact(string kind, string role) =>
        new(
            DedupeKey: $"{kind}:{Guid.NewGuid()}",
            Kind: kind,
            SubjectId: Guid.NewGuid(),
            SubjectRole: role,
            RideId: Guid.NewGuid(),
            Source: IntakeSources.RideEvents);
}
