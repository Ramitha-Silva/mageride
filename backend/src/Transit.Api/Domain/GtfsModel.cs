namespace MageRide.Transit.Domain;

/// <summary>A halt, from <c>transit.gtfs_stops</c>.</summary>
public sealed record TransitStop(string StopId, string Name, double Lat, double Lng);

/// <summary>A route, from <c>transit.gtfs_routes</c>.</summary>
public sealed record TransitRoute(
    string RouteId, string? ShortName, string? LongName, string? Agency, int? RouteType);

/// <summary>
/// One distinct stop sequence a route is operated over — a <em>pattern</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not one entry per trip.</b> A GTFS route has hundreds or thousands of trips and only a
/// handful of distinct stop sequences between them; the question BR-23.2 asks — "does a single
/// route's stop sequence cover a stop near the origin <em>before</em> a stop near the
/// destination" — is a question about the sequence, so trips that share one are folded together.
/// A national feed's 18 922 trips collapse to a few thousand patterns, which is what makes the
/// match an in-memory lookup rather than a join over half a million <c>stop_times</c>.
/// </para>
/// <para>
/// <b>Distinct sequences, not one representative per direction.</b> Taking only the longest
/// pattern would be smaller still and would answer wrongly in both directions: it would claim a
/// direct route for a corridor only the full-length trips cover, and it would still miss a
/// short-turn pattern that reaches somewhere the long one does not.
/// </para>
/// </remarks>
/// <param name="StopIds">The halts in order. Index <em>is</em> the sequence position.</param>
/// <param name="Durations">
/// Seconds from the first stop to each stop, where the feed supplies times. A pattern with no
/// times has an empty list — GTFS <c>arr</c>/<c>dep</c> are optional per row and a feed that omits
/// them is still routable, just without a duration.
/// </param>
public sealed record RoutePattern(
    string RouteId,
    string? ShapeId,
    string? Headsign,
    short? Direction,
    IReadOnlyList<string> StopIds,
    IReadOnlyList<int> Durations)
{
    private readonly Dictionary<string, int> _positions = BuildPositions(StopIds);

    /// <summary>Where a halt sits in this sequence, or -1. First occurrence wins on a loop route.</summary>
    public int PositionOf(string stopId) => _positions.TryGetValue(stopId, out var index) ? index : -1;

    /// <summary>Whether this pattern reaches <paramref name="to"/> after <paramref name="from"/>.</summary>
    public bool Covers(string from, string to)
    {
        var start = PositionOf(from);

        return start >= 0 && PositionOf(to) > start;
    }

    /// <summary>Seconds in the vehicle between two positions, when the feed carried times.</summary>
    /// <remarks>
    /// <b>In-vehicle duration, not a departure time.</b> It is a property of the pattern rather
    /// than of today — which matters, because this build has nowhere to put GTFS's service
    /// calendar (see <c>TransitRouting</c>) and so cannot say whether a given trip runs today.
    /// </remarks>
    public int? DurationBetween(int from, int to) =>
        Durations.Count > to && to > from && from >= 0 ? Durations[to] - Durations[from] : null;

    private static Dictionary<string, int> BuildPositions(IReadOnlyList<string> stopIds)
    {
        var positions = new Dictionary<string, int>(stopIds.Count, StringComparer.Ordinal);

        for (var index = 0; index < stopIds.Count; index++)
        {
            // First occurrence wins: a loop route that returns to a halt is boarded at the first
            // chance, and taking the later one would invent a longer ride than anybody would take.
            positions.TryAdd(stopIds[index], index);
        }

        return positions;
    }
}

/// <summary>An origin or destination halt, with how far the passenger walks to it.</summary>
public sealed record NearbyStop(TransitStop Stop, int DistanceM);

/// <summary>Great-circle distance, which is what a 400 m halt radius is measured in.</summary>
public static class Haversine
{
    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>Metres between two WGS-84 points (haversine).</summary>
    /// <remarks>
    /// Haversine rather than PostGIS, because the halt lookup runs against the in-memory stop set
    /// on every request and a round trip to the database for a distance would put a query on the
    /// path of a screen the passenger is already looking at. The two agree to well under a metre
    /// at these distances, and the tolerance that matters is the 400 m radius itself.
    /// </remarks>
    public static double DistanceM(double lat1, double lng1, double lat2, double lng2)
    {
        var phi1 = double.DegreesToRadians(lat1);
        var phi2 = double.DegreesToRadians(lat2);
        var deltaPhi = double.DegreesToRadians(lat2 - lat1);
        var deltaLambda = double.DegreesToRadians(lng2 - lng1);

        var a = (Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2))
                + (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2));

        return 2 * EarthRadiusM * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
