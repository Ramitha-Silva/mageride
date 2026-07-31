using MageRide.Fare.Distance;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Fare.Tests.Unit;

/// <summary>
/// E-04 — "raw-GPS distance inflation is measurably reduced by the filter on a replayed noisy
/// track", which is this component's third definition-of-done item.
/// </summary>
/// <remarks>
/// <b>Every track here is generated from a fixed seed.</b> A filter tested against random noise
/// passes or fails by luck, and the number under test is money: the assertions are about a specific
/// replayable trace, so a regression is a failure rather than a flake.
/// </remarks>
public sealed class KalmanTrackTests
{
    /// <summary>Colombo Fort, near enough — the frame is local, so the origin only sets the scale.</summary>
    private static readonly GeoPoint Origin = new(6.9344, 79.8428);

    private static readonly DateTimeOffset Start = new(2026, 7, 30, 3, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// A vehicle driving due east in a straight line at 10 m/s, with Gaussian noise on every fix.
    /// The true distance is known exactly, so both the raw and the filtered figure can be scored
    /// against it rather than against each other.
    /// </summary>
    [Fact]
    public void The_filter_moves_a_noisy_straight_line_closer_to_its_true_length()
    {
        const int seconds = 300;
        const double speedMps = 10;

        var truth = seconds * speedMps / 1_000.0;

        var track = KalmanTrack.Filter(StraightLine(seconds, speedMps, noiseM: 8, seed: 20260730));

        var rawError = Math.Abs(track.RawDistanceKm - truth);
        var filteredError = Math.Abs(track.DistanceKm - truth);

        Assert.True(
            track.RawDistanceKm > truth,
            $"the raw sum should be inflated by the noise; truth {truth:F3} km, raw {track.RawDistanceKm:F3} km");

        Assert.True(
            filteredError < rawError,
            $"the filter should be closer to the truth: raw error {rawError:F3} km, filtered error {filteredError:F3} km");

        // E-04 puts the inflation at 5–15%. The assertion is that most of it is removed, not that a
        // particular fraction is — the exact figure depends on the noise model and would make this
        // a test of the generator.
        Assert.True(
            filteredError < rawError / 2,
            $"more than half the inflation should be removed: raw {rawError:F3} km, filtered {filteredError:F3} km");
    }

    /// <summary>
    /// The failure that costs the most money: a vehicle standing still at a light still reports a
    /// fix every second, and each one lands metres from the last. Summing those turns a ninety-second
    /// wait into a few hundred metres of fare.
    /// </summary>
    [Fact]
    public void A_stationary_vehicle_travels_almost_nothing()
    {
        var samples = Stationary(seconds: 180, noiseM: 6, seed: 4242);

        var track = KalmanTrack.Filter(samples);

        Assert.True(
            track.RawDistanceKm > 0.3,
            $"the raw sum of a stationary wobble should be substantial; it was {track.RawDistanceKm:F3} km");

        Assert.True(
            track.DistanceKm < 0.05,
            $"a stationary vehicle should accumulate almost nothing; it accumulated {track.DistanceKm:F3} km");
    }

    /// <summary>
    /// A single-sample coordinate glitch — a dropped digit, an urban-canyon reflection — adds
    /// kilometres rather than metres, so it is rejected outright before the filter sees it.
    /// </summary>
    [Fact]
    public void A_teleport_is_rejected_rather_than_smoothed()
    {
        var samples = StraightLine(60, speedMps: 10, noiseM: 2, seed: 7).ToList();

        // One fix half a degree east — about 55 km away, and back again on the next sample.
        samples[30] = samples[30] with { Lng = samples[30].Lng + 0.5 };

        var track = KalmanTrack.Filter(samples);

        Assert.Equal(1, track.RejectedCount);
        Assert.True(
            track.DistanceKm < 1.0,
            $"a 55 km glitch must not reach the distance; the filtered track was {track.DistanceKm:F3} km");
    }

    /// <summary>A fix too uncertain to inform anything is dropped rather than weighted in.</summary>
    [Fact]
    public void Fixes_worse_than_the_accuracy_ceiling_are_dropped()
    {
        var samples = StraightLine(30, speedMps: 10, noiseM: 2, seed: 11)
            .Select((s, i) => i % 3 == 0 ? s with { AccuracyM = 400 } : s)
            .ToList();

        var track = KalmanTrack.Filter(samples);

        Assert.Equal(samples.Count(static s => s.AccuracyM > 50), track.RejectedCount);
    }

    /// <summary>
    /// D5' §1.2's <c>distance_calculation_failed</c>: a track with nothing usable in it produces no
    /// distance at all, and the caller falls back to the estimate rather than charging zero.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_track_too_short_to_measure_produces_nothing(int sampleCount)
    {
        var track = KalmanTrack.Filter(StraightLine(sampleCount, 10, 2, seed: 1).Take(sampleCount).ToList());

        Assert.Equal(0, track.DistanceKm);
        Assert.Empty(track.Points);
    }

    /// <summary>
    /// The filter must not eat a real journey. A clean track with no noise comes back within a few
    /// per cent of its true length — a filter that removed inflation by removing distance would
    /// under-charge every ride and pass the two tests above.
    /// </summary>
    [Fact]
    public void A_clean_track_keeps_its_length()
    {
        const int seconds = 200;
        const double speedMps = 12;

        var truth = seconds * speedMps / 1_000.0;

        var track = KalmanTrack.Filter(StraightLine(seconds, speedMps, noiseM: 0, seed: 3));

        Assert.InRange(track.DistanceKm, truth * 0.95, truth * 1.05);
    }

    /// <summary>
    /// The over-smoothing guard. A low process noise makes the filter trust its
    /// constant-velocity model, which is what keeps a stationary vehicle still — and the risk is
    /// that it then coasts through a real turn and loses the corner. A route with four right-angle
    /// turns must keep its length.
    /// </summary>
    /// <remarks>
    /// This is the test that stops <see cref="KalmanTrackOptions.ProcessNoise"/> being tuned down
    /// until the stationary case passes and every real journey quietly under-charges.
    /// </remarks>
    [Fact]
    public void A_route_with_right_angle_turns_keeps_its_length()
    {
        const double speedMps = 10;
        const int legSeconds = 40;

        var truth = 4 * legSeconds * speedMps / 1_000.0;

        var track = KalmanTrack.Filter(Square(legSeconds, speedMps, noiseM: 5, seed: 1234));

        Assert.InRange(track.DistanceKm, truth * 0.90, truth * 1.10);
    }

    /// <summary>
    /// A vehicle creeping forward in traffic keeps its distance: the movement gate compares against
    /// the last <em>counted</em> point, so several sub-gate steps still accumulate.
    /// </summary>
    [Fact]
    public void A_vehicle_creeping_in_traffic_still_accumulates_distance()
    {
        // 0.5 m/s for five minutes — 150 m, in steps smaller than the position uncertainty.
        var track = KalmanTrack.Filter(StraightLine(300, speedMps: 0.5, noiseM: 3, seed: 99));

        Assert.True(
            track.DistanceKm > 0.08,
            $"a slow crawl is still travel; the filter kept only {track.DistanceKm:F3} km of ~0.15 km");
    }

    // ------------------------------------------------------------------------------------------
    // Deterministic track generation
    // ------------------------------------------------------------------------------------------

    /// <summary>A due-east line at a constant speed, one fix per second, with Gaussian noise.</summary>
    private static List<TrackSample> StraightLine(int seconds, double speedMps, double noiseM, int seed)
    {
        var random = new Random(seed);
        var metresPerDegreeLng = 111_320.0 * Math.Cos(double.DegreesToRadians(Origin.Latitude));

        var samples = new List<TrackSample>(seconds + 1);

        for (var i = 0; i <= seconds; i++)
        {
            var east = i * speedMps;

            samples.Add(new TrackSample(
                SampleTs: Start.AddSeconds(i),
                Lat: Origin.Latitude + (Gaussian(random, noiseM) / 110_574.0),
                Lng: Origin.Longitude + ((east + Gaussian(random, noiseM)) / metresPerDegreeLng),
                AccuracyM: Math.Max(1, noiseM)));
        }

        return samples;
    }

    /// <summary>The same, standing still.</summary>
    private static List<TrackSample> Stationary(int seconds, double noiseM, int seed) =>
        StraightLine(seconds, speedMps: 0, noiseM: noiseM, seed: seed);

    /// <summary>Four legs at right angles — east, north, west, south — back to the start.</summary>
    private static List<TrackSample> Square(int legSeconds, double speedMps, double noiseM, int seed)
    {
        var random = new Random(seed);
        var metresPerDegreeLng = 111_320.0 * Math.Cos(double.DegreesToRadians(Origin.Latitude));

        (double East, double North)[] headings = [(1, 0), (0, 1), (-1, 0), (0, -1)];

        var samples = new List<TrackSample>((legSeconds * 4) + 1);
        double east = 0, north = 0;

        for (var leg = 0; leg < headings.Length; leg++)
        {
            for (var i = 0; i < legSeconds; i++)
            {
                east += headings[leg].East * speedMps;
                north += headings[leg].North * speedMps;

                samples.Add(new TrackSample(
                    SampleTs: Start.AddSeconds(samples.Count),
                    Lat: Origin.Latitude + ((north + Gaussian(random, noiseM)) / 110_574.0),
                    Lng: Origin.Longitude + ((east + Gaussian(random, noiseM)) / metresPerDegreeLng),
                    AccuracyM: Math.Max(1, noiseM)));
            }
        }

        return samples;
    }

    /// <summary>Box–Muller, so the noise is normal rather than uniform — GPS error is Gaussian.</summary>
    private static double Gaussian(Random random, double sigma)
    {
        if (sigma <= 0)
        {
            return 0;
        }

        var u1 = 1.0 - random.NextDouble();
        var u2 = 1.0 - random.NextDouble();

        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>Guards the generator itself: a "straight line" that is not straight proves nothing.</summary>
    [Fact]
    public void The_generator_produces_the_length_it_claims()
    {
        var clean = StraightLine(100, speedMps: 10, noiseM: 0, seed: 0);

        var measured = GeoMath.DistanceM(
            new GeoPoint(clean[0].Lat, clean[0].Lng),
            new GeoPoint(clean[^1].Lat, clean[^1].Lng));

        Assert.InRange(measured, 995, 1_005);
    }
}
