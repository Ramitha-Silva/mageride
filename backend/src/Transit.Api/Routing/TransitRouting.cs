using MageRide.Transit.Configuration;
using MageRide.Transit.Domain;
using MageRide.Transit.Feed;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Routing;

/// <summary>One boarding-to-alighting ride on a single route.</summary>
public sealed record RouteLeg(
    RoutePattern Pattern,
    TransitStop BoardStop,
    TransitStop AlightStop,
    int BoardWalkM,
    int AlightWalkM,
    int StopsTravelled,
    int? DurationSec);

/// <summary>A direct ride, or a ride with one transfer (BR-23.2).</summary>
public sealed record RouteOption(bool IsDirect, IReadOnlyList<RouteLeg> Legs)
{
    /// <summary>What the passenger walks: to the first halt, plus off the last.</summary>
    /// <remarks>
    /// The transfer walk is deliberately not added — a transfer here is at <em>one</em> halt, so
    /// there is nothing to walk between. A transfer across two nearby halts would be a different
    /// option shape and a different promise about the interchange.
    /// </remarks>
    public int WalkingDistanceM => Legs[0].BoardWalkM + Legs[^1].AlightWalkM;

    public int StopsTravelled => Legs.Sum(leg => leg.StopsTravelled);

    /// <summary>Total in-vehicle seconds, or null when any leg's feed carried no times.</summary>
    public int? TotalDurationSec =>
        Legs.All(leg => leg.DurationSec is not null) ? Legs.Sum(leg => leg.DurationSec!.Value) : null;
}

/// <summary>The answer to "how do I get from here to there by bus", plus whether we could tell.</summary>
/// <param name="HasActiveFeed">
/// <see langword="false"/> is AL-55's safety net and is <b>not</b> the same as an empty option list
/// on a live feed: one means "no bus goes there", the other means "we cannot say".
/// </param>
public sealed record RoutingResult(
    bool HasActiveFeed, string? FeedVersion, IReadOnlyList<RouteOption> Options);

/// <summary>BR-23.2's route discovery, over the in-memory feed.</summary>
public interface ITransitRouting
{
    RoutingResult Options(double fromLat, double fromLng, double toLat, double toLng);
}

/// <summary>
/// Direct routes first, then one-transfer options (AL-18, BR-23.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>"Direct" is exactly BR-23.2's sentence</b>: a single route's stop sequence covers a halt near
/// the origin <em>before</em> a halt near the destination, both within the halt radius. Nothing
/// about time of day enters into it, which is what lets the answer be computed from patterns.
/// </para>
/// <para>
/// <b>Ordering is fewest stops, then shortest walk — and deliberately not "soonest departure".</b>
/// BR-23.2 asks for "fewest stops/soonest departure", and this build cannot honour the second half:
/// <c>server_db_schema</c> §18c mirrors five GTFS tables and <b>none of them is
/// <c>calendar</c>/<c>calendar_dates</c></b>, so a trip's departure time is readable but whether
/// that trip runs today is not. Ordering on a departure the service cannot validate would put a
/// Sunday-only working at the top of a Tuesday morning list. Raised as a gap in the C056 handoff;
/// the durations that <em>are</em> reported come from the pattern's own arrival offsets, which are
/// service-day independent.
/// </para>
/// <para>
/// <b>One transfer, not two.</b> BR-23.2 says "≥ 1 transfer" and lists them below direct options;
/// two-transfer search over a national feed is a different algorithm (RAPTOR or a transfer graph)
/// and a different latency budget. Named in the handoff rather than half-built.
/// </para>
/// </remarks>
internal sealed class TransitRouting : ITransitRouting
{
    private readonly IGtfsFeedCache _cache;
    private readonly TransitOptions _options;

    public TransitRouting(IGtfsFeedCache cache, IOptions<TransitOptions> options)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public RoutingResult Options(double fromLat, double fromLng, double toLat, double toLng)
    {
        var feed = _cache.Current;

        if (!feed.IsActive)
        {
            return new RoutingResult(HasActiveFeed: false, FeedVersion: null, Options: []);
        }

        var origins = feed.StopsNear(fromLat, fromLng, _options.HaltRadiusM, _options.MaxHaltsPerEnd);
        var destinations = feed.StopsNear(toLat, toLng, _options.HaltRadiusM, _options.MaxHaltsPerEnd);

        var direct = Direct(feed, origins, destinations);

        var transfers = _options.TransferOptionsEnabled
            ? Transfers(feed, origins, destinations, direct)
            : [];

        // Direct first, whole, then transfers — BR-23.2 puts transfer options "below" direct ones,
        // and the cap is applied to the concatenation so a corridor with many direct routes never
        // loses one to a transfer.
        return new RoutingResult(
            HasActiveFeed: true,
            feed.FeedInfoVersion,
            [.. direct.Concat(transfers).Take(_options.MaxOptions)]);
    }

    /// <summary>Every route whose own sequence covers origin → destination.</summary>
    private static List<RouteOption> Direct(
        GtfsFeed feed, IReadOnlyList<NearbyStop> origins, IReadOnlyList<NearbyStop> destinations)
    {
        var best = new Dictionary<string, RouteOption>(StringComparer.Ordinal);

        foreach (var origin in origins)
        {
            foreach (var pattern in feed.PatternsAt(origin.Stop.StopId))
            {
                var boardAt = pattern.PositionOf(origin.Stop.StopId);

                foreach (var destination in destinations)
                {
                    var alightAt = pattern.PositionOf(destination.Stop.StopId);

                    if (alightAt <= boardAt)
                    {
                        continue;
                    }

                    var option = new RouteOption(
                        IsDirect: true,
                        [Leg(pattern, origin, destination, boardAt, alightAt)]);

                    // BR-23.2 asks for all direct ROUTES, and a route with three patterns over the
                    // same corridor is one answer to the passenger — the shortest ride on it.
                    if (!best.TryGetValue(pattern.RouteId, out var existing) || Better(option, existing))
                    {
                        best[pattern.RouteId] = option;
                    }
                }
            }
        }

        return [.. best.Values.OrderBy(option => option.StopsTravelled).ThenBy(option => option.WalkingDistanceM)];
    }

    /// <summary>
    /// Rides with one interchange: a pattern from the origin and a pattern to the destination that
    /// share a halt, in the right order on both.
    /// </summary>
    /// <remarks>
    /// Keyed by the pair of routes rather than by the interchange, so a passenger is offered
    /// "138 then 154" once, at its best interchange, instead of once per halt the two share.
    /// </remarks>
    private List<RouteOption> Transfers(
        GtfsFeed feed,
        IReadOnlyList<NearbyStop> origins,
        IReadOnlyList<NearbyStop> destinations,
        List<RouteOption> direct)
    {
        var alreadyDirect = direct.Select(option => option.Legs[0].Pattern.RouteId)
            .ToHashSet(StringComparer.Ordinal);

        var best = new Dictionary<(string First, string Second), RouteOption>();

        // Where each inbound pattern can drop somebody who wants the destination, so the search is
        // "does a first leg reach any of these" rather than a scan over every halt in the feed.
        var arrivals = new Dictionary<string, List<(RoutePattern Pattern, NearbyStop Destination, int AlightAt)>>(
            StringComparer.Ordinal);

        foreach (var destination in destinations)
        {
            foreach (var pattern in feed.PatternsAt(destination.Stop.StopId))
            {
                var alightAt = pattern.PositionOf(destination.Stop.StopId);

                for (var index = 0; index < alightAt; index++)
                {
                    if (!arrivals.TryGetValue(pattern.StopIds[index], out var list))
                    {
                        arrivals[pattern.StopIds[index]] = list = [];
                    }

                    list.Add((pattern, destination, alightAt));
                }
            }
        }

        foreach (var origin in origins)
        {
            foreach (var first in feed.PatternsAt(origin.Stop.StopId))
            {
                var boardAt = first.PositionOf(origin.Stop.StopId);

                for (var index = boardAt + 1; index < first.StopIds.Count; index++)
                {
                    if (!arrivals.TryGetValue(first.StopIds[index], out var candidates))
                    {
                        continue;
                    }

                    var interchange = feed.Stop(first.StopIds[index]);

                    if (interchange is null)
                    {
                        continue;
                    }

                    foreach (var (second, destination, alightAt) in candidates)
                    {
                        // A "transfer" onto the route you are already on is the direct ride, and a
                        // route already offered directly needs no transfer version of itself.
                        if (string.Equals(first.RouteId, second.RouteId, StringComparison.Ordinal)
                            || alreadyDirect.Contains(first.RouteId))
                        {
                            continue;
                        }

                        var transferAt = second.PositionOf(interchange.StopId);

                        if (transferAt < 0 || transferAt >= alightAt)
                        {
                            continue;
                        }

                        var option = new RouteOption(
                            IsDirect: false,
                            [
                                Leg(first, origin, new NearbyStop(interchange, 0), boardAt, index),
                                Leg(second, new NearbyStop(interchange, 0), destination, transferAt, alightAt),
                            ]);

                        var key = (first.RouteId, second.RouteId);

                        if (!best.TryGetValue(key, out var existing) || Better(option, existing))
                        {
                            best[key] = option;
                        }
                    }
                }
            }
        }

        return [.. best.Values
            .OrderBy(option => option.StopsTravelled)
            .ThenBy(option => option.WalkingDistanceM)
            .Take(_options.MaxTransferOptions)];
    }

    private static RouteLeg Leg(
        RoutePattern pattern, NearbyStop board, NearbyStop alight, int boardAt, int alightAt) =>
        new(pattern,
            board.Stop,
            alight.Stop,
            board.DistanceM,
            alight.DistanceM,
            alightAt - boardAt,
            pattern.DurationBetween(boardAt, alightAt));

    /// <summary>Fewest stops, then the shortest walk. The same order the list is sorted in.</summary>
    private static bool Better(RouteOption candidate, RouteOption incumbent) =>
        candidate.StopsTravelled < incumbent.StopsTravelled
        || (candidate.StopsTravelled == incumbent.StopsTravelled
            && candidate.WalkingDistanceM < incumbent.WalkingDistanceM);
}
