using MageRide.Fanout.Visibility;
using MageRide.Shared.Realtime;

namespace MageRide.Fanout.Tests.Unit;

/// <summary>
/// D6' §5.2's visibility table, asserted directly on the rule rather than through a socket.
/// </summary>
/// <remarks>
/// The integration suites prove that the rule is <em>reached</em> — that a batch really is filtered
/// before it leaves, that a revocation really does remove a group membership. What is proved here is
/// that the rule itself is the one the specification writes, cell by cell, including the boundaries
/// no realistic pipeline test can hit on purpose.
/// </remarks>
public sealed class VisibilityRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void Mode_A_is_public_always()
    {
        // Buses and trains are public infrastructure: there is no entitlement to check and nothing
        // to hide behind (D6' §5.2, "Mode A (bus + train) … always").
        var verdict = Classify(OperatingModes.A, Now, VehicleState.Unknown);

        Assert.Equal(VehicleAudience.Public, verdict.Audience);
    }

    [Fact]
    public void Mode_B_is_never_on_a_public_group()
    {
        // D-23. Not "public unless": a private vehicle has no route to a cell group at all, which is
        // what makes the entitlement check a group join rather than a per-frame test.
        var verdict = Classify(OperatingModes.B, Now, VehicleState.Unknown);

        Assert.Equal(VehicleAudience.Entitled, verdict.Audience);
        Assert.Null(verdict.RemovalReason);
    }

    [Fact]
    public void An_idle_Mode_C_vehicle_is_public()
    {
        // The passenger's whole reason for opening the map: US-7.16 hides engaged three-wheelers,
        // not available ones.
        Assert.Equal(VehicleAudience.Public, Classify(OperatingModes.C, Now, VehicleState.Unknown).Audience);
    }

    [Fact]
    public void An_engaged_Mode_C_vehicle_goes_to_its_ride_and_nowhere_else()
    {
        var rideId = Guid.NewGuid();

        var verdict = Classify(OperatingModes.C, Now, new VehicleState(rideId, null));

        Assert.Equal(VehicleAudience.Ride, verdict.Audience);
        Assert.Equal(rideId, verdict.RideId);
        Assert.Equal(VehicleRemovalReasons.Engaged, verdict.RemovalReason);
    }

    [Fact]
    public void A_sample_older_than_the_freshness_window_is_stale_whatever_its_mode()
    {
        // US-7.17 cuts across every mode. It is also what keeps a replayed backlog off the live map:
        // `veh/{id}/pos/replay` samples reach the same cell stream as live ones and are only
        // distinguishable by their capture instant.
        foreach (var mode in new[] { OperatingModes.A, OperatingModes.B, OperatingModes.C })
        {
            var verdict = Classify(mode, Now - Window - TimeSpan.FromSeconds(1), VehicleState.Unknown);

            Assert.Equal(VehicleAudience.None, verdict.Audience);
            Assert.Equal(VehicleRemovalReasons.Stale, verdict.RemovalReason);
        }
    }

    [Fact]
    public void A_sample_exactly_at_the_window_is_still_fresh()
    {
        // The boundary is "older than", not "at least". A vehicle reporting once a minute against a
        // sixty-second window would otherwise flicker off the map on every other tick.
        Assert.Equal(
            VehicleAudience.Public,
            Classify(OperatingModes.C, Now - Window, VehicleState.Unknown).Audience);
    }

    [Fact]
    public void A_last_will_hides_a_vehicle_whose_newest_sample_is_older_than_it()
    {
        var verdict = Classify(
            OperatingModes.C,
            Now - TimeSpan.FromSeconds(10),
            new VehicleState(null, Now - TimeSpan.FromSeconds(5)));

        Assert.Equal(VehicleAudience.None, verdict.Audience);
        Assert.Equal(VehicleRemovalReasons.Offline, verdict.RemovalReason);
    }

    [Fact]
    public void A_fresher_sample_beats_an_older_last_will_with_no_online_message_needed()
    {
        // The self-healing case, and the reason the mark holds an instant rather than a flag: a
        // device whose session dropped and whose app restarted publishing may never send an
        // `online`, and waiting for one would leave it invisible for the rest of its shift.
        var verdict = Classify(
            OperatingModes.C,
            Now - TimeSpan.FromSeconds(2),
            new VehicleState(null, Now - TimeSpan.FromSeconds(30)));

        Assert.Equal(VehicleAudience.Public, verdict.Audience);
    }

    [Fact]
    public void A_last_will_hides_a_vehicle_that_has_never_reported_at_all()
    {
        var verdict = VehicleVisibilityRules.Classify(
            OperatingModes.C, sampleTs: null, new VehicleState(null, Now), Now, Window);

        Assert.Equal(VehicleAudience.None, verdict.Audience);
        Assert.Equal(VehicleRemovalReasons.Offline, verdict.RemovalReason);
    }

    [Fact]
    public void Staleness_is_decided_before_engagement()
    {
        // An engaged vehicle that has stopped reporting has no current position to send to its ride
        // either. Checking the hire first would push an hour-old fix to the passenger waiting for it.
        var verdict = Classify(
            OperatingModes.C, Now - TimeSpan.FromMinutes(10), new VehicleState(Guid.NewGuid(), null));

        Assert.Equal(VehicleAudience.None, verdict.Audience);
        Assert.Equal(VehicleRemovalReasons.Stale, verdict.RemovalReason);
    }

    [Fact]
    public void An_unclassifiable_frame_reaches_nobody_and_removes_nothing()
    {
        // A frame with no mode cannot be assigned a visibility rule, and publishing it would mean
        // publishing a vehicle whose rule is unknown. Nothing is *removed* either — it was never on
        // a group to be removed from.
        foreach (var mode in new[] { null, string.Empty, "D", "c" })
        {
            var verdict = Classify(mode, Now, VehicleState.Unknown);

            Assert.Equal(VehicleAudience.None, verdict.Audience);
            Assert.Null(verdict.RemovalReason);
        }
    }

    [Fact]
    public void A_frame_with_no_timestamp_is_treated_as_current()
    {
        // The wire contract marks `sampleTs` optional, and position-processor-svc stamps every entry
        // it writes — so refusing an unstamped frame would make the filter depend on a field that is
        // not guaranteed, and the symptom would be an empty map.
        var verdict = VehicleVisibilityRules.Classify(
            OperatingModes.A, sampleTs: null, VehicleState.Unknown, Now, Window);

        Assert.Equal(VehicleAudience.Public, verdict.Audience);
    }

    private static VehicleVerdict Classify(string? mode, DateTimeOffset sampleTs, VehicleState state) =>
        VehicleVisibilityRules.Classify(mode, sampleTs, state, Now, Window);
}
