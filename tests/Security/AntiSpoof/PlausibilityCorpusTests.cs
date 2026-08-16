using System.Globalization;
using System.Text;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.Security.Tests.AntiSpoof.Corpus;

namespace MageRide.Security.Tests.AntiSpoof;

/// <summary>
/// C128's first definition-of-done item: <b>the adversarial corpus is rejected with a measured
/// false-positive rate below the agreed bound</b> (D-18, T-07; D5' §13.1, ADD §12.6).
///
/// <para>
/// The corpus is <c>Corpus/position-corpus.json</c> and the bound is in it, beside the data it
/// judges. Every track runs through <c>PlausibilityFilter</c> — position-processor-svc's own class
/// — configured from <c>infra/env/.env.app.example</c>, so the numbers are about the gate the
/// replica runs rather than about a transcription of its rules.
/// </para>
///
/// <para>
/// <b>Two rates, and they are not symmetric.</b> A false positive takes a vehicle off the live map
/// and, for a Mode C driver, out of the candidate pool — so it is measured as a rate and bounded at
/// a small one. A false negative is not a rate at all: a corpus is a list of attacks somebody wrote
/// down, and one this suite knows about and does not catch is a finding with an owner. The escape
/// bound is therefore zero, and the attacks the gate genuinely cannot see are held in
/// <c>knownGap</c> — asserted to still escape, so closing one fails here and asks for the entry to
/// be deleted.
/// </para>
/// </summary>
[Trait("Category", "AntiSpoof")]
public sealed class PlausibilityCorpusTests
{
    /// <summary>
    /// The denominator, asserted before anything is asserted about it.
    /// </summary>
    /// <remarks>
    /// A corpus that quietly stopped loading half its tracks would report a false-positive rate of
    /// zero and an escape rate of zero, which is exactly what a clean run looks like. The same
    /// failure C118's `ServiceCatalog` denominator exists to prevent.
    /// </remarks>
    [Fact]
    public void The_corpus_exercises_every_gate_and_every_vehicle_type_the_spec_prices()
    {
        var tracks = PositionCorpus.Current.Tracks;

        Assert.True(tracks.Count >= 30, $"The corpus holds {tracks.Count} tracks; C128 landed with 33.");

        var honest = tracks.Count(track => track.Label is TrackLabel.Honest);
        var hostile = tracks.Count(track => track.Label is TrackLabel.Hostile);

        Assert.True(honest >= 12, $"Only {honest} honest tracks: a false-positive rate needs a denominator.");
        Assert.True(hostile >= 15, $"Only {hostile} hostile tracks.");

        // Every gate the filter can close is driven by something. A check with no track is a check
        // whose threshold nobody is measuring.
        var driven = tracks
            .SelectMany(track => track.Legs)
            .Where(leg => leg.Hostile)
            .Select(leg => leg.Expect)
            .Where(expect => expect is not null and not nameof(PlausibilityCheck.None))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var closable = Enum.GetValues<PlausibilityCheck>()
            .Where(check => check is not PlausibilityCheck.None)
            .Select(check => check.ToString());

        Assert.Empty(closable.Except(driven, StringComparer.Ordinal));

        // Every tier ADD §12.6 prices carries at least one honest track, because a ceiling nobody
        // drives honestly against is a ceiling whose false-positive rate is unmeasured — and the
        // low tiers are exactly where a per-type table can be wrong.
        var exercised = tracks
            .Where(track => track.Label is TrackLabel.Honest)
            .Select(track => track.VehicleType)
            .ToHashSet(StringComparer.Ordinal);

        var unmeasured = PositionCorpus.Deployed.MaxSpeedKph.Keys
            .Where(type => !exercised.Contains(type))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unmeasured.Count == 0,
            "These tiers have a configured ceiling and no honest track measuring it: "
            + string.Join(", ", unmeasured));
    }

    [Theory]
    [MemberData(nameof(Honest))]
    public void An_honest_track_is_carried_by_the_gate_intact(string id)
    {
        var track = PositionCorpus.Track(id);
        var outcome = PositionCorpus.Run(track);

        Assert.Equal(0, outcome.Escapes);

        // Per-track as well as in aggregate: one badly-behaved tier hiding inside a corpus-wide
        // average is the failure mode a per-vehicle-type table exists to make visible.
        var rate = outcome.FalsePositives / (double)outcome.HonestSamples;

        Assert.True(
            rate <= PerTrackRefusalBound,
            $"{id} ({track.VehicleType}, {track.Family}): the gate refused {outcome.FalsePositives} of "
            + $"{outcome.HonestSamples} honest samples ({rate:P2}), over the {PerTrackRefusalBound:P0} "
            + $"per-track bound.{Environment.NewLine}{track.Note}{Environment.NewLine}"
            + outcome.Explain(sample => sample.IsFalsePositive));
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_hostile_track_is_refused_by_the_gate_it_names(string id)
    {
        var track = PositionCorpus.Track(id);

        if (track.KnownGap is not null)
        {
            Assert.Skip($"{id} is a documented gap; The_documented_gaps_are_still_exactly_the_documented_gaps owns it.");
        }

        var outcome = PositionCorpus.Run(track);

        Assert.True(
            outcome.Escapes == 0,
            $"{id} ({track.Family}/{track.VehicleType}): {outcome.Escapes} of {outcome.HostileSamples} "
            + $"hostile samples reached the live map.{Environment.NewLine}{track.Note}{Environment.NewLine}"
            + outcome.Explain(sample => sample.IsEscape));

        // Caught, and caught by the gate the corpus predicted. A teleport refused for having too
        // few satellites is a pass that would survive the teleport gate being deleted — and the
        // `check` tag is what an operator retuning ADD §12.6's table reads.
        var miscategorised = outcome.Samples.Where(sample => sample.IsMiscategorised).ToList();

        Assert.True(
            miscategorised.Count == 0,
            $"{id}: {miscategorised.Count} hostile samples were refused by a gate other than the one "
            + $"the corpus names ({miscategorised.FirstOrDefault()?.Candidate.Expect}).{Environment.NewLine}"
            + outcome.Explain(sample => sample.IsMiscategorised));

        // An honest run-up refused inside a hostile track is still a false positive, and one that
        // the aggregate would otherwise never see: the hostile tracks carry roughly a third of the
        // corpus's honest samples.
        Assert.True(
            outcome.FalsePositives == 0,
            $"{id}: the run-up to the attack lost {outcome.FalsePositives} honest samples."
            + Environment.NewLine + outcome.Explain(sample => sample.IsFalsePositive));
    }

    /// <summary>The DoD's headline number.</summary>
    [Fact]
    public void The_measured_false_positive_rate_is_below_the_agreed_bound()
    {
        var measured = Measure();
        var bound = PositionCorpus.Current.Bounds.MaxHonestRefusalRate;

        Assert.True(
            measured.HonestRefusalRate <= bound,
            $"The gate refused {measured.FalsePositives} of {measured.HonestSamples} honest samples "
            + $"({measured.HonestRefusalRate:P3}), over the agreed {bound:P2}."
            + Environment.NewLine + measured.PerTypeReport());
    }

    [Fact]
    public void The_measured_escape_rate_is_below_the_agreed_bound()
    {
        var measured = Measure();
        var bound = PositionCorpus.Current.Bounds.MaxHostileEscapeRate;

        Assert.True(
            measured.HostileEscapeRate <= bound,
            $"{measured.Escapes} of {measured.HostileSamples} hostile samples reached the live map "
            + $"({measured.HostileEscapeRate:P3}), over the agreed {bound:P2}. Every escape the "
            + "platform cannot close belongs in the track's `knownGap`, naming the control that "
            + "does close it — 'noted' is not a resolution (C127 fence)."
            + Environment.NewLine + measured.PerFamilyReport());
    }

    /// <summary>
    /// The gaps are a ratchet in both directions, which is what stops one outliving the defect.
    /// </summary>
    /// <remarks>
    /// `LiveDrift`'s idiom (C118) and `AnonymousSurface`'s (C127): a ledgered escape has to STILL
    /// escape. The day a gap is closed — by a new gate, by a threshold change, or by somebody
    /// noticing the corpus was wrong about it — this fails and asks for the entry to be deleted.
    /// </remarks>
    [Fact]
    public void The_documented_gaps_are_still_exactly_the_documented_gaps()
    {
        var gaps = PositionCorpus.Current.Tracks.Where(track => track.KnownGap is not null).ToList();

        Assert.NotEmpty(gaps);

        foreach (var track in gaps)
        {
            var outcome = PositionCorpus.Run(track);

            Assert.True(
                outcome.Escapes > 0,
                $"{track.Id} is recorded as a gap the D-18/T-07 gate cannot close, and the gate now "
                + $"refuses all {outcome.HostileSamples} of its hostile samples. If that is the "
                + "platform improving, delete the `knownGap` and give the leg the check that caught "
                + $"it — the ledger may only shrink.{Environment.NewLine}  recorded: {track.KnownGap}");

            Assert.True(
                outcome.FalsePositives == 0,
                $"{track.Id}: the run-up to a documented gap lost {outcome.FalsePositives} honest samples.");
        }
    }

    /// <summary>
    /// A measurement that moved between two runs could not be compared against a bound at all.
    /// </summary>
    [Fact]
    public void The_measurement_is_reproducible()
    {
        var first = Measure();
        var second = Measure();

        Assert.Equal(first.FalsePositives, second.FalsePositives);
        Assert.Equal(first.Escapes, second.Escapes);
        Assert.Equal(first.HonestSamples, second.HonestSamples);
        Assert.Equal(first.HostileSamples, second.HostileSamples);
    }

    /// <summary>
    /// Writes the per-vehicle-type and per-family tables C128's deliverable asks for.
    /// </summary>
    /// <remarks>
    /// The same idiom as C127's <c>InventoryDump</c>: the evidence in
    /// <c>security/anti-spoof-tuning.md</c> is transcribed from a run rather than typed from
    /// memory, and regenerating it is one command. Set <c>MAGERIDE_ANTISPOOF_DUMP=1</c>.
    /// </remarks>
    [Fact]
    public void The_measurement_can_be_dumped_for_the_tuning_report()
    {
        if (Environment.GetEnvironmentVariable("MAGERIDE_ANTISPOOF_DUMP") != "1")
        {
            Assert.Skip("Set MAGERIDE_ANTISPOOF_DUMP=1 to rewrite the tuning report's evidence tables.");
        }

        var measured = Measure();
        var path = Path.Combine(DeployedConfiguration.RepositoryRoot, "security", "anti-spoof-corpus-run.md");

        File.WriteAllText(path, measured.Report());

        Assert.True(File.Exists(path));
    }

    public static TheoryData<string> Honest() => Ids(TrackLabel.Honest);

    public static TheoryData<string> Hostile() => Ids(TrackLabel.Hostile);

    /// <summary>
    /// The per-track bound, deliberately looser than the corpus-wide one.
    /// </summary>
    /// <remarks>
    /// The corpus-wide bound is what the DoD names and is dominated by the easy tracks; this is
    /// what stops the hard ones hiding inside it. Five per cent rather than zero because the
    /// hardest honest track in the corpus — a GT06 three-wheeler at a 1 s cadence in the Pettah
    /// canyon, against the lowest ceiling ADD §12.6 sets — is genuinely near the boundary, and a
    /// bound of zero there would be a bound met by softening the track.
    /// </remarks>
    private const double PerTrackRefusalBound = 0.05;

    private static TheoryData<string> Ids(TrackLabel label)
    {
        var data = new TheoryData<string>();

        foreach (var track in PositionCorpus.Current.Tracks.Where(track => track.Label == label))
        {
            data.Add(track.Id);
        }

        return data;
    }

    private static Measurement Measure() =>
        new([.. PositionCorpus.Current.Tracks.Select(track => PositionCorpus.Run(track))]);

    private sealed class Measurement(IReadOnlyList<TrackOutcome> outcomes)
    {
        public int HonestSamples { get; } = outcomes.Sum(outcome => outcome.HonestSamples);

        public int HostileSamples { get; } = outcomes
            .Where(outcome => outcome.Track.KnownGap is null)
            .Sum(outcome => outcome.HostileSamples);

        public int FalsePositives { get; } = outcomes.Sum(outcome => outcome.FalsePositives);

        public int Escapes { get; } = outcomes
            .Where(outcome => outcome.Track.KnownGap is null)
            .Sum(outcome => outcome.Escapes);

        public double HonestRefusalRate => HonestSamples == 0 ? 0 : FalsePositives / (double)HonestSamples;

        public double HostileEscapeRate => HostileSamples == 0 ? 0 : Escapes / (double)HostileSamples;

        public string PerTypeReport() => Table(
            "vehicle type",
            outcomes.GroupBy(outcome => outcome.Track.VehicleType, StringComparer.Ordinal));

        public string PerFamilyReport() => Table(
            "family",
            outcomes.GroupBy(outcome => outcome.Track.Family, StringComparer.Ordinal));

        public string Report()
        {
            var report = new StringBuilder();

            report.AppendLine("<!-- GENERATED by tests/Security AntiSpoof: MAGERIDE_ANTISPOOF_DUMP=1 -->");
            report.AppendLine(CultureInfo.InvariantCulture, $"# Corpus run — {outcomes.Count} tracks");
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"Honest samples {HonestSamples}, refused {FalsePositives} ({HonestRefusalRate:P3}).");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"Hostile samples {HostileSamples} (excluding documented gaps), escaped {Escapes} "
                + $"({HostileEscapeRate:P3}).");
            report.AppendLine();
            report.AppendLine("## By vehicle type");
            report.AppendLine();
            report.AppendLine(PerTypeReport());
            report.AppendLine("## By family");
            report.AppendLine();
            report.AppendLine(PerFamilyReport());
            report.AppendLine("## Documented gaps");
            report.AppendLine();

            foreach (var outcome in outcomes.Where(entry => entry.Track.KnownGap is not null))
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"- **{outcome.Track.Id}** — {outcome.Escapes}/{outcome.HostileSamples} escaped. {outcome.Track.KnownGap}");
            }

            return report.ToString();
        }

        private static string Table(string heading, IEnumerable<IGrouping<string, TrackOutcome>> groups)
        {
            var rows = new StringBuilder();

            rows.AppendLine(CultureInfo.InvariantCulture,
                $"| {heading} | honest | refused | rate | hostile | escaped |");
            rows.AppendLine("|---|---:|---:|---:|---:|---:|");

            foreach (var group in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var honest = group.Sum(outcome => outcome.HonestSamples);
                var refused = group.Sum(outcome => outcome.FalsePositives);
                var hostile = group.Sum(outcome => outcome.HostileSamples);
                var escaped = group.Sum(outcome => outcome.Escapes);
                var rate = honest == 0 ? 0 : refused / (double)honest;

                rows.AppendLine(CultureInfo.InvariantCulture,
                    $"| {group.Key} | {honest} | {refused} | {rate:P2} | {hostile} | {escaped} |");
            }

            return rows.ToString();
        }
    }
}
