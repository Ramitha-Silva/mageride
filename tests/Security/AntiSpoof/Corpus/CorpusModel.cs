using System.Text.Json.Serialization;

namespace MageRide.Security.Tests.AntiSpoof.Corpus;

/// <summary>Whether a track is something the platform must accept or something it must refuse.</summary>
public enum TrackLabel
{
    /// <summary>A vehicle behaving as vehicles behave. Every sample must be accepted.</summary>
    Honest,

    /// <summary>An attack. The samples marked hostile must be refused, by the named check.</summary>
    Hostile,
}

/// <summary>
/// One labelled track in the C128 adversarial corpus.
/// </summary>
/// <param name="Id">Stable, unique — it is the name a measurement failure is reported under.</param>
/// <param name="Label">Accept or refuse.</param>
/// <param name="Family">
/// The attack family, or the honest driving pattern. Reported per family so a threshold change is
/// visible as "urban-canyon regressed" rather than as one aggregate moving.
/// </param>
/// <param name="VehicleType">
/// The canonical snake-case type the sample carries. The ceiling is looked up by this, ordinally,
/// so a track typed <c>threeWheeler</c> would silently be measured against the 200 km/h default —
/// which is why <see cref="Loading.PositionCorpus"/> refuses a type ADD §12.6 does not price
/// unless the track says it means to use the default.
/// </param>
/// <param name="Source">
/// The <c>PositionSource</c> name. Hardware and mobile take different paths through the filter —
/// D5' §13.1's "hardware additionally" — so a corpus that only carried one would measure half the
/// gate.
/// </param>
/// <param name="Note">Why this track is in the corpus. Read by whoever retunes a threshold.</param>
/// <param name="Start">Starting <c>[lat, lng]</c>.</param>
/// <param name="Legs">The track, as segments of steady travel.</param>
/// <param name="KnownGap">
/// Set on a hostile track the gate is known <b>not</b> to catch. The measurement still runs and
/// still reports it; what changes is that the escape is asserted to be exactly the documented one
/// rather than counted as a regression. Every entry names the control that does catch it, or says
/// that nothing does — see <c>security/anti-spoof-tuning.md</c>.
/// </param>
public sealed record CorpusTrack(
    string Id,
    TrackLabel Label,
    string Family,
    string VehicleType,
    string Source,
    string Note,
    double[] Start,
    IReadOnlyList<CorpusLeg> Legs,
    string? KnownGap = null)
{
    /// <summary>Opts a track out of the vehicle-type check — for the untyped and unpriced tiers.</summary>
    [JsonPropertyName("usesDefaultCeiling")]
    public bool UsesDefaultCeiling { get; init; }

    /// <summary>When the track starts, ISO-8601. Relative to "now" when absent, which is the norm.</summary>
    /// <remarks>
    /// Almost every track is anchored to the run's clock rather than to a literal instant, because
    /// <c>MaxClockSkewAhead</c> compares against <c>DateTimeOffset.UtcNow</c>: a corpus pinned to a
    /// date in 2026 would start failing the forward-skew gate the moment it was old, and the
    /// failure would look like a threshold regression rather than like a stale fixture.
    /// </remarks>
    [JsonPropertyName("startsAtUtc")]
    public DateTimeOffset? StartsAtUtc { get; init; }

    public override string ToString() => Id;
}

/// <summary>
/// A segment of steady travel, expanded into <see cref="Count"/> samples.
/// </summary>
/// <remarks>
/// Every knob here exists because one attack family needs it. Nothing is a general-purpose
/// simulator setting: <see cref="JumpKm"/> is the teleport, <see cref="ClockOffsetSeconds"/> is the
/// skew, <see cref="ReplayOfLeg"/> is the replayed track, and so on. Keeping the mapping
/// one-to-one is what lets a reviewer read a track and know what it is testing.
/// </remarks>
/// <param name="Count">Samples this leg produces.</param>
/// <param name="CadenceSeconds">Gap between them, in seconds. D5' §5.2's table is the honest range.</param>
/// <param name="SpeedKph">Ground speed actually travelled. Distance is derived from it.</param>
/// <param name="BearingDeg">Course, degrees clockwise from true north.</param>
public sealed record CorpusLeg(
    int Count,
    double CadenceSeconds,
    double SpeedKph,
    double BearingDeg)
{
    /// <summary>Reported horizontal accuracy in metres, or null for a device that reports none.</summary>
    public double? AccuracyM { get; init; }

    /// <summary>Satellites in the fix, or null for a frame that carries no count (a GT06 location frame).</summary>
    public int? SatCount { get; init; }

    /// <summary>
    /// How far the fix may wander from the truth, in metres — the receiver's own error, not motion.
    /// </summary>
    /// <remarks>
    /// This and <see cref="JitterDriftM"/> are the load-bearing honest-side knobs. The step gate
    /// divides a distance by a time, and over a one-second cadence the distance is very largely
    /// receiver error rather than travel — so a corpus of clean fixes would measure a filter nobody
    /// deploys.
    /// </remarks>
    public double JitterM { get; init; }

    /// <summary>
    /// How far that error moves <b>between consecutive fixes</b>, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This, and not <see cref="JitterM"/>, is what the step gate sees</b>, and modelling it
    /// wrongly is the easiest way to produce a false-positive rate that does not exist in the field.
    /// GNSS error is strongly autocorrelated: multipath geometry changes over seconds, so a
    /// receiver reading 30 m off truth reads roughly 30 m off truth a second later, in roughly the
    /// same direction. Drawing each fix independently from a 30 m disc instead implies up to 60 m
    /// of displacement per second — 216 km/h of pure noise — and would condemn a threshold that is
    /// fine.
    /// </para>
    /// <para>
    /// So the error is a bounded random walk: each sample moves it by at most this much, and it is
    /// clamped to <see cref="JitterM"/> from truth. Defaults to a fifth of <see cref="JitterM"/>
    /// when unset, which is about the ratio a consumer receiver shows at 1 Hz.
    /// </para>
    /// </remarks>
    public double? JitterDriftM { get; init; }

    /// <summary>Whether the device reports its own ground speed. Cheap trackers often do not.</summary>
    public bool ReportSpeed { get; init; } = true;

    /// <summary>What the device <i>claims</i>, when that differs from what it did — a speed spoof.</summary>
    public double? ReportedSpeedKph { get; init; }

    /// <summary>Silence before this leg's first sample, in seconds — a tunnel, or a dark spell.</summary>
    public double GapSeconds { get; init; }

    /// <summary>Displace this leg's start by this many km along <see cref="BearingDeg"/> — the teleport.</summary>
    public double? JumpKm { get; init; }

    /// <summary>Add this to every timestamp in the leg — the forward/backward clock skew.</summary>
    public double ClockOffsetSeconds { get; init; }

    /// <summary>Stamp every sample in the leg with the same instant — a tracker with a stuck clock.</summary>
    public bool ClockFrozen { get; init; }

    /// <summary>Walk timestamps backwards at the cadence instead of forwards.</summary>
    public bool ClockRewind { get; init; }

    /// <summary>Re-emit an earlier leg's samples verbatim, as live — the replayed track.</summary>
    /// <remarks>
    /// Zero-based index into the track's own legs. The positions, the accuracies and the satellite
    /// counts are the originals; only the arrival is new. That is what a captured-and-replayed
    /// track actually is, and it is why the plausibility gate has so little to say about one —
    /// every sample is consistent with the sample before it, because it once was.
    /// </remarks>
    public int? ReplayOfLeg { get; init; }

    /// <summary>Mark this leg's samples on the <c>pos/replay</c> stream (T-05), not the live one.</summary>
    public bool OnReplayStream { get; init; }

    /// <summary>Whether this leg is the attack. Only hostile samples count toward the escape rate.</summary>
    public bool Hostile { get; init; }

    /// <summary>
    /// Which <c>PlausibilityCheck</c> must refuse a hostile leg — <c>None</c> for a known gap.
    /// </summary>
    public string? Expect { get; init; }

    /// <summary>
    /// How many of the leg's samples must be refused, when not all of them can be.
    /// </summary>
    /// <remarks>
    /// A teleport is one bad step: the sample that lands 300 km away is refused, and — because a
    /// refused sample never becomes the position the next one is measured against (C039's ordering
    /// fence) — every sample after it is measured against the vehicle's *real* last position and is
    /// refused too. A track that walks back is the interesting case, and this is where it is said.
    /// </remarks>
    public int? MinRefused { get; init; }
}

/// <summary>The bounds the corpus is measured against, held beside the data they judge.</summary>
/// <param name="MaxHonestRefusalRate">
/// The agreed false-positive bound the DoD names. A refusal here is a vehicle taken off the live
/// map and, for a Mode C driver, out of the candidate pool — so the number is small.
/// </param>
/// <param name="MaxHostileEscapeRate">
/// The false-negative bound, over hostile samples that are not on a <see cref="CorpusTrack.KnownGap"/>
/// track. Zero: a corpus is a set of attacks somebody wrote down, and one this suite knows about
/// and does not catch is a finding, not a rate.
/// </param>
public sealed record CorpusBounds(double MaxHonestRefusalRate, double MaxHostileEscapeRate);

/// <summary>The corpus file.</summary>
public sealed record CorpusDocument(CorpusBounds Bounds, IReadOnlyList<CorpusTrack> Tracks);
