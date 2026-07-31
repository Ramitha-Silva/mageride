using System.Collections.Frozen;
using MageRide.Transit.Domain;

namespace MageRide.Transit.Feed;

/// <summary>
/// One loaded GTFS feed, immutable, and everything route matching needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaced wholesale, never mutated.</b> Activation swaps the live tables in one transaction
/// (AL-54); this side matches it — a reload builds a whole new <see cref="GtfsFeed"/> and one
/// reference assignment publishes it. A request that started under the old feed finishes under the
/// old feed, which is what stops a half-loaded feed being served.
/// </para>
/// <para>
/// <b>Empty is a valid feed and is not the same as no feed.</b> <see cref="Empty"/> is what a
/// deployment before its first import holds, and AL-55 makes that a safety net rather than the
/// expected state — so it is carried as <see cref="IsActive"/> = false and the answer on the wire
/// says so, instead of looking like a corridor no bus serves.
/// </para>
/// </remarks>
public sealed class GtfsFeed
{
    /// <summary>What a deployment with no activated feed holds (AL-55's safety net).</summary>
    public static readonly GtfsFeed Empty = new(null, null, [], [], [], []);

    private readonly FrozenDictionary<string, TransitStop> _stops;
    private readonly FrozenDictionary<string, TransitRoute> _routes;
    private readonly FrozenDictionary<string, string> _shapes;

    /// <summary>Patterns indexed by the halts they call at, so a nearby stop is a direct lookup.</summary>
    private readonly FrozenDictionary<string, RoutePattern[]> _patternsByStop;

    public GtfsFeed(
        Guid? feedVersionId,
        string? feedInfoVersion,
        IReadOnlyList<TransitStop> stops,
        IReadOnlyList<TransitRoute> routes,
        IReadOnlyList<RoutePattern> patterns,
        IReadOnlyList<KeyValuePair<string, string>> shapes)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(shapes);

        FeedVersionId = feedVersionId;
        FeedInfoVersion = feedInfoVersion;

        Stops = stops;
        Patterns = patterns;

        _stops = stops.ToFrozenDictionary(stop => stop.StopId, StringComparer.Ordinal);
        _routes = routes.ToFrozenDictionary(route => route.RouteId, StringComparer.Ordinal);
        _shapes = shapes.ToFrozenDictionary(StringComparer.Ordinal);

        _patternsByStop = patterns
            .SelectMany(pattern => pattern.StopIds.Distinct(StringComparer.Ordinal)
                .Select(stopId => (stopId, pattern)))
            .GroupBy(entry => entry.stopId, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.Select(entry => entry.pattern).ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>The <c>transit.gtfs_feed_versions</c> row this was loaded from.</summary>
    public Guid? FeedVersionId { get; }

    /// <summary>The feed's own <c>feed_info.txt</c> version string, when it carried one.</summary>
    public string? FeedInfoVersion { get; }

    /// <summary>Whether an activated feed is behind this. False is AL-55's safety net.</summary>
    public bool IsActive => FeedVersionId is not null;

    public IReadOnlyList<TransitStop> Stops { get; }

    public IReadOnlyList<RoutePattern> Patterns { get; }

    public TransitStop? Stop(string stopId) =>
        _stops.TryGetValue(stopId, out var stop) ? stop : null;

    public TransitRoute? Route(string routeId) =>
        _routes.TryGetValue(routeId, out var route) ? route : null;

    /// <summary>The encoded polyline for a shape, or null when the feed carried no shapes.</summary>
    public string? Shape(string? shapeId) =>
        shapeId is not null && _shapes.TryGetValue(shapeId, out var shape) ? shape : null;

    /// <summary>Every pattern calling at a halt.</summary>
    public IReadOnlyList<RoutePattern> PatternsAt(string stopId) =>
        _patternsByStop.TryGetValue(stopId, out var patterns) ? patterns : [];

    /// <summary>
    /// Halts within <paramref name="radiusM"/> of a point, nearest first.
    /// </summary>
    /// <remarks>
    /// A linear scan, deliberately. A national feed is ~7 600 halts; a haversine over all of them
    /// is tens of microseconds and needs no index to maintain, no rebuild on reload and no second
    /// structure that can disagree with <see cref="Stops"/>. If a feed ever reaches a size where
    /// this matters, the fix is a grid over the same list rather than a query.
    /// </remarks>
    public IReadOnlyList<NearbyStop> StopsNear(double lat, double lng, int radiusM, int limit)
    {
        var found = new List<NearbyStop>();

        foreach (var stop in Stops)
        {
            var distance = Haversine.DistanceM(lat, lng, stop.Lat, stop.Lng);

            if (distance <= radiusM)
            {
                found.Add(new NearbyStop(stop, (int)Math.Round(distance)));
            }
        }

        return [.. found.OrderBy(entry => entry.DistanceM).Take(limit)];
    }
}
