using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Security.Tests.AntiSpoof.Corpus;

namespace MageRide.Security.Tests.AntiSpoof;

/// <summary>
/// C128's first fence: <b>thresholds are per vehicle type and configurable — tuning means changing
/// config plus tests, not hardcoding</b>.
///
/// <para>
/// A fence like that cannot be asserted by grepping for constants. What it means operationally is
/// three things, and each is asserted here: the deployed environment file carries every threshold
/// (so an operator has something to change); the values in it are ADD §12.6's, verbatim (so the
/// corpus is measuring the spec rather than a drift); and changing one actually changes the gate's
/// verdicts (so nothing compares against a literal at the comparison site).
/// </para>
///
/// <para>
/// The third is the one that matters most, and it is also what stops
/// <see cref="PlausibilityCorpusTests"/> from being vacuous: a corpus that reports zero false
/// positives proves nothing unless a mistuned threshold makes it report some.
/// </para>
/// </summary>
[Trait("Category", "AntiSpoof")]
public sealed class ThresholdConfigurationTests
{
    /// <summary>ADD §12.6's anti-spoof table, transcribed once, here.</summary>
    /// <remarks>
    /// The only literal copy of the table in this suite. Everything else reads configuration, so
    /// this is the single place a spec change has to be reflected — and the place a reviewer
    /// diffs against the ADD.
    /// </remarks>
    private static readonly Dictionary<string, double> SpecCeilingsKph = new(StringComparer.Ordinal)
    {
        ["bus"] = 120,
        ["sedan"] = 180,
        ["mini_van"] = 140,
        ["van"] = 130,
        ["flex"] = 200,
        ["three_wheeler"] = 80,
        ["motorbike"] = 180,
    };

    [Fact]
    public void Every_threshold_the_gate_uses_is_in_the_deployed_environment_file()
    {
        var section = DeployedConfiguration.Current.GetSection(PositionProcessorOptions.SectionName);

        string[] required =
        [
            "PlausibilityEnabled",
            "DefaultMaxSpeedKph",
            "MaxJumpSpeedKph",
            "MaxAccuracyM",
            "MinStepInterval",
            "MinSatellites",
            "RequireSatelliteCount",
            "MaxClockSkewAhead",
            "VehicleMetaTtl",
        ];

        var missing = required.Where(key => section[key] is null).ToList();

        Assert.True(
            missing.Count == 0,
            "These anti-spoof thresholds have no line in infra/env/.env.app.example, so retuning "
            + "them after a month of false positives is a build rather than a setting (C128 fence 1): "
            + string.Join(", ", missing));

        // Per-type ceilings bind as `MaxSpeedKph:{type}`. A tier with no line falls silently to the
        // default, which for the low tiers is a 2.5x widening nobody would see.
        var configured = section.GetSection("MaxSpeedKph").GetChildren().Select(child => child.Key).ToList();

        Assert.Equal(
            SpecCeilingsKph.Keys.Order(StringComparer.Ordinal),
            configured.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_deployed_ceilings_are_the_spec_table_verbatim()
    {
        var deployed = PositionCorpus.Deployed;

        Assert.Equal(SpecCeilingsKph.Count, deployed.MaxSpeedKph.Count);

        foreach (var (type, kph) in SpecCeilingsKph)
        {
            Assert.True(
                deployed.MaxSpeedKph.TryGetValue(type, out var actual),
                $"ADD §12.6 prices '{type}' and the deployment does not.");

            Assert.Equal(kph, actual);
        }

        // ADD §12.6's "jump < 1 km/s" and D-18's 200 m accuracy circle, in the units the options use.
        Assert.Equal(3_600d, deployed.MaxJumpSpeedKph);
        Assert.Equal(200d, deployed.MaxAccuracyM);

        // The most permissive value IN the table, deliberately: a tier no spec prices should never
        // be refused by a number nobody wrote, and the jump backstop is what still catches it.
        Assert.Equal(SpecCeilingsKph.Values.Max(), deployed.DefaultMaxSpeedKph);
    }

    /// <summary>
    /// The environment file and the class initialisers say the same thing.
    /// </summary>
    /// <remarks>
    /// Both are checked in, both are read as "the platform's thresholds", and only one of them is
    /// what a container runs. A divergence is not necessarily wrong — an operator may deliberately
    /// retune the deployment — but it must be a decision somebody made rather than one half of a
    /// pair being edited.
    /// </remarks>
    [Fact]
    public void The_environment_file_and_the_options_defaults_have_not_drifted_apart()
    {
        var deployed = PositionCorpus.Deployed;
        var defaults = new PositionProcessorOptions();

        Assert.Equal(defaults.MaxSpeedKph.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            deployed.MaxSpeedKph.OrderBy(entry => entry.Key, StringComparer.Ordinal));

        Assert.Equal(defaults.DefaultMaxSpeedKph, deployed.DefaultMaxSpeedKph);
        Assert.Equal(defaults.MaxJumpSpeedKph, deployed.MaxJumpSpeedKph);
        Assert.Equal(defaults.MaxAccuracyM, deployed.MaxAccuracyM);
        Assert.Equal(defaults.MinStepInterval, deployed.MinStepInterval);
        Assert.Equal(defaults.MinSatellites, deployed.MinSatellites);
        Assert.Equal(defaults.RequireSatelliteCount, deployed.RequireSatelliteCount);
        Assert.Equal(defaults.MaxClockSkewAhead, deployed.MaxClockSkewAhead);
        Assert.Equal(defaults.VehicleMetaTtl, deployed.VehicleMetaTtl);
    }

    [Fact]
    public void The_gate_is_switched_on_in_the_deployed_configuration()
    {
        // Off means every gate passes and a spoofed position reaches the live map, while from the
        // outside the pipeline looks exactly the same: positions flow, the map is populated,
        // nothing errors. C039 logs it loudly at start-up; this is the assertion that it is on.
        Assert.True(PositionCorpus.Deployed.PlausibilityEnabled);
        Assert.True(PositionCorpus.Deployed.RateCheckEnabled);
    }

    /// <summary>
    /// Retuning one tier changes that tier's verdicts and nothing else's.
    /// </summary>
    /// <remarks>
    /// This is the fence, executable. If any comparison site held a literal, tightening the
    /// three-wheeler ceiling in configuration would leave its honest track untouched — and a
    /// "configurable" threshold nothing reads is the failure mode that looks most like success.
    /// </remarks>
    [Theory]
    [InlineData("honest-three-wheeler-canyon-one-hertz", "three_wheeler", 25d)]
    [InlineData("honest-bus-expressway-express-service", "bus", 40d)]
    [InlineData("honest-sedan-e01-expressway-cruise", "sedan", 55d)]
    public void Tightening_one_tiers_ceiling_in_configuration_changes_that_tiers_verdicts(
        string trackId, string type, double tightenedKph)
    {
        var track = PositionCorpus.Track(trackId);

        Assert.Equal(0, PositionCorpus.Run(track).FalsePositives);

        var tightened = Clone(PositionCorpus.Deployed);
        tightened.MaxSpeedKph[type] = tightenedKph;

        var after = PositionCorpus.Run(track, tightened);

        Assert.True(
            after.FalsePositives > 0,
            $"Dropping the '{type}' ceiling to {tightenedKph} km/h in configuration changed nothing "
            + $"about how {trackId} is judged. Either the corpus is not driving that tier or the "
            + "comparison site is not reading the configured value (C128 fence 1).");

        // And only that tier: a per-type table that moved every type together would be one number
        // wearing seven names.
        var others = PositionCorpus.Current.Tracks
            .Where(other => other.Label is TrackLabel.Honest
                && !string.Equals(other.VehicleType, type, StringComparison.Ordinal))
            .Sum(other => PositionCorpus.Run(other, tightened).FalsePositives);

        Assert.Equal(0, others);
    }

    /// <summary>
    /// The other direction: loosening a gate lets its own attacks through.
    /// </summary>
    /// <remarks>
    /// Without this the corpus's zero-escape result would be consistent with a filter that refuses
    /// everything for some unrelated reason. Each case loosens exactly one knob and asserts the
    /// attack that knob answers stops being caught.
    /// </remarks>
    [Fact]
    public void Loosening_a_gate_lets_the_attack_it_answers_through()
    {
        // The accuracy discard, at 500 m instead of D-18's 200.
        var wideAccuracy = Clone(PositionCorpus.Deployed);
        wideAccuracy.MaxAccuracyM = 500;

        Assert.True(
            PositionCorpus.Run(PositionCorpus.Track("hostile-accuracy-circle-past-the-ceiling"), wideAccuracy)
                .UnrefusedHostileSamples > 0,
            "A 450 m error circle is still refused with the ceiling raised to 500 m, so the "
            + "accuracy gate is not the thing refusing it.");

        // T-07's satellite minimum, at zero.
        var noSatelliteFloor = Clone(PositionCorpus.Deployed);
        noSatelliteFloor.MinSatellites = 0;

        Assert.True(
            PositionCorpus.Run(PositionCorpus.Track("hostile-hardware-fix-with-no-satellites"), noSatelliteFloor)
                .UnrefusedHostileSamples > 0,
            "A zero-satellite fix is still refused with MinSatellites at 0.");

        // The forward-skew guard, at a day.
        var wideSkew = Clone(PositionCorpus.Deployed);
        wideSkew.MaxClockSkewAhead = TimeSpan.FromHours(24);

        Assert.True(
            PositionCorpus.Run(PositionCorpus.Track("hostile-gnss-clock-an-hour-in-the-future"), wideSkew)
                .UnrefusedHostileSamples > 0,
            "An hour-ahead GNSS instant is still refused with MaxClockSkewAhead at 24 h.");

        // And the whole gate. `PlausibilityEnabled = false` is a switch position an operator can
        // reach, so what it costs is worth stating as a number rather than as a warning.
        var off = Clone(PositionCorpus.Deployed);
        off.MaxSpeedKph.Clear();
        off.DefaultMaxSpeedKph = 5_000;
        off.MaxJumpSpeedKph = 100_000;

        var escaped = PositionCorpus.Current.Tracks
            .Where(track => track.Label is TrackLabel.Hostile)
            .Sum(track => PositionCorpus.Run(track, off).UnrefusedHostileSamples);

        Assert.True(escaped > 60, $"Only {escaped} hostile samples survive a gate with no speed ceilings at all.");
    }

    /// <summary>
    /// <c>MinStepInterval</c> is a clamp, not a skip — asserted through the attack it exists for.
    /// </summary>
    /// <remarks>
    /// C039's rule, and the one most likely to be "simplified" by somebody who reads it as a
    /// division-by-zero guard: two fixes bearing one timestamp have no interval to divide by, and
    /// SKIPPING them would hand a spoofer the entire step gate for the price of stamping two
    /// samples with the same second — which every tracker family in D6' §4.1 does anyway.
    /// </remarks>
    [Fact]
    public void The_minimum_step_interval_is_a_clamp_and_a_wider_one_is_a_looser_gate()
    {
        var track = PositionCorpus.Track("hostile-same-instant-burst-hiding-a-teleport");

        Assert.Equal(0, PositionCorpus.Run(track).Escapes);

        // 5 km judged over 60 s instead of 1 s is 300 km/h — still over the sedan ceiling, which is
        // why the assertion is about the *jump* tag rather than about escaping altogether. A wider
        // clamp does not open the gate; it demotes a teleport to a speeding vehicle, and the tag is
        // what an operator retuning ADD §12.6's table reads.
        var wide = Clone(PositionCorpus.Deployed);
        wide.MinStepInterval = TimeSpan.FromSeconds(60);

        var after = PositionCorpus.Run(track, wide);

        Assert.All(
            after.Samples.Where(sample => sample.Candidate.Hostile),
            sample => Assert.NotEqual(
                MageRide.HotPath.PositionProcessor.Plausibility.PlausibilityCheck.Jump, sample.Verdict.Check));
    }

    private static PositionProcessorOptions Clone(PositionProcessorOptions source) => new()
    {
        PlausibilityEnabled = source.PlausibilityEnabled,
        MaxSpeedKph = new Dictionary<string, double>(source.MaxSpeedKph, StringComparer.Ordinal),
        DefaultMaxSpeedKph = source.DefaultMaxSpeedKph,
        MaxJumpSpeedKph = source.MaxJumpSpeedKph,
        MaxAccuracyM = source.MaxAccuracyM,
        MinStepInterval = source.MinStepInterval,
        MinSatellites = source.MinSatellites,
        RequireSatelliteCount = source.RequireSatelliteCount,
        MaxClockSkewAhead = source.MaxClockSkewAhead,
        VehicleMetaTtl = source.VehicleMetaTtl,
    };
}
