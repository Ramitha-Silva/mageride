using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.Shared.Geo;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PositionProcessor.Plausibility;

/// <summary>Which D-18/T-07 gate refused a sample. The metric's <c>check</c> tag.</summary>
public enum PlausibilityCheck
{
    /// <summary>Nothing refused it.</summary>
    None = 0,

    /// <summary>Reported accuracy circle wider than the ceiling (D-18). Discarded, never smoothed.</summary>
    Accuracy,

    /// <summary>Implied or reported ground speed above the vehicle type's ceiling (D-18, ADD §12.6).</summary>
    Speed,

    /// <summary>Implied speed above the absolute teleport backstop — ADD §12.6's "jump &lt; 1 km/s".</summary>
    Jump,

    /// <summary>A hardware GNSS clock that went backwards, stood still, or ran far ahead (T-07).</summary>
    Clock,

    /// <summary>A hardware fix with too few satellites behind it (T-07).</summary>
    Satellites,
}

/// <summary>What the filter decided, and enough of why to put in a log line and a metric tag.</summary>
/// <param name="Check">The gate that refused it, or <see cref="PlausibilityCheck.None"/>.</param>
/// <param name="Detail">Human-readable reason, for the log line. Never a user-facing string.</param>
public readonly record struct PlausibilityVerdict(PlausibilityCheck Check, string? Detail = null)
{
    /// <summary>The sample may be indexed.</summary>
    public static readonly PlausibilityVerdict Accepted = new(PlausibilityCheck.None);

    /// <summary>Whether the sample passed every gate.</summary>
    public bool IsPlausible => Check is PlausibilityCheck.None;

    internal static PlausibilityVerdict Rejected(PlausibilityCheck check, string detail) => new(check, detail);
}

/// <summary>The D-18 / T-07 anti-spoof gate (D5' §13.1, ADD §12.6).</summary>
public interface IPlausibilityFilter
{
    /// <summary>
    /// Judges one sample against the vehicle's last accepted state.
    /// </summary>
    /// <param name="sample">The decoded, rebound sample.</param>
    /// <param name="previous">
    /// The vehicle's last accepted position, or <see langword="null"/> when there is none — a first
    /// sample, or one after <c>veh:meta</c>'s TTL lapsed. The step checks are skipped in that case;
    /// there is no step to measure.
    /// </param>
    /// <param name="isReplay">
    /// Whether the sample came off <c>pos/replay</c>. A backlog is a vehicle's own history arriving
    /// late, so the checks that compare it against the vehicle's <i>current</i> state do not apply.
    /// </param>
    PlausibilityVerdict Judge(PositionSample sample, LastAcceptedPosition? previous, bool isReplay);
}

/// <inheritdoc cref="IPlausibilityFilter"/>
/// <remarks>
/// <para>
/// Five gates, cheapest first, and every threshold comes from
/// <see cref="PositionProcessorOptions"/> rather than from a literal at the comparison site — this
/// component's first fence, because "per vehicle type" is only true if an operator can retune a
/// tier without a build.
/// </para>
/// <para>
/// <b>A refused sample is dropped, not corrected.</b> D-18 replaced NY's "arbitrary 200 km/h and no
/// accuracy filter" with a per-type table precisely because the previous rule let bad fixes through
/// and had nothing to say about the ones it did catch. Nothing here smooths, clamps or averages: a
/// 500 m error circle is not a worse position, it is a different building, and a Kalman pass over
/// the accepted track is C040's if it is ever wanted.
/// </para>
/// <para>
/// <b>Pure, and deliberately so.</b> Every input is an argument: the sample, the vehicle's last
/// accepted state, and whether this is a backlog. That makes each boundary assertable directly
/// rather than inferred from what came out of Redis afterwards, which matters for a gate whose
/// false positives take a vehicle off the map.
/// </para>
/// </remarks>
public sealed class PlausibilityFilter(IOptions<PositionProcessorOptions> options) : IPlausibilityFilter
{
    private const double MetresPerSecondPerKph = 1_000d / 3_600d;

    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public PlausibilityVerdict Judge(PositionSample sample, LastAcceptedPosition? previous, bool isReplay)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // --- Per-sample gates. These hold for a backlog too: a fix with a 500 m error circle was
        // already useless when it was captured, and no amount of waiting improves it.

        if (sample.AccuracyM is { } accuracy && accuracy > _options.MaxAccuracyM)
        {
            return PlausibilityVerdict.Rejected(
                PlausibilityCheck.Accuracy,
                $"reported accuracy {accuracy:F0} m is worse than the {_options.MaxAccuracyM:F0} m ceiling");
        }

        var ceilingMps = MaxSpeedMpsFor(sample.VehicleType);

        // The device's own speedometer, before any arithmetic. A tracker reporting 300 km/h for a
        // three-wheeler is making a claim it does not need two samples to be wrong about.
        if (sample.SpeedMps is { } reported && !double.IsNaN(reported) && reported > ceilingMps)
        {
            return PlausibilityVerdict.Rejected(
                PlausibilityCheck.Speed,
                $"reported {ToKph(reported):F0} km/h against a {ToKph(ceilingMps):F0} km/h ceiling " +
                $"for '{sample.VehicleType ?? "(untyped)"}'");
        }

        if (SatelliteVerdict(sample) is { IsPlausible: false } satellites)
        {
            return satellites;
        }

        if (isReplay)
        {
            // Everything below measures this sample against where the vehicle is *now*, and a
            // backlog is by definition where it was. T-05's `seq` watermark is what a replay is
            // filtered by; judging it as a teleport would drop a vehicle's whole history the moment
            // it reconnected.
            return PlausibilityVerdict.Accepted;
        }

        if (ClockVerdict(sample, previous) is { IsPlausible: false } clock)
        {
            return clock;
        }

        return previous is { } last ? StepVerdict(sample, last, ceilingMps) : PlausibilityVerdict.Accepted;
    }

    /// <summary>The ceiling for a tier, in m/s. ADD §12.6's table, from configuration.</summary>
    private double MaxSpeedMpsFor(string? vehicleType)
    {
        var kph = vehicleType is { Length: > 0 } type && _options.MaxSpeedKph.TryGetValue(type, out var configured)
            ? configured
            : _options.DefaultMaxSpeedKph;

        return kph * MetresPerSecondPerKph;
    }

    /// <summary>T-07's satellite minimum. Hardware only — D5' §13.1's "hardware additionally".</summary>
    private PlausibilityVerdict SatelliteVerdict(PositionSample sample)
    {
        if (sample.Source is PositionSource.Mobile)
        {
            // A handset reports a fused Wi-Fi/cell/GNSS position; `satCount` describes one of three
            // inputs and is routinely absent or zero on a perfectly good fix.
            return PlausibilityVerdict.Accepted;
        }

        if (sample.SatCount is not { } satellites)
        {
            return _options.RequireSatelliteCount
                ? PlausibilityVerdict.Rejected(
                    PlausibilityCheck.Satellites, "a hardware fix reported no satellite count")
                : PlausibilityVerdict.Accepted;
        }

        return satellites < _options.MinSatellites
            ? PlausibilityVerdict.Rejected(
                PlausibilityCheck.Satellites,
                $"{satellites} satellites, below the minimum of {_options.MinSatellites}")
            : PlausibilityVerdict.Accepted;
    }

    /// <summary>T-07's monotonic GNSS UTC, plus the forward-skew guard that keeps it survivable.</summary>
    private PlausibilityVerdict ClockVerdict(PositionSample sample, LastAcceptedPosition? previous)
    {
        // The forward guard applies to every source, hardware or not: a sample dated years ahead
        // becomes the watermark the *next* sample is judged against, so one bad frame would take a
        // vehicle off the map until `veh:meta` expired. Nothing in D5' names it — see the options.
        var ahead = sample.SampleTs - DateTimeOffset.UtcNow;

        if (ahead > _options.MaxClockSkewAhead)
        {
            return PlausibilityVerdict.Rejected(
                PlausibilityCheck.Clock,
                $"GNSS instant {sample.SampleTs:O} is {ahead.TotalSeconds:F0} s ahead of the platform clock");
        }

        if (sample.Source is PositionSource.Mobile || previous is not { } last)
        {
            // D5' §13.1 asks for the monotonic clock on hardware only. A handset's clock is the
            // user's to set and Android will happily move it backwards mid-track; `seq` is what
            // orders a handset's samples (R-17), and it is checked by the watermark.
            return PlausibilityVerdict.Accepted;
        }

        return sample.SampleTs <= last.SampleTs
            ? PlausibilityVerdict.Rejected(
                PlausibilityCheck.Clock,
                $"GNSS instant {sample.SampleTs:O} is not after the last accepted {last.SampleTs:O}")
            : PlausibilityVerdict.Accepted;
    }

    /// <summary>D-18's teleport gate: the speed the step from the last accepted fix implies.</summary>
    private PlausibilityVerdict StepVerdict(PositionSample sample, LastAcceptedPosition last, double ceilingMps)
    {
        // Clamped, never zero. Most trackers stamp `sampleTs` to a whole second, so two fixes a
        // fraction of a second apart arrive bearing the same instant and there is nothing to divide
        // by; and over a very short gap the numerator is the fix's error circle rather than the
        // vehicle's motion. Judging at the floor keeps a genuine teleport catchable — skipping
        // would hand a spoofer the gate by publishing two samples with one timestamp.
        var elapsed = Math.Max(
            (sample.SampleTs - last.SampleTs).TotalSeconds, _options.MinStepInterval.TotalSeconds);

        var distanceM = GeoMath.DistanceM(last.Point, sample.Point);
        var impliedMps = distanceM / elapsed;

        // Checked before the per-type ceiling so the louder failure wins the tag: a vehicle crossing
        // the island between two samples is a different problem from one driving too fast for its
        // tier, and it is the only gate a tier with no ADD §12.6 row still has.
        var jumpMps = _options.MaxJumpSpeedKph * MetresPerSecondPerKph;

        if (impliedMps > jumpMps)
        {
            return PlausibilityVerdict.Rejected(
                PlausibilityCheck.Jump,
                $"{distanceM:F0} m in {elapsed:F1} s implies {ToKph(impliedMps):F0} km/h, past the " +
                $"{_options.MaxJumpSpeedKph:F0} km/h teleport backstop");
        }

        return impliedMps > ceilingMps
            ? PlausibilityVerdict.Rejected(
                PlausibilityCheck.Speed,
                $"{distanceM:F0} m in {elapsed:F1} s implies {ToKph(impliedMps):F0} km/h against a " +
                $"{ToKph(ceilingMps):F0} km/h ceiling for '{sample.VehicleType ?? "(untyped)"}'")
            : PlausibilityVerdict.Accepted;
    }

    private static double ToKph(double metresPerSecond) => metresPerSecond / MetresPerSecondPerKph;
}
