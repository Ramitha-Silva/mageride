using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// The D-18 / T-07 anti-spoof filter, gate by gate (D5' §13.1, ADD §12.6).
/// </summary>
/// <remarks>
/// <para>
/// No containers: the filter is pure, and every input — the sample, the vehicle's last accepted
/// state, whether this is a backlog — is an argument. That is deliberate. A gate whose false
/// positives take a vehicle off the map has to have its boundaries asserted <i>at</i> the boundary,
/// and a test that inferred the verdict from what came out of Redis afterwards could not tell a
/// rejection from a write that failed.
/// </para>
/// <para>
/// <see cref="PositionGateTests"/> is the other half: the same gates through the real processor,
/// against a real Redis, proving a refused sample reaches neither the live map nor the watermark.
/// </para>
/// </remarks>
[Trait("Category", "PositionProcessor")]
public sealed class PlausibilityTests
{
    /// <summary>The instant the fixture's "last accepted" sample was captured at.</summary>
    /// <remarks>
    /// Relative to now, not a fixed date. The forward-skew gate compares against the platform's
    /// clock, so a literal instant would make these tests pass or fail depending on what time of day
    /// the suite ran — the worst kind of flake, because it would look like a real regression.
    /// </remarks>
    private static readonly DateTimeOffset Captured = DateTimeOffset.UtcNow.AddMinutes(-10);

    /// <summary>~1.1 km north of Colombo Fort.</summary>
    private static readonly GeoPoint OneKilometreNorth = new(6.9444, 79.8428);

    [Fact]
    public void A_first_sample_has_no_step_to_measure_and_is_accepted()
    {
        // Nothing to compare against is not the same as nothing suspicious. A vehicle's first sample
        // after a restart, or after `veh:meta` expired, gets the per-sample gates and no more.
        Assert.True(Judge(At(Samples.ColomboFort), previous: null).IsPlausible);
    }

    // --- Accuracy (D-18) -------------------------------------------------------------------------

    [Theory]
    [InlineData(6.0, true)]
    [InlineData(199.9, true)]
    [InlineData(200.0, true)]
    [InlineData(200.1, false)]
    [InlineData(1_500.0, false)]
    public void The_accuracy_ceiling_is_two_hundred_metres_and_it_discards(double accuracyM, bool accepted)
    {
        var verdict = Judge(At(Samples.ColomboFort) with { AccuracyM = accuracyM }, previous: null);

        Assert.Equal(accepted, verdict.IsPlausible);

        if (!accepted)
        {
            // "Discarded, not smoothed" is this component's second fence. The verdict carries no
            // corrected position because there is none to carry.
            Assert.Equal(PlausibilityCheck.Accuracy, verdict.Check);
        }
    }

    [Fact]
    public void A_sample_that_reports_no_accuracy_at_all_is_not_refused_for_it()
    {
        // Most hardware frames carry no accuracy circle. Refusing them would blind the tracker fleet
        // rather than catch a spoofer — the ceiling is a gate on a *claim*, and there is no claim.
        Assert.True(Judge(At(Samples.ColomboFort) with { AccuracyM = null }, previous: null).IsPlausible);
    }

    // --- Per-vehicle-type speed (D-18, ADD §12.6) ------------------------------------------------

    [Theory]
    [InlineData("three_wheeler", 80)]
    [InlineData("motorbike", 180)]
    [InlineData("sedan", 180)]
    [InlineData("van", 130)]
    [InlineData("mini_van", 140)]
    [InlineData("flex", 200)]
    [InlineData("bus", 120)]
    public void Each_tier_carries_the_ADD_12_6_ceiling(string vehicleType, double ceilingKph)
    {
        // The table is asserted against the spec's own numbers rather than against whatever the
        // defaults happen to hold: this is the D-18 anti-spoof table, and a typo in it is a gate
        // that quietly stops gating.
        Assert.Equal(ceilingKph, ProcessorParts.Defaults().MaxSpeedKph[vehicleType]);

        var previous = Last(Samples.ColomboFort, Captured);

        // One kilometre in the time the ceiling allows, minus a hair, and plus a hair.
        var justUnder = SecondsFor(ceilingKph * 0.95);
        var justOver = SecondsFor(ceilingKph * 1.05);

        Assert.True(Judge(Step(vehicleType, Captured.AddSeconds(justUnder)), previous).IsPlausible);

        var refused = Judge(Step(vehicleType, Captured.AddSeconds(justOver)), previous);

        Assert.False(refused.IsPlausible);
        Assert.Equal(PlausibilityCheck.Speed, refused.Check);
    }

    /// <summary>The DoD's first line: "a teleporting sample is rejected".</summary>
    [Fact]
    public void A_three_wheeler_that_crosses_the_island_between_two_samples_is_refused()
    {
        // Colombo to Kandy is ~95 km. Ten seconds apart, which is 34,000 km/h — past every per-type
        // ceiling and past the absolute backstop.
        var verdict = Judge(
            At(Samples.Kandy, Captured.AddSeconds(10)),
            Last(Samples.ColomboFort, Captured));

        Assert.False(verdict.IsPlausible);
        Assert.Equal(PlausibilityCheck.Jump, verdict.Check);
    }

    [Fact]
    public void A_tier_the_ADD_table_omits_falls_to_the_default_rather_than_to_nothing()
    {
        var options = ProcessorParts.Defaults();

        // registry allows ten vehicle types; ADD §12.6 prices seven. See the C039 handoff.
        Assert.False(options.MaxSpeedKph.ContainsKey("truck"));

        var previous = Last(Samples.ColomboFort, Captured);

        // 1 km in 12 s is 300 km/h — over the 200 km/h default, under the jump backstop.
        var verdict = Judge(Step("truck", Captured.AddSeconds(12)), previous, options);

        Assert.False(verdict.IsPlausible);
        Assert.Equal(PlausibilityCheck.Speed, verdict.Check);

        // …and 1 km in 30 s is 120 km/h, which a truck may well be doing.
        Assert.True(Judge(Step("truck", Captured.AddSeconds(30)), previous, options).IsPlausible);
    }

    [Fact]
    public void The_devices_own_reported_speed_is_checked_before_any_arithmetic()
    {
        // A tracker claiming 300 km/h for a three-wheeler is wrong about something it needs no
        // second sample to be wrong about.
        var verdict = Judge(
            At(Samples.ColomboFort) with { SpeedMps = 300 * 1000d / 3600d },
            previous: null);

        Assert.False(verdict.IsPlausible);
        Assert.Equal(PlausibilityCheck.Speed, verdict.Check);
    }

    [Fact]
    public void Two_samples_bearing_one_instant_are_judged_at_the_minimum_step_rather_than_skipped()
    {
        var previous = Last(Samples.ColomboFort, Captured);

        // Most trackers stamp to the whole second, so a burst arrives with no gap at all. Skipping
        // the check there would let a spoofer publish a teleport as two same-instant samples.
        var verdict = Judge(At(OneKilometreNorth, Captured), previous);

        Assert.False(verdict.IsPlausible);

        // 1.1 km judged over the 1 s floor is 4,000 km/h — past the backstop, not merely past the
        // three-wheeler's ceiling.
        Assert.Equal(PlausibilityCheck.Jump, verdict.Check);

        // And a step small enough to be jitter still passes at the same floor: ~11 m over 1 s.
        var jitter = new GeoPoint(Samples.ColomboFort.Latitude, Samples.ColomboFort.Longitude + 0.0001);
        Assert.True(Judge(At(jitter, Captured), previous).IsPlausible);
    }

    // --- T-07: hardware only ---------------------------------------------------------------------

    [Fact]
    public void A_hardware_GNSS_clock_that_does_not_advance_is_refused()
    {
        var previous = Last(Samples.ColomboFort, Captured);

        // T-07's monotonic GNSS UTC. The position barely moved, so nothing else would catch it —
        // which is the point: a replayed frame carries a real position and a stale clock.
        foreach (var instant in new[] { Captured, Captured.AddSeconds(-30) })
        {
            var verdict = Judge(Hardware(Samples.ColomboFort, instant), previous);

            Assert.False(verdict.IsPlausible);
            Assert.Equal(PlausibilityCheck.Clock, verdict.Check);
        }

        Assert.True(Judge(Hardware(Samples.ColomboFort, Captured.AddSeconds(5)), previous).IsPlausible);
    }

    [Fact]
    public void A_mobile_clock_that_goes_backwards_is_not_refused_for_it()
    {
        // D5' §13.1 gives the monotonic-clock rule to hardware only, and it is right to: a handset's
        // clock is the user's to set and Android will move it mid-track. `seq` is what orders a
        // handset's samples (R-17), and the watermark is where that is enforced.
        Assert.True(Judge(At(Samples.ColomboFort, Captured.AddSeconds(-30)), Last(Samples.ColomboFort, Captured))
            .IsPlausible);
    }

    [Fact]
    public void A_sample_dated_far_in_the_future_is_refused_before_it_becomes_the_watermark()
    {
        // Not in any spec — see the option. Without it, one frame dated 2099 becomes the monotonic
        // watermark and takes the tracker off the map until `veh:meta` expires.
        var verdict = Judge(
            Hardware(Samples.ColomboFort, DateTimeOffset.UtcNow.AddHours(2)),
            previous: null);

        Assert.False(verdict.IsPlausible);
        Assert.Equal(PlausibilityCheck.Clock, verdict.Check);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(11, true)]
    public void A_hardware_fix_needs_the_minimum_satellite_count(int satellites, bool accepted)
    {
        var verdict = Judge(
            Hardware(Samples.ColomboFort, Captured) with { SatCount = satellites },
            previous: null);

        Assert.Equal(accepted, verdict.IsPlausible);

        if (!accepted)
        {
            Assert.Equal(PlausibilityCheck.Satellites, verdict.Check);
        }
    }

    [Fact]
    public void A_hardware_fix_reporting_no_satellite_count_is_accepted_by_default_and_refusable_by_config()
    {
        var sample = Hardware(Samples.ColomboFort, Captured) with { SatCount = null };

        // A GT06 location frame carries no satellite count at all, and it is the largest tracker
        // family in `mqtt-topics.md` §7. Requiring one by default would blind the fleet.
        Assert.True(Judge(sample, previous: null).IsPlausible);

        var strict = ProcessorParts.Defaults();
        strict.RequireSatelliteCount = true;

        Assert.Equal(PlausibilityCheck.Satellites, Judge(sample, previous: null, strict).Check);
    }

    [Fact]
    public void A_mobile_sample_is_never_judged_on_satellites()
    {
        // A handset reports a fused Wi-Fi/cell/GNSS position; `satCount` describes one of three
        // inputs and is routinely zero on a perfectly good fix.
        var strict = ProcessorParts.Defaults();
        strict.RequireSatelliteCount = true;

        Assert.True(Judge(At(Samples.ColomboFort) with { SatCount = 0 }, previous: null, strict).IsPlausible);
        Assert.True(Judge(At(Samples.ColomboFort) with { SatCount = null }, previous: null, strict).IsPlausible);
    }

    // --- The backlog exemption -------------------------------------------------------------------

    [Fact]
    public void A_replayed_sample_is_not_judged_against_where_the_vehicle_is_now()
    {
        var previous = Last(Samples.Kandy, Captured);

        // R-17: a tracker that was offline in Colombo reconnects in Kandy and bursts its backlog.
        // Every one of those samples is 95 km and an hour away from the live position, and judging
        // them as teleports would drop the vehicle's whole history. `seq` is what filters a replay.
        var backlog = At(Samples.ColomboFort, Captured.AddHours(-1));

        Assert.False(Judge(backlog, previous).IsPlausible);
        Assert.True(Judge(backlog, previous, isReplay: true).IsPlausible);
    }

    [Fact]
    public void A_replayed_sample_is_still_judged_on_its_own_accuracy()
    {
        // A fix with a 500 m error circle was useless when it was captured; waiting an hour did not
        // improve it. The per-sample gates hold for a backlog, only the step gates do not.
        var verdict = Judge(
            At(Samples.ColomboFort, Captured.AddHours(-1)) with { AccuracyM = 500 },
            Last(Samples.Kandy, Captured),
            isReplay: true);

        Assert.False(verdict.IsPlausible);
        Assert.Equal(PlausibilityCheck.Accuracy, verdict.Check);
    }

    // ---------------------------------------------------------------------------------------------

    private static PlausibilityVerdict Judge(
        PositionSample sample,
        LastAcceptedPosition? previous,
        PositionProcessorOptions? options = null,
        bool isReplay = false) =>
        ProcessorParts.Filter(options).Judge(sample, previous, isReplay);

    private static PlausibilityVerdict Judge(
        PositionSample sample, LastAcceptedPosition? previous, bool isReplay) =>
        Judge(sample, previous, options: null, isReplay);

    private static PositionSample At(GeoPoint point, DateTimeOffset? at = null) =>
        Samples.At(Guid.NewGuid(), point, seq: 2, sampleTs: at ?? Captured);

    private static PositionSample Hardware(GeoPoint point, DateTimeOffset at) =>
        At(point, at) with { Source = PositionSource.Gt06, SatCount = 9 };

    /// <summary>A sample of <paramref name="vehicleType"/> a kilometre north of Colombo Fort.</summary>
    private static PositionSample Step(string vehicleType, DateTimeOffset at) =>
        Samples.At(Guid.NewGuid(), OneKilometreNorth, seq: 2, vehicleType: vehicleType, sampleTs: at)
            // The reported speedometer reading is cleared: this asserts the *implied* speed gate,
            // and leaving 8.5 m/s on it would mean the sample carried two different claims.
            with
        { SpeedMps = null };

    private static LastAcceptedPosition Last(GeoPoint point, DateTimeOffset at) =>
        new(point, at, Seq: 1, Cell: GeoCells.ViewCell(point), Pool: null);

    /// <summary>How long the ~1.1 km step takes at <paramref name="kph"/>.</summary>
    private static double SecondsFor(double kph) =>
        GeoMath.DistanceM(Samples.ColomboFort, OneKilometreNorth) / (kph * 1000d / 3600d);
}
