using MageRide.Reputation.Configuration;

namespace MageRide.Reputation.Domain;

/// <summary>
/// The D5' block-state rules, as a pure function of the counters and the clock.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static because this is the part of the component that has to be *arguable*: every rule
/// below is one line of a spec, and a table that can be read next to the spec is worth more than a
/// service method that also opens transactions. Everything stateful — reading counters, writing
/// block states, emitting events — is <see cref="Counters.ReputationService"/>'s.
/// </para>
/// <para>
/// <b>The rules, and where they come from:</b>
/// </para>
/// <list type="table">
///   <item>
///     <term>3 consecutive post-acceptance cancels → BOOKING_DISABLED</term>
///     <description>D5' §7.2, US-6A.10b, AL-16. Pre-acceptance cancels never count; a completed
///     ride resets the run to 0.</description>
///   </item>
///   <item>
///     <term>3 confirmed reports → DELISTED (time-boxed) + level−1</term>
///     <description>D5' §4.2 "3 passenger reports → level −= 1 + temporary delisting", US-12.6.</description>
///   </item>
///   <item>
///     <term>Driver-side cancel → brief DELISTED</term>
///     <description>ADD §11.12, rows "Accepted | Driver cancel" and "Accepted | Driver LWT&gt;60s"
///     — "reputation hit, brief delist", both with the same effect.</description>
///   </item>
///   <item>
///     <term>No-show on an accepted scheduled ride → level−1</term>
///     <description>D5' §4.2, US-6A.7. It moves the level, never the block state on its own.</description>
///   </item>
///   <item>
///     <term>WARN</term>
///     <description><b>No spec produces it.</b> The enum exists and no rule fills it, so it is
///     "one short of a hard threshold" and every bound is configuration.</description>
///   </item>
/// </list>
/// </remarks>
public static class ReputationRules
{
    /// <summary>
    /// The state the counters alone imply. The severity order is the one D5' §3.2 cares about:
    /// a delisting outranks a booking-disable outranks a warning.
    /// </summary>
    public static BlockDecision Derive(CounterRow counters, ReputationOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(options);

        if (counters.ReportsTotal >= options.ReportDelistThreshold)
        {
            // Time-boxed: D5' §4.2 says "temporary". The box is what makes the strike servable —
            // when it lapses the window that produced it is reset with it (see
            // ReputationService.SettleExpiredAsync), so the same three reports cannot delist twice.
            return new BlockDecision(
                BlockStates.Delisted, BlockReasons.ReportsDelist, now + options.ReportDelistDuration);
        }

        if (counters.CancellationsContinuous >= options.CancellationDisableThreshold)
        {
            // AL-16's cooldown. Not the only way out — a completed ride resets the run (D5' §7.2)
            // and an admin can reinstate — but it is the one this service can apply by itself.
            return new BlockDecision(
                BlockStates.BookingDisabled, BlockReasons.CancellationsDisabled,
                now + options.BookingDisableCooldown);
        }

        var warns =
            counters.CancellationsContinuous >= options.CancellationWarnThreshold ||
            counters.ReportsTotal >= options.ReportWarnThreshold ||
            counters.NoShows >= options.NoShowWarnThreshold;

        return warns
            ? new BlockDecision(BlockStates.Warn, BlockReasons.ApproachingThreshold, null)
            : BlockDecision.Clear;
    }

    /// <summary>
    /// The state a single event imposes regardless of the counters, or <see langword="null"/> when
    /// it imposes none. Today that is only §11.12's brief delist on a driver-side cancel.
    /// </summary>
    /// <remarks>
    /// This exists because §11.12's rule is about the *event*, not about a tally: a driver's first
    /// cancel earns the delist, so no counter threshold can express it. It is merged with
    /// <see cref="Derive"/> by <see cref="Resolve"/> rather than replacing it, so a driver who is
    /// also over the report threshold keeps the longer penalty.
    /// </remarks>
    public static BlockDecision? DeriveFromEvent(
        ReputationFact fact, ReputationOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(options);

        if (!fact.Counted)
        {
            return null;
        }

        return fact is { Kind: IntakeKinds.Cancellation, SubjectRole: SubjectRoles.Driver }
            ? new BlockDecision(
                BlockStates.Delisted, BlockReasons.DriverCancelDelist,
                now + options.DriverCancelDelistDuration)
            : null;
    }

    /// <summary>
    /// What to write, given what is already there and what the rules now say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules, in order:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>A manual state that still holds wins.</b> An admin lifting a block would otherwise be
    ///     undone by the next report already sitting in the queue — the override would work for as
    ///     long as it took one message to arrive, which is worse than not having one.
    ///   </description></item>
    ///   <item><description>
    ///     <b>An unexpired <em>event-imposed</em> penalty is not lowered.</b> A driver who earned a
    ///     30-minute delist keeps it even though their counters only imply WARN; otherwise the very
    ///     next counted fact would lift the penalty the previous one imposed. A counter-derived
    ///     state is deliberately not protected this way — see
    ///     <see cref="BlockReasons.SurvivesRecompute"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Otherwise the most severe candidate wins, and an equal-severity candidate refreshes the
    ///     time box (a second driver cancel restarts the 30 minutes rather than riding out the
    ///     first one's).
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static BlockDecision Resolve(
        BlockStateRow? existing, BlockDecision derived, BlockDecision? fromEvent, DateTimeOffset now)
    {
        if (existing is { Source: BlockSources.Manual } manual && manual.HoldsAt(now))
        {
            return new BlockDecision(manual.State, manual.Reason ?? BlockReasons.Manual, manual.ExpiresAt);
        }

        var candidate = derived;

        if (fromEvent is { } imposed && imposed.Severity >= candidate.Severity)
        {
            candidate = imposed;
        }

        // A held time box only survives if it is strictly more severe: an equal one is replaced so
        // its deadline is refreshed, and a less severe one has nothing left to serve. And only an
        // event-imposed one survives at all — see BlockReasons.SurvivesRecompute.
        if (existing is { } current &&
            current.ExpiresAt is not null &&
            current.HoldsAt(now) &&
            BlockReasons.SurvivesRecompute(current.Reason) &&
            BlockStates.Severity(current.State) > candidate.Severity)
        {
            return new BlockDecision(
                current.State, current.Reason ?? BlockReasons.Clear, current.ExpiresAt);
        }

        return candidate;
    }

    /// <summary>
    /// Whether a fact costs the subject a level, and by how much (D5' §4.2).
    /// </summary>
    /// <remarks>
    /// Only two things take a level away and both are driver-side: crossing the report threshold,
    /// and a no-show on an accepted ride (US-6A.7). A passenger has no level, so their facts return
    /// 0 — the caller does not have to know which counters belong to which role.
    /// </remarks>
    public static int LevelPenalty(ReputationFact fact, CounterRow after, ReputationOptions options)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(options);

        if (!fact.Counted || fact.SubjectRole != SubjectRoles.Driver)
        {
            return 0;
        }

        return fact.Kind switch
        {
            // Exactly at the threshold, not at-or-above: the fact that took the count from 2 to 3
            // costs the level, and the fourth report inside the same window does not cost another.
            // A fresh window is what makes the next one bite (D-04's rolling reset).
            IntakeKinds.Report when after.ReportsTotal == options.ReportDelistThreshold => 1,
            IntakeKinds.NoShow => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Applies a fact to a counter row, having first rolled the window if it has elapsed.
    /// </summary>
    /// <remarks>
    /// The window rolls <em>before</em> the delta so a report arriving after a 30-day gap starts a
    /// new window at 1 rather than being the third strike of a window nobody remembers.
    /// <c>cancellations_continuous</c> deliberately survives the roll — D5' §7.2 gives it one reset
    /// condition, a completed ride, and a time-based one would let a passenger wait out a strike.
    /// </remarks>
    public static CounterRow Apply(
        CounterRow current, ReputationFact fact, ReputationOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(options);

        var rolled = Roll(current, options, now);

        if (!fact.Counted)
        {
            return rolled;
        }

        return fact.Kind switch
        {
            IntakeKinds.Cancellation => rolled with
            {
                CancellationsContinuous = rolled.CancellationsContinuous + 1,
            },
            IntakeKinds.NoShow => rolled with { NoShows = rolled.NoShows + 1 },
            IntakeKinds.Report => rolled with { ReportsTotal = rolled.ReportsTotal + 1 },

            // D5' §7.2: "Counter resets to 0 on any completed ride." The run, and only the run —
            // a completed ride is not an answer to a confirmed report.
            IntakeKinds.Completion => rolled with { CancellationsContinuous = 0 },
            _ => rolled,
        };
    }

    /// <summary>
    /// Clears the window-scoped counters when the window has elapsed (D-04 "rolling-window reset").
    /// </summary>
    public static CounterRow Roll(CounterRow current, ReputationOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);

        if (current.WindowStartedAt is { } started && now - started < options.CounterWindow)
        {
            return current;
        }

        return current with { ReportsTotal = 0, NoShows = 0, WindowStartedAt = now };
    }
}
