using MageRide.Shared.Primitives;

namespace MageRide.Shared.Geo;

/// <summary>
/// Great-circle distance and bearing over WGS-84 coordinates — the arithmetic behind the DT-02
/// Directional Travel predicate (D5' §12.1) and D5' §1.2's "straight-line proximity = haversine".
/// </summary>
/// <remarks>
/// <para>
/// <b>A sphere, not the spheroid.</b> PostGIS <c>ST_Distance</c> on <c>geography</c> uses the
/// ellipsoid and is what decides who is <em>near</em> (D5' §3.1's mandatory post-filter); this is
/// what decides which <em>way</em> a ride points, where the inputs are already approximations of a
/// road network. The two disagree by about 0.2 % over Sri Lanka, which is metres over the tens of
/// kilometres this is used on, and the DT-02 progress test is a difference of two distances taken
/// the same way — so the error very largely cancels rather than accumulating.
/// </para>
/// <para>
/// Kept here rather than in dispatch-svc because a bearing is not a dispatch concept: fare-svc
/// already carries a private haversine (<c>FareEstimator.HaversineKm</c>) and a third copy is how
/// two services end up disagreeing about a distance. Collapsing that one onto this is a
/// micro-change-set in the C036 handoff, not a change made from under it.
/// </para>
/// </remarks>
public static class GeoMath
{
    /// <summary>IUGG mean Earth radius in metres — the radius <c>ST_Distance(…, false)</c> uses.</summary>
    public const double EarthRadiusM = 6_371_008.8;

    /// <summary>Great-circle distance between two points, in metres.</summary>
    public static double DistanceM(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.Latitude);
        var lat2 = ToRadians(to.Latitude);
        var dLat = lat2 - lat1;
        var dLng = ToRadians(to.Longitude - from.Longitude);

        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));

        return 2 * EarthRadiusM * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
    }

    /// <summary>
    /// Initial (forward) bearing from one point to another, in degrees clockwise from true north
    /// and normalised to <c>[0, 360)</c>.
    /// </summary>
    /// <remarks>
    /// The <em>initial</em> bearing: a great circle's heading changes along its length, and the one
    /// DT-02 compares is the direction the journey sets off in. Two points that coincide have no
    /// bearing at all; the result is 0 there, and callers that care — the predicate does — check the
    /// distance first.
    /// </remarks>
    public static double InitialBearingDeg(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.Latitude);
        var lat2 = ToRadians(to.Latitude);
        var dLng = ToRadians(to.Longitude - from.Longitude);

        var y = Math.Sin(dLng) * Math.Cos(lat2);
        var x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLng));

        var degrees = ToDegrees(Math.Atan2(y, x));

        return (degrees + 360d) % 360d;
    }

    /// <summary>
    /// The smaller angle between two bearings, in <c>[0, 180]</c> degrees.
    /// </summary>
    /// <remarks>
    /// Compass arithmetic, so 350° and 10° are 20° apart and not 340°. Getting this wrong would
    /// make the DT-02 predicate reject every ride that happens to cross due north.
    /// </remarks>
    public static double AngularDifferenceDeg(double firstDeg, double secondDeg)
    {
        var difference = Math.Abs(Normalise(firstDeg) - Normalise(secondDeg)) % 360d;

        return difference > 180d ? 360d - difference : difference;
    }

    private static double Normalise(double degrees) => ((degrees % 360d) + 360d) % 360d;

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
