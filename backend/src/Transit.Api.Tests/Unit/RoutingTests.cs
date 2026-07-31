using MageRide.Transit.Configuration;
using MageRide.Transit.Domain;
using MageRide.Transit.Feed;
using MageRide.Transit.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Tests.Unit;

/// <summary>Holds a feed handed to it, so the matcher can be driven without a database.</summary>
internal sealed class StaticFeedCache(GtfsFeed feed) : IGtfsFeedCache
{
    public GtfsFeed Current { get; } = feed;

    public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// BR-23.2's rule, at the level it is written: a single route's sequence covering origin before
/// destination.
/// </summary>
public sealed class RoutingTests
{
    // A straight line east along one degree of longitude at Colombo's latitude, ~1.1 km apart.
    private static readonly TransitStop A = new("A", "A", 6.90, 79.90);
    private static readonly TransitStop B = new("B", "B", 6.90, 79.91);
    private static readonly TransitStop C = new("C", "C", 6.90, 79.92);
    private static readonly TransitStop D = new("D", "D", 6.90, 79.93);

    /// <summary>A halt off the line, reachable only by changing at C.</summary>
    private static readonly TransitStop E = new("E", "E", 6.91, 79.92);

    private static ITransitRouting Build(GtfsFeed feed, Action<TransitOptions>? configure = null)
    {
        var options = new TransitOptions();

        configure?.Invoke(options);

        return new TransitRouting(new StaticFeedCache(feed), Options.Create(options));
    }

    private static GtfsFeed Feed(params RoutePattern[] patterns) => new(
        Guid.NewGuid(),
        "test",
        [A, B, C, D, E],
        [.. patterns.Select(pattern => pattern.RouteId).Distinct(StringComparer.Ordinal)
            .Select(routeId => new TransitRoute(routeId, routeId, routeId + " long", "SLTB", 3))],
        patterns,
        []);

    private static RoutePattern Pattern(string routeId, string[] stops, int[]? minutes = null) =>
        new(routeId, "shape-" + routeId, null, 0, stops,
            minutes is null ? [] : [.. minutes.Select(minute => minute * 60)]);

    [Fact]
    public void A_route_covering_origin_before_destination_is_direct()
    {
        var result = Build(Feed(Pattern("R1", ["A", "B", "C", "D"]))).Options(6.90, 79.90, 6.90, 79.93);

        var option = Assert.Single(result.Options);

        Assert.True(option.IsDirect);
        Assert.Equal("A", option.Legs[0].BoardStop.StopId);
        Assert.Equal("D", option.Legs[0].AlightStop.StopId);
        Assert.Equal(3, option.Legs[0].StopsTravelled);
    }

    [Fact]
    public void The_order_of_the_halts_on_the_route_is_the_whole_rule()
    {
        // A route that passes both halts but reaches the destination FIRST is not a way to get
        // there. This is the "before" in BR-23.2, and it is the difference between a matcher and
        // a set intersection.
        var result = Build(Feed(Pattern("R1", ["D", "C", "B", "A"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Empty(result.Options);
        Assert.True(result.HasActiveFeed);
    }

    [Fact]
    public void Both_directions_of_a_route_are_matched_independently()
    {
        var feed = Feed(Pattern("R1", ["A", "B", "C", "D"]), Pattern("R1", ["D", "C", "B", "A"]));

        Assert.Single(Build(feed).Options(6.90, 79.90, 6.90, 79.93).Options);
        Assert.Single(Build(feed).Options(6.90, 79.93, 6.90, 79.90).Options);
    }

    [Fact]
    public void A_short_turn_pattern_does_not_claim_the_full_corridor()
    {
        // The reason patterns are distinct sequences rather than one representative per route: a
        // working that terminates early must not answer for a destination it never reaches.
        var result = Build(Feed(Pattern("R1", ["A", "B", "C"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Empty(result.Options);
    }

    [Fact]
    public void One_route_with_several_patterns_is_one_option()
    {
        // BR-23.2 asks for all direct ROUTES. Three workings over the same corridor are one answer
        // to a passenger, and it is the shortest ride on it.
        var feed = Feed(
            Pattern("R1", ["A", "B", "C", "D"]),
            Pattern("R1", ["A", "C", "D"]),
            Pattern("R1", ["A", "B", "C", "D"]));

        var option = Assert.Single(Build(feed).Options(6.90, 79.90, 6.90, 79.93).Options);

        Assert.Equal(2, option.Legs[0].StopsTravelled);
    }

    [Fact]
    public void Every_direct_route_is_returned_not_just_the_best_one()
    {
        // "ALL direct public-transport routes" (BR-23.2). A shortest-path answer would drop the
        // route the passenger actually prefers.
        var result = Build(Feed(
            Pattern("R1", ["A", "B", "C", "D"]),
            Pattern("R2", ["A", "D"]),
            Pattern("R3", ["A", "C", "D"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Equal(3, result.Options.Count);
        Assert.All(result.Options, option => Assert.True(option.IsDirect));
    }

    [Fact]
    public void Direct_options_are_ordered_by_fewest_stops()
    {
        var result = Build(Feed(
            Pattern("R1", ["A", "B", "C", "D"]),
            Pattern("R2", ["A", "D"]),
            Pattern("R3", ["A", "C", "D"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Equal(["R2", "R3", "R1"], result.Options.Select(option => option.Legs[0].Pattern.RouteId));
    }

    [Fact]
    public void A_halt_outside_the_radius_is_not_reachable()
    {
        // The 400 m halt radius is what decides whether a corridor has a route at all (BR-23.2),
        // so it is asserted rather than assumed: the origin here is a kilometre from every halt.
        var result = Build(Feed(Pattern("R1", ["A", "B", "C", "D"]))).Options(6.93, 79.90, 6.90, 79.93);

        Assert.Empty(result.Options);
    }

    [Fact]
    public void The_radius_is_a_setting_and_widening_it_reaches_further()
    {
        var feed = Feed(Pattern("R1", ["A", "B", "C", "D"]));

        Assert.Empty(Build(feed).Options(6.905, 79.90, 6.90, 79.93).Options);
        Assert.Single(Build(feed, options => options.HaltRadiusM = 1000).Options(6.905, 79.90, 6.90, 79.93).Options);
    }

    [Fact]
    public void A_transfer_option_is_offered_when_no_route_runs_the_whole_way()
    {
        var result = Build(Feed(
            Pattern("R1", ["A", "B", "C"]),
            Pattern("R2", ["C", "E"]))).Options(6.90, 79.90, 6.91, 79.92);

        var option = Assert.Single(result.Options);

        Assert.False(option.IsDirect);
        Assert.Equal(2, option.Legs.Count);
        Assert.Equal("C", option.Legs[0].AlightStop.StopId);
        Assert.Equal("C", option.Legs[1].BoardStop.StopId);
    }

    [Fact]
    public void Transfer_options_are_listed_below_direct_ones()
    {
        // BR-23.2: "Transit options (≥1 transfer) are computed and listed BELOW direct options."
        var result = Build(Feed(
            Pattern("R1", ["A", "B", "C", "E"]),
            Pattern("R2", ["A", "B", "C"]),
            Pattern("R3", ["C", "E"]))).Options(6.90, 79.90, 6.91, 79.92);

        Assert.True(result.Options[0].IsDirect);
        Assert.Contains(result.Options, option => !option.IsDirect);
        Assert.True(result.Options.TakeWhile(option => option.IsDirect).Count()
                    == result.Options.Count(option => option.IsDirect));
    }

    [Fact]
    public void A_route_already_offered_directly_is_not_offered_again_with_a_transfer()
    {
        var result = Build(Feed(
            Pattern("R1", ["A", "B", "C", "E"]),
            Pattern("R2", ["C", "E"]))).Options(6.90, 79.90, 6.91, 79.92);

        Assert.Single(result.Options);
        Assert.True(result.Options[0].IsDirect);
    }

    [Fact]
    public void Transfers_can_be_switched_off_without_touching_direct_matching()
    {
        var feed = Feed(Pattern("R1", ["A", "B", "C"]), Pattern("R2", ["C", "E"]));

        Assert.Empty(Build(feed, options => options.TransferOptionsEnabled = false)
            .Options(6.90, 79.90, 6.91, 79.92).Options);

        Assert.Single(Build(feed, options => options.TransferOptionsEnabled = false)
            .Options(6.90, 79.90, 6.90, 79.92).Options);
    }

    [Fact]
    public void An_in_vehicle_duration_is_reported_when_the_feed_carried_times()
    {
        var result = Build(Feed(Pattern("R1", ["A", "B", "C", "D"], [0, 10, 30, 55])))
            .Options(6.90, 79.90, 6.90, 79.93);

        Assert.Equal(55 * 60, Assert.Single(result.Options).TotalDurationSec);
    }

    [Fact]
    public void A_feed_with_no_times_still_routes_and_simply_has_no_duration()
    {
        // GTFS lets a feed omit arrival and departure times; a routable feed without them is
        // normal, and inventing a duration would be worse than admitting there is none.
        var result = Build(Feed(Pattern("R1", ["A", "B", "C", "D"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Null(Assert.Single(result.Options).TotalDurationSec);
    }

    [Fact]
    public void With_no_active_feed_the_answer_says_so_rather_than_being_empty()
    {
        // AL-55's safety net. "No bus goes there" and "we cannot tell" are different answers, and
        // SCR-PA-009 renders them differently — the second keeps live buses and private tiers and
        // hides route matching.
        var result = Build(GtfsFeed.Empty).Options(6.90, 79.90, 6.90, 79.93);

        Assert.False(result.HasActiveFeed);
        Assert.Empty(result.Options);
        Assert.Null(result.FeedVersion);
    }

    [Fact]
    public void A_live_feed_with_no_route_on_the_corridor_is_a_different_answer()
    {
        var result = Build(Feed(Pattern("R1", ["A", "B"]))).Options(6.90, 79.92, 6.90, 79.93);

        Assert.True(result.HasActiveFeed);
        Assert.Empty(result.Options);
    }

    [Fact]
    public void A_loop_route_is_boarded_at_the_first_chance()
    {
        // Taking the later occurrence would invent a ride around the whole loop that nobody would
        // choose.
        var result = Build(Feed(Pattern("R1", ["A", "B", "C", "A", "D"]))).Options(6.90, 79.90, 6.90, 79.93);

        Assert.Equal(4, Assert.Single(result.Options).Legs[0].StopsTravelled);
    }
}
