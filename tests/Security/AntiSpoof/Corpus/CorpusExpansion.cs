using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;

namespace MageRide.Security.Tests.AntiSpoof.Corpus;

/// <summary>
/// One expanded sample, and what the corpus says about it.
/// </summary>
/// <param name="Sample">What the vehicle published.</param>
/// <param name="Hostile">Whether this sample is the attack rather than its run-up.</param>
/// <param name="Expect">The check that must refuse it, or <c>None</c> for a documented gap.</param>
/// <param name="IsReplay">Whether it arrived on <c>veh/{id}/pos/replay</c> (T-05).</param>
/// <param name="Leg">Index of the leg it came from — so a failure names the segment, not just the track.</param>
public sealed record CorpusSample(
    PositionSample Sample,
    bool Hostile,
    PlausibilityCheck Expect,
    bool IsReplay,
    int Leg);

/// <summary>
/// Turns a <see cref="CorpusTrack"/>'s legs into the samples a vehicle would have published.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic to the metre.</b> The jitter is drawn from a xorshift seeded off the track id,
/// not from <c>System.Random</c>, because a false-positive rate that moves between runs cannot be
/// compared against a bound — and a seeded <c>Random</c> is only promised to be stable inside one
/// .NET version. Six lines here buy a number that means the same thing next year.
/// </para>
/// <para>
/// <b>Receiver error is a bounded random walk, not a fresh draw per fix.</b> See
/// <c>CorpusLeg.JitterDriftM</c> — the difference decides whether the measured false-positive rate
/// is a property of the platform or of the model.
/// </para>
/// </remarks>
internal static class CorpusExpansion
{
    private const double EarthRadiusM = 6_371_008.8;
    private const double MetresPerSecondPerKph = 1_000d / 3_600d;

    public static IReadOnlyList<CorpusSample> Expand(CorpusTrack track, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(track);

        var vehicleId = StableVehicleId(track.Id);
        var source = Enum.Parse<PositionSource>(track.Source, ignoreCase: false);
        var wobble = new Xorshift(StableSeed(track.Id));

        // The receiver's current error, in metres east and north of the true position. Carried
        // across legs, because a tracker does not get a clean fix because the vehicle turned.
        var errorEastM = 0d;
        var errorNorthM = 0d;

        var cursor = new GeoPoint(track.Start[0], track.Start[1]);
        var instant = track.StartsAtUtc ?? now;

        // The latest point on the UNSKEWED timeline, which is what the track is anchored by. Taking
        // it from the reported instants instead would let the one-hour-ahead track drag its own
        // honest run-up an hour into the past and quietly disarm the gate it exists to fire.
        var latest = instant;

        var expanded = new List<CorpusSample>();
        var byLeg = new List<List<CorpusSample>>();

        for (var legIndex = 0; legIndex < track.Legs.Count; legIndex++)
        {
            var leg = track.Legs[legIndex];
            var produced = new List<CorpusSample>(leg.Count);

            if (leg.ReplayOfLeg is { } replayed)
            {
                // A replayed track is the original bytes arriving again. Position, instant, accuracy,
                // satellite count and — critically — `seq` are the originals; only the arrival is new.
                foreach (var original in byLeg[replayed])
                {
                    produced.Add(original with
                    {
                        Sample = original.Sample with { ReceivedTs = instant },
                        Hostile = leg.Hostile,
                        Expect = ParseExpect(leg.Expect),
                        IsReplay = leg.OnReplayStream,
                        Leg = legIndex,
                    });
                }

                expanded.AddRange(produced);
                byLeg.Add(produced);
                continue;
            }

            var stepM = leg.SpeedKph * MetresPerSecondPerKph * leg.CadenceSeconds;

            // A leg boundary is a sample interval like any other, in time AND in space. Without
            // this the first sample of every leg shares its predecessor's instant and position,
            // which turns every multi-leg track into a same-timestamp burst that has not moved —
            // so a leg that raises the speed appears to start from standstill and its opening
            // sample is correctly accepted, reading as an escape the platform never had.
            if (legIndex > 0)
            {
                instant += TimeSpan.FromSeconds(leg.CadenceSeconds);
                cursor = Advance(cursor, leg.BearingDeg, stepM);
            }

            instant += TimeSpan.FromSeconds(leg.GapSeconds);

            if (leg.JumpKm is { } jump)
            {
                cursor = Advance(cursor, leg.BearingDeg, jump * 1_000d);
            }
            var frozenAt = instant;

            for (var i = 0; i < leg.Count; i++)
            {
                if (i > 0)
                {
                    cursor = Advance(cursor, leg.BearingDeg, stepM);
                    instant += TimeSpan.FromSeconds(leg.ClockRewind ? -leg.CadenceSeconds : leg.CadenceSeconds);
                }

                latest = instant > latest ? instant : latest;

                var reported = leg.ClockFrozen ? frozenAt : instant;
                reported += TimeSpan.FromSeconds(leg.ClockOffsetSeconds);

                var fix = cursor;

                if (leg.JitterM > 0)
                {
                    var drift = leg.JitterDriftM ?? (leg.JitterM / 5d);
                    var step = drift * Math.Sqrt(wobble.NextDouble());
                    var direction = wobble.NextDouble() * 2d * Math.PI;

                    errorEastM += step * Math.Sin(direction);
                    errorNorthM += step * Math.Cos(direction);

                    // Clamped rather than reflected: an error that walked freely would drift off
                    // over a long leg and the track would end somewhere the vehicle never was.
                    var magnitude = Math.Sqrt((errorEastM * errorEastM) + (errorNorthM * errorNorthM));

                    if (magnitude > leg.JitterM)
                    {
                        var scale = leg.JitterM / magnitude;
                        errorEastM *= scale;
                        errorNorthM *= scale;
                        magnitude = leg.JitterM;
                    }

                    if (magnitude > 0)
                    {
                        var bearing = Math.Atan2(errorEastM, errorNorthM) * 180d / Math.PI;
                        fix = Advance(cursor, bearing, magnitude);
                    }
                }

                var speedKph = leg.ReportedSpeedKph ?? leg.SpeedKph;

                produced.Add(new CorpusSample(
                    new PositionSample(
                        VehicleId: vehicleId,
                        SampleTs: reported,
                        // tcp-adapter's rule (`CapturedAt.ToUnixTimeMilliseconds()`), so a replayed
                        // sample carries a seq at or below the watermark by construction rather
                        // than by the corpus asserting it.
                        Seq: reported.ToUnixTimeMilliseconds(),
                        Lat: fix.Latitude,
                        Lng: fix.Longitude,
                        Source: source,
                        ReceivedTs: instant,
                        SpeedMps: leg.ReportSpeed ? speedKph * MetresPerSecondPerKph : null,
                        AccuracyM: leg.AccuracyM,
                        SatCount: leg.SatCount,
                        VehicleType: track.VehicleType),
                    Hostile: leg.Hostile,
                    Expect: ParseExpect(leg.Expect),
                    IsReplay: leg.OnReplayStream,
                    Leg: legIndex));
            }

            expanded.AddRange(produced);
            byLeg.Add(produced);
        }

        // Anchored so the track ENDS at `now`, unless it pinned its own start.
        //
        // Every track has to land in the recent past, because `MaxClockSkewAhead` compares each
        // sample against the wall clock: a forty-sample bus leg at a 10 s cadence spans 400 s, and
        // a track that began at `now` would have its own tail refused as six minutes in the future.
        // That failure would read as a threshold regression and is a fixture bug.
        return track.StartsAtUtc is not null
            ? expanded
            : [.. expanded.Select(candidate => Shift(candidate, now - latest))];
    }

    private static CorpusSample Shift(CorpusSample candidate, TimeSpan by)
    {
        var shifted = candidate.Sample.SampleTs + by;

        return candidate with
        {
            Sample = candidate.Sample with
            {
                SampleTs = shifted,
                Seq = shifted.ToUnixTimeMilliseconds(),
                ReceivedTs = candidate.Sample.ReceivedTs + by,
            },
        };
    }

    /// <summary>The point reached by travelling <paramref name="distanceM"/> on a bearing.</summary>
    private static GeoPoint Advance(GeoPoint from, double bearingDeg, double distanceM)
    {
        var delta = distanceM / EarthRadiusM;
        var theta = bearingDeg * Math.PI / 180d;
        var lat = from.Latitude * Math.PI / 180d;
        var lng = from.Longitude * Math.PI / 180d;

        var destLat = Math.Asin((Math.Sin(lat) * Math.Cos(delta))
            + (Math.Cos(lat) * Math.Sin(delta) * Math.Cos(theta)));

        var destLng = lng + Math.Atan2(
            Math.Sin(theta) * Math.Sin(delta) * Math.Cos(lat),
            Math.Cos(delta) - (Math.Sin(lat) * Math.Sin(destLat)));

        return new GeoPoint(destLat * 180d / Math.PI, NormaliseLongitude(destLng * 180d / Math.PI));
    }

    private static double NormaliseLongitude(double degrees) => ((degrees + 540d) % 360d) - 180d;

    private static PlausibilityCheck ParseExpect(string? expect) =>
        expect is null ? PlausibilityCheck.None : Enum.Parse<PlausibilityCheck>(expect, ignoreCase: false);

    /// <summary>A vehicle id derived from the track id, so a failure names the track it came from.</summary>
    private static Guid StableVehicleId(string trackId)
    {
        var bytes = new byte[16];
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trackId));
        Array.Copy(hash, bytes, 16);

        return new Guid(bytes);
    }

    private static ulong StableSeed(string trackId)
    {
        // FNV-1a. Any stable mixing function would do; what matters is that it is written here
        // rather than inherited from a runtime that is free to change it.
        var hash = 14695981039346656037UL;

        foreach (var c in trackId)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash == 0 ? 1 : hash;
    }

    /// <summary>xorshift64*, so the corpus draws the same wobble on every machine and every year.</summary>
    private sealed class Xorshift(ulong seed)
    {
        private ulong _state = seed;

        public double NextDouble()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;

            return (_state * 2685821657736338717UL >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}
