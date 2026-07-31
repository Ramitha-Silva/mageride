using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Fare.Distance;

/// <summary>One raw GPS sample as <c>telemetry.positions</c> stores it (migration 1801).</summary>
/// <param name="AccuracyM">
/// Reported horizontal accuracy in metres. <see langword="null"/> on trackers that do not report
/// one, which is treated as the pessimistic default rather than as "perfect".
/// </param>
public sealed record TrackSample(DateTimeOffset SampleTs, double Lat, double Lng, double? AccuracyM);

/// <summary>What the filter made of a track.</summary>
/// <param name="DistanceKm">The distance the fare is charged on (E-04).</param>
/// <param name="RawDistanceKm">
/// The naive sum over the unfiltered samples, kept so the inflation the filter removed is a number
/// somebody can look at rather than a claim.
/// </param>
/// <param name="Points">The filtered track, for the receipt and for query-svc's trip line.</param>
public sealed record FilteredTrack(
    IReadOnlyList<GeoPoint> Points,
    double DistanceKm,
    double RawDistanceKm,
    int SampleCount,
    int RejectedCount)
{
    public static readonly FilteredTrack Empty = new([], 0, 0, 0, 0);

    /// <summary>
    /// How much of the raw distance was noise, as a fraction. E-04 expects 5–15% on a real track.
    /// </summary>
    public double InflationRemoved => RawDistanceKm > 0 ? (RawDistanceKm - DistanceKm) / RawDistanceKm : 0;
}

/// <summary>Knobs for <see cref="KalmanTrack"/>. Every default is argued at its declaration.</summary>
public sealed record KalmanTrackOptions
{
    /// <summary>
    /// Process noise — the acceleration variance the model expects, m²/s³.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No spec pins it.</b> It is the one number that decides how much the filter trusts its
    /// constant-velocity model against the measurement, and it trades the two failure modes off
    /// against each other: too high and the filter follows the jitter it exists to remove — a
    /// vehicle standing at a light drifts, and the drift is charged — too low and it coasts through
    /// a real corner and the distance comes out short.
    /// </para>
    /// <para>
    /// 0.05 was chosen by sweeping it against both, not by argument from physics: at 1 Hz it is the
    /// largest value that holds a three-minute stop under 50 m, and
    /// <c>A_route_with_right_angle_turns_keeps_its_length</c> is the test that stops it being tuned
    /// any lower — four 90° turns must still measure within 10% of their true length. Retune the
    /// pair together, and delete both when OSRM <c>match</c> lands (Phase 3).
    /// </para>
    /// </remarks>
    public double ProcessNoise { get; init; } = 0.05;

    /// <summary>
    /// Assumed accuracy for a sample that reports none, in metres.
    /// </summary>
    /// <remarks>
    /// Pessimistic on purpose: a tracker that does not report accuracy is usually a cheap one, and
    /// treating an unknown as perfect would give it the whole weight in the update.
    /// </remarks>
    public double DefaultAccuracyM { get; init; } = 20.0;

    /// <summary>
    /// Samples worse than this are dropped before filtering.
    /// </summary>
    /// <remarks>
    /// A fix with 50 m of uncertainty carries more noise than signal over a city block; feeding it
    /// in moves the estimate without informing it.
    /// </remarks>
    public double MaxAccuracyM { get; init; } = 50.0;

    /// <summary>
    /// Implied speed above which a sample is a teleport, m/s. 55 m/s ≈ 200 km/h.
    /// </summary>
    /// <remarks>
    /// Above any speed reachable on a Sri Lankan road. This catches the single-sample coordinate
    /// glitch — a dropped digit, an urban-canyon reflection — which is the failure that adds
    /// kilometres rather than metres.
    /// </remarks>
    public double MaxSpeedMps { get; init; } = 55.0;

    /// <summary>
    /// How many standard deviations of combined positional uncertainty a step must exceed before
    /// it is counted as movement.
    /// </summary>
    /// <remarks>
    /// This is the "accuracy-weighted" half of E-04 and the half that removes most of the
    /// inflation: a vehicle waiting at a light still reports a fix every second, and each one lands
    /// a few metres from the last. Summing those is how a stationary minute becomes 200 m of fare.
    /// </remarks>
    public double MovementGateSigma { get; init; } = 1.0;
}

/// <summary>
/// E-04 — the Kalman filter and accuracy-weighted resample that run over a raw GPS trace before
/// its distance is summed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Raw GPS inflates distance by 5–15% (E-04), and on MageRide that
/// inflation is money: the fare is per kilometre, so every metre of jitter is charged to a
/// passenger who did not travel it. ADD §6 gives fare-svc this filter as Phase 1 and OSRM
/// <c>match</c> snap-to-road as Phase 3; this is Phase 1.
/// </para>
/// <para>
/// <b>Three rules, and they remove different errors.</b> The <em>rejection</em> pass drops fixes
/// that are too uncertain to inform anything and single-sample teleports, which add kilometres. The
/// <em>filter</em> smooths the ordinary metre-scale wobble of a moving vehicle. The <em>movement
/// gate</em> refuses to accumulate a step the position uncertainty cannot distinguish from standing
/// still, which is what a vehicle at a red light does for ninety seconds at a time. Any one of the
/// three alone leaves most of the inflation in place.
/// </para>
/// <para>
/// <b>A constant-velocity model, decoupled per axis.</b> The two axes of a local metric frame are
/// filtered independently — the standard treatment for GPS, and the one that keeps this readable.
/// The frame is an equirectangular projection about the first sample, which is exact enough over a
/// city-scale ride and avoids trigonometry per sample.
/// </para>
/// <para>
/// <b>Failure is the estimate, not an exception.</b> D5' §1.2: <c>distance_calculation_failed</c>
/// falls back to the estimate. A track with nothing usable in it returns
/// <see cref="FilteredTrack.Empty"/> and the caller prices the ride on what it quoted.
/// </para>
/// </remarks>
public static class KalmanTrack
{
    /// <summary>Metres per degree of latitude — the meridian is very nearly constant.</summary>
    private const double MetresPerDegreeLat = 110_574.0;

    /// <summary>Metres per degree of longitude at the equator, scaled by cos(lat) in the frame.</summary>
    private const double MetresPerDegreeLngEquator = 111_320.0;

    /// <summary>Filters a track and returns the distance the fare should be charged on.</summary>
    public static FilteredTrack Filter(IReadOnlyList<TrackSample> samples, KalmanTrackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var settings = options ?? new KalmanTrackOptions();

        if (samples.Count < 2)
        {
            return FilteredTrack.Empty;
        }

        // Chronological, and de-duplicated on the instant: two fixes with the same timestamp give
        // the predict step a dt of zero, and the second carries no new information anyway.
        var ordered = samples
            .OrderBy(static s => s.SampleTs)
            .Where(static s => double.IsFinite(s.Lat) && double.IsFinite(s.Lng))
            .ToArray();

        var rawDistanceM = RawDistanceM(ordered);

        var accepted = Reject(ordered, settings, out var rejected);

        if (accepted.Count < 2)
        {
            return new FilteredTrack([], 0, rawDistanceM / 1000.0, ordered.Length, rejected);
        }

        var smoothed = Smooth(accepted, settings);
        var distanceM = GatedDistanceM(smoothed, settings);

        return new FilteredTrack(
            Points: [.. smoothed.Select(static p => new GeoPoint(p.Lat, p.Lng))],
            DistanceKm: distanceM / 1000.0,
            RawDistanceKm: rawDistanceM / 1000.0,
            SampleCount: ordered.Length,
            RejectedCount: rejected);
    }

    /// <summary>The naive sum a caller would get without any of this — the number E-04 is about.</summary>
    private static double RawDistanceM(IReadOnlyList<TrackSample> samples)
    {
        var total = 0.0;

        for (var i = 1; i < samples.Count; i++)
        {
            total += GeoMath.DistanceM(
                new GeoPoint(samples[i - 1].Lat, samples[i - 1].Lng),
                new GeoPoint(samples[i].Lat, samples[i].Lng));
        }

        return total;
    }

    /// <summary>Drops the fixes that are too uncertain to use, and the teleports.</summary>
    private static List<TrackSample> Reject(
        IReadOnlyList<TrackSample> samples, KalmanTrackOptions settings, out int rejected)
    {
        var accepted = new List<TrackSample>(samples.Count);
        rejected = 0;

        foreach (var sample in samples)
        {
            var accuracy = sample.AccuracyM ?? settings.DefaultAccuracyM;

            if (!double.IsFinite(accuracy) || accuracy > settings.MaxAccuracyM)
            {
                rejected++;
                continue;
            }

            if (accepted.Count > 0)
            {
                var previous = accepted[^1];
                var seconds = (sample.SampleTs - previous.SampleTs).TotalSeconds;

                if (seconds <= 0)
                {
                    // Same instant or out of order after the sort: no new information, and a dt of
                    // zero would divide by zero below.
                    rejected++;
                    continue;
                }

                var metres = GeoMath.DistanceM(
                    new GeoPoint(previous.Lat, previous.Lng), new GeoPoint(sample.Lat, sample.Lng));

                if (metres / seconds > settings.MaxSpeedMps)
                {
                    rejected++;
                    continue;
                }
            }

            accepted.Add(sample);
        }

        return accepted;
    }

    /// <summary>One filtered position, with the uncertainty the movement gate reads.</summary>
    /// <param name="SigmaM">
    /// The <b>measurement</b> uncertainty of the fix this point came from, not the filter's
    /// posterior. The posterior shrinks as the filter converges — after a minute of standing still
    /// it is under a metre — and a gate built from it would reopen for exactly the stationary
    /// vehicle it exists to hold shut. What the gate is asking is "could these two fixes be the same
    /// place?", and that question is answered by how well the receiver knew where it was, which is
    /// the accuracy the sample reported.
    /// </param>
    private readonly record struct SmoothedPoint(double Lat, double Lng, double SigmaM);

    /// <summary>
    /// The constant-velocity Kalman filter, run once forward over each axis.
    /// </summary>
    /// <remarks>
    /// Forward only, deliberately: a fixed-interval smoother would be more accurate, and it would
    /// also make the distance depend on samples that arrive after the one being corrected — which
    /// is fine for a completed ride and wrong for anything incremental. Phase 3's OSRM
    /// <c>match</c> replaces this whole method rather than refining it.
    /// </remarks>
    private static List<SmoothedPoint> Smooth(IReadOnlyList<TrackSample> samples, KalmanTrackOptions settings)
    {
        var originLat = samples[0].Lat;
        var metresPerDegreeLng = MetresPerDegreeLngEquator * Math.Cos(double.DegreesToRadians(originLat));
        var originLng = samples[0].Lng;

        // [position, velocity] per axis, and the 2x2 covariance for each.
        var x = new Axis();
        var y = new Axis();

        var results = new List<SmoothedPoint>(samples.Count);
        var previousTs = samples[0].SampleTs;
        var initialised = false;

        foreach (var sample in samples)
        {
            var accuracy = sample.AccuracyM ?? settings.DefaultAccuracyM;
            var variance = accuracy * accuracy;

            var east = (sample.Lng - originLng) * metresPerDegreeLng;
            var north = (sample.Lat - originLat) * MetresPerDegreeLat;

            if (!initialised)
            {
                x = Axis.At(east, variance);
                y = Axis.At(north, variance);
                initialised = true;
            }
            else
            {
                var dt = (sample.SampleTs - previousTs).TotalSeconds;

                x = x.Predict(dt, settings.ProcessNoise).Update(east, variance);
                y = y.Predict(dt, settings.ProcessNoise).Update(north, variance);
            }

            previousTs = sample.SampleTs;

            results.Add(new SmoothedPoint(
                Lat: originLat + (y.Position / MetresPerDegreeLat),
                Lng: originLng + (x.Position / metresPerDegreeLng),
                SigmaM: accuracy));
        }

        return results;
    }

    /// <summary>
    /// Sums the filtered track, counting only steps the position uncertainty can tell from standing
    /// still.
    /// </summary>
    /// <remarks>
    /// The comparison is against the last <em>counted</em> point rather than the last sample, so a
    /// vehicle genuinely creeping forward accumulates its distance across several sub-gate steps
    /// instead of losing all of it. Without that, a filter tuned to remove a stationary wobble would
    /// also erase a traffic jam.
    /// </remarks>
    private static double GatedDistanceM(IReadOnlyList<SmoothedPoint> points, KalmanTrackOptions settings)
    {
        var total = 0.0;
        var anchor = points[0];

        for (var i = 1; i < points.Count; i++)
        {
            var point = points[i];

            var metres = GeoMath.DistanceM(
                new GeoPoint(anchor.Lat, anchor.Lng), new GeoPoint(point.Lat, point.Lng));

            var gate = settings.MovementGateSigma
                       * Math.Sqrt((anchor.SigmaM * anchor.SigmaM) + (point.SigmaM * point.SigmaM));

            if (metres <= gate)
            {
                continue;
            }

            total += metres;
            anchor = point;
        }

        return total;
    }

    /// <summary>
    /// One axis of the constant-velocity model: state <c>[p, v]</c> and its covariance.
    /// </summary>
    /// <remarks>
    /// A struct with explicit members rather than a matrix library: the model is 2×2, every entry
    /// is written out below, and a reader checking it against a textbook can do so without also
    /// checking a dependency.
    /// </remarks>
    private readonly record struct Axis(
        double Position, double Velocity, double P00, double P01, double P10, double P11)
    {
        /// <summary>The posterior variance of the position — <c>P00</c>, named for the reader.</summary>
        public double PositionVariance => P00;

        /// <summary>
        /// The first fix: the position is the measurement, the velocity is unknown.
        /// </summary>
        /// <remarks>
        /// The velocity's prior variance is deliberately large (a vehicle may already be doing
        /// 30 m/s when the first fix lands) so the second measurement, not the initial guess, is
        /// what establishes it.
        /// </remarks>
        public static Axis At(double position, double variance) =>
            new(position, 0, variance, 0, 0, 1_000);

        /// <summary>
        /// <c>x ← F x</c>, <c>P ← F P Fᵀ + Q</c> with <c>F = [[1, dt], [0, 1]]</c> and the
        /// continuous white-noise-acceleration <c>Q</c>.
        /// </summary>
        public Axis Predict(double dt, double processNoise)
        {
            if (dt <= 0 || !double.IsFinite(dt))
            {
                return this;
            }

            var position = Position + (Velocity * dt);

            // F P Fᵀ, expanded.
            var p00 = P00 + (dt * (P10 + P01)) + (dt * dt * P11);
            var p01 = P01 + (dt * P11);
            var p10 = P10 + (dt * P11);
            var p11 = P11;

            // Q for a constant-velocity model driven by white-noise acceleration of variance q.
            var dt2 = dt * dt;
            var dt3 = dt2 * dt;
            var dt4 = dt2 * dt2;

            return new Axis(
                position,
                Velocity,
                p00 + (processNoise * dt4 / 4),
                p01 + (processNoise * dt3 / 2),
                p10 + (processNoise * dt3 / 2),
                p11 + (processNoise * dt2));
        }

        /// <summary>The scalar measurement update with <c>H = [1, 0]</c> and <c>R = variance</c>.</summary>
        public Axis Update(double measurement, double variance)
        {
            var innovation = measurement - Position;
            var s = P00 + variance;

            if (s <= 0 || !double.IsFinite(s))
            {
                return this;
            }

            var k0 = P00 / s;
            var k1 = P10 / s;

            return new Axis(
                Position + (k0 * innovation),
                Velocity + (k1 * innovation),
                (1 - k0) * P00,
                (1 - k0) * P01,
                P10 - (k1 * P00),
                P11 - (k1 * P01));
        }
    }
}
